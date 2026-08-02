using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The color and the tint slot for a captured block. This is the one part of the capture
/// that is not the same on both sides. The color of a block comes from
/// <c>capi.BlockTextureAtlas</c>, and a dedicated server does not have that atlas. Read
/// DESIGN.md section 10.4.
/// </summary>
/// <param name="blockId">The live block id from the capture.</param>
/// <param name="cx">The X of the chunk column, for the sample position.</param>
/// <param name="cz">The Z of the chunk column, for the sample position.</param>
/// <param name="sampleY">The Y of the top of the run, for the sample position.</param>
public delegate (int Color, byte TintSlot) LodPaletteDescriber(int blockId, int cx, int cz, int sampleY);

/// <summary>The live tint that applies to a block. A server has no tints, and it answers
/// 0.</summary>
public delegate byte LodTintSlotResolver(Block block);

/// <summary>
/// All the work between "a chunk column arrived" and "a section is on the disk". That work
/// is the capture scheduling, the palette registration, the mip propagation and the
/// persistence.
///
/// This class owns each change to the <see cref="LodWorld"/>. The worker thread reads
/// snapshots only, and those snapshots do not change.
///
/// This class is the same on both sides, on purpose. The client drives it from `ChunkDirty`,
/// and it also draws from the same LodWorld. A server drives it from `ChunkColumnLoaded`, and
/// it never draws.
///
/// Two things differ between the sides: which chunks arrive, and the color of a palette
/// entry. Thus the caller supplies those two things. There is no branch on the side. One copy
/// of this class for each side would become different over time, and the mip rules and the
/// persistence rules are exactly the rules that must stay the same.
///
/// CAUTION: Call each method here from the thread that owns the world, which is the game
/// tick.
/// </summary>
public class LodPipeline
{
    const int CaptureSchedulesPerTick = 8;
    const int CaptureAppliesPerTick = 8;
    const int PropagationsPerTick = 3;
    const int SectionSavesPerTick = 6;
    const int MaxWorkerCaptureBacklog = 24;
    const int ChunkSize = GlobalConstants.ChunkSize;

    /// <summary>
    /// A snapshot in the queue holds a copy of the run data of its section. Thus a queue
    /// with no limit is a memory leak with no limit, if the disk is too slow. Above this
    /// depth, a section stays dirty, and thus it stays in RAM. The mod tries again later.
    /// </summary>
    const int MaxStorageBacklog = 256;

    readonly ICoreAPI api;
    readonly ILogger logger;
    readonly LodPaletteDescriber describePalette;

    /// <summary>The tint slot for a block. A server has no tints, and it leaves this at
    /// 0.</summary>
    readonly LodTintSlotResolver tintSlotFor;

    public LodWorld World { get; }
    public LodWorker Worker { get; }

    LodStore? store;
    LodStorageThread? storageThread;

    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    /// <summary>
    /// This is false until the store exists. Nothing can touch a section before that
    /// moment. A capture that goes into a new empty section hides the stored row, and later
    /// it overwrites that row. The column queue holds the work until this value becomes
    /// true.
    /// </summary>
    public bool Active { get; private set; }

    public bool Persisting => store != null;
    public int CachedSectionsLoaded { get; private set; }
    public int ColumnsCaptured { get; private set; }
    public int PendingColumns => pendingColumns.Count;
    public string? DbPath { get; private set; }

    // The storage cost on the main thread. This measurement decided whether a move of the
    // SQLite work to a background thread is worth its complexity.
    readonly System.Diagnostics.Stopwatch storageClock = new();
    public double SaveMsMax { get; private set; }
    public double SaveMsTotal { get; private set; }
    public int SaveCalls { get; private set; }
    public double LoadMsMax { get; private set; }
    public double LoadMsTotal { get; private set; }
    public int LoadCalls { get; private set; }
    public LodStorageThread? StorageThread => storageThread;

    int tickCounter;

    public LodPipeline(ICoreAPI api, ILogger logger, LodPaletteDescriber describePalette,
        LodTintSlotResolver? tintSlotFor = null)
    {
        this.api = api;
        this.logger = logger;
        this.describePalette = describePalette;
        this.tintSlotFor = tintSlotFor ?? (_ => 0);
        World = new LodWorld();
        Worker = new LodWorker();
        Remote = new LodRemoteKeySet(World);
    }

    public void ResetStorageStats()
    {
        SaveMsMax = SaveMsTotal = LoadMsMax = LoadMsTotal = 0;
        SaveCalls = LoadCalls = 0;
    }

    /// <summary>Record that a chunk column needs a capture. Any thread can call
    /// this.</summary>
    public void QueueColumn(int cx, int cz)
    {
        long key = ((long)cz << 32) | (uint)cx;
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    /// <summary>
    /// Open the LOD cache for the current world, or make it, and take its key set.
    ///
    /// A failure to open is not fatal. The capture and the rendering both operate without
    /// persistence.
    /// </summary>
    /// <param name="subdir">The directory for the cache file, relative to ModData.</param>
    /// <param name="suffix">
    /// The mod adds this to the world key. It is a second protection after a real defect.
    /// The client and the server find the same ModData path from the same savegame
    /// identifier. Thus in one process they opened one file through two connections.
    /// Different names mean that this class of mistake cannot damage a cache, even if the
    /// two sides exist together again.
    /// </param>
    public void Open(string subdir, string suffix = "")
    {
        string worldKey = api.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + api.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = api.GetOrCreateDataPath(subdir);
        string dbPath = Path.Combine(dir, worldKey + suffix + ".db");

        var newStore = new LodStore(logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            Active = true; // no persistence this session; everything else still works
            return;
        }

        store = newStore;
        DbPath = dbPath;
        newStore.ClassifyBlock = blockId =>
        {
            Block? block = blockId > 0 ? api.World.GetBlock(blockId) : null;
            return block == null ? ((byte)0, (byte)0) : (LodBlockPolicy.FlagsFor(block), tintSlotFor(block));
        };
        storageThread = new LodStorageThread(newStore);

        // Background loads for the render path. The loader runs on the storage thread. The
        // world thread installs the results, in Tick.
        storageThread.SetLoader(key => newStore.LoadSection(
            LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World, resolveBlockIds: false));
        // This is routing, and not only loading. The server offers a key that the local
        // disk never held. That key returns nothing from the store, and it goes into
        // LoadFailed, which is permanent. Thus such a key goes to the network instead. The
        // LoadsInFlight record of the quadtree covers both paths without a change.
        World.RequestAsyncLoad = key =>
        {
            if (!Remote.WantFromRemote(key)) storageThread?.RequestLoad(key);
        };

        World.LoadFromStore = key =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LodSection? loaded = store?.LoadSection(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World);
            double ms = clock.Elapsed.TotalMilliseconds;
            LoadCalls++;
            LoadMsTotal += ms;
            if (ms > LoadMsMax) LoadMsMax = ms;
            return loaded;
        };
        CachedSectionsLoaded = store.LoadAllKeys((level, sx, sz, applyToParent) =>
        {
            Remote.AddLocalKey(LodWorld.SectionKey(level, sx, sz));
            World.InstallStoredKey(level, sx, sz, applyToParent);
        });
        Active = true;
        logger.Notification("LOD cache: {0}", dbPath);
    }

    /// <summary>
    /// The stored blob for a key, without a parse, to give over the network.
    ///
    /// The result is null when the key is not on the disk. This includes a key that is
    /// resident in RAM but that the mod did not write yet. For that reason the caller treats
    /// a miss as "ask again later", and not as "gone".
    /// </summary>
    public byte[]? LoadBlob(long key) => store?.LoadBlob(
        LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key));

    /// <summary>
    /// Take a section that arrived from a source other than the local disk.
    ///
    /// The result is false when the key has local data already. The local data wins. The
    /// capture of the client is what the client observed, and it includes the edits that the
    /// client saw. Read DESIGN.md section 10.5.
    /// </summary>
    public bool InstallForeignBlob(long key, byte[] blob, Action<LodSection>? recolor)
    {
        if (store == null || blob.Length == 0) return false;
        if (World.Sections.ContainsKey(key)) return false;

        LodSection? section = store.DeserializeForeign(blob, api.World);
        if (section == null) return false;

        // The sender had no texture atlas, thus each palette color is 0. Give them a color
        // before anything draws the section.
        recolor?.Invoke(section);
        section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);

        World.InstallLoaded(key, section);
        // Store it. A fetch of 45.9 KB for each section, in each session, is not
        // acceptable. Thus a section from the network goes into the local cache, as each
        // other section does.
        World.MarkChanged(key);
        ForeignSectionsInstalled++;
        return true;
    }

    public int ForeignSectionsInstalled { get; private set; }

    /// <summary>
    /// The keys that a remote source can supply, and the keys that the view waits for.
    ///
    /// This is its own class, thus a test can reach the set logic without a game API. Read
    /// LodRemoteKeySet.
    ///
    /// The field is private, and the members below are the only way to it. The pipeline is
    /// the one interface that the mod system uses. Two ways into the same state is how two
    /// parts become different.
    /// </summary>
    readonly LodRemoteKeySet Remote;

    /// <inheritdoc cref="LodRemoteKeySet.RemoteOnly"/>
    public HashSet<long> RemoteOnly => Remote.RemoteOnly;

    /// <inheritdoc cref="LodRemoteKeySet.MarkUnavailable"/>
    public void MarkRemoteUnavailable(long key) => Remote.MarkUnavailable(key);

    /// <inheritdoc cref="LodRemoteKeySet.AddRemoteKeys"/>
    public int AddRemoteKeys(IEnumerable<long> keys) => Remote.AddRemoteKeys(keys);

    /// <inheritdoc cref="LodRemoteKeySet.Wanted"/>
    public long[] RemoteWanted() => Remote.Wanted();

    /// <inheritdoc cref="LodRemoteKeySet.MarkRequested"/>
    public void MarkRemoteRequested(IEnumerable<long> sent) => Remote.MarkRequested(sent);

    /// <summary>One step of the full pipeline. Call this one time in each game
    /// tick.</summary>
    public void Tick()
    {
        if (!Active) return;

        InstallLoadedSections();
        ScheduleCaptures();
        ApplyCaptureResults();
        World.ProcessPropagation(PropagationsPerTick);
        SaveSomeDirtySections(SectionSavesPerTick);
        tickCounter++;
    }

    /// <summary>
    /// Remove cold sections from RAM, around an anchor point.
    ///
    /// This is useful only after a load from the disk is possible. Call it approximately
    /// every 5 seconds, because the sweep walks each resident section.
    /// </summary>
    public bool MaybeEvictAround(double x, double z)
    {
        if (tickCounter % 100 != 0 || World.LoadFromStore == null) return false;
        World.EvictColdSections(x, z, 50);
        return tickCounter % 1200 == 0;
    }

    /// <summary>
    /// Take the sections that the storage thread finished reading. This is cheap. The
    /// decompress occurred on the other thread already, and this method only publishes the
    /// reference.
    /// </summary>
    void InstallLoadedSections()
    {
        if (storageThread == null) return;

        while (storageThread.LoadResults.TryDequeue(out (long Key, LodSection? Section) result))
        {
            // The mod finds the palette ids here, on the world thread, before anything
            // reads them. The storage thread must not touch the block registry.
            if (result.Section != null && store != null)
            {
                store.ResolvePendingPalette(result.Section, api.World);
                // Reclassify took the flags from the live blocks again. Thus this call
                // removes the runs for anything that is no longer terrain, such as fire or
                // a meta block. It does this for a section that the mod captured under an
                // older policy, and the player does not explore that area again.
                result.Section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
            }
            World.InstallLoaded(result.Key, result.Section);
        }
    }

    // ---- Capture scheduling. The world thread collects the references, and the worker
    // reads the blocks. ----

    void ScheduleCaptures()
    {
        if (Worker.PendingCaptures >= MaxWorkerCaptureBacklog) return;

        int chunkYCount = api.World.BlockAccessor.MapSizeY / ChunkSize;

        for (int n = 0; n < CaptureSchedulesPerTick && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            int cx = (int)(key & 0xFFFFFFFF);
            int cz = (int)(key >> 32);

            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(cx, cz);
            ushort[]? rainMap = mapChunk?.RainHeightMap;
            if (rainMap == null) continue;

            var chunks = new IWorldChunk?[chunkYCount];
            for (int cy = 0; cy < chunkYCount; cy++)
            {
                chunks[cy] = api.World.BlockAccessor.GetChunk(cx, cy, cz);
            }

            Worker.EnqueueCapture(new CaptureJob
            {
                Cx = cx,
                Cz = cz,
                Chunks = chunks,
                RainMap = (ushort[])rainMap.Clone(),
            });
        }
    }

    // ---- Apply the capture results: block ids to the palette ids of a section ----

    void ApplyCaptureResults()
    {
        for (int n = 0; n < CaptureAppliesPerTick && Worker.CaptureResults.TryDequeue(out CaptureResult? result); n++)
        {
            LodSection section = World.GetOrCreateSection(result.SectionKey);

            var pidByBlockId = new Dictionary<int, int>();
            ulong[]?[] batch = result.RunsByColumn;

            for (int col = 0; col < batch.Length; col++)
            {
                ulong[]? runs = batch[col];
                if (runs == null) continue;

                int kept = 0;
                for (int i = 0; i < runs.Length; i++)
                {
                    int blockId = LodSection.RunPaletteId(runs[i]); // raw block id from capture
                    if (!pidByBlockId.TryGetValue(blockId, out int pid))
                    {
                        pid = RegisterPaletteEntry(section, result, blockId, LodSection.RunYTop(runs[i]));
                        pidByBlockId[blockId] = pid;
                    }

                    // Decorative ground cover never becomes terrain. Without this rule, a
                    // flower is a solid, pale grey cube of 1 block.
                    if ((section.Palette[pid].Flags & LodPaletteEntry.FlagSkip) != 0) continue;

                    runs[kept++] = LodSection.PackRun(pid, LodSection.RunYTop(runs[i]), LodSection.RunYBottom(runs[i]));
                }

                if (kept != runs.Length) batch[col] = runs[..kept];
            }

            ColumnsCaptured++;
            if (section.ReplaceColumns(batch))
            {
                World.MarkChanged(result.SectionKey);
            }
        }
    }

    int RegisterPaletteEntry(LodSection section, CaptureResult result, int blockId, int sampleY)
    {
        Block block = api.World.Blocks[blockId];
        (int color, byte tintSlot) = describePalette(blockId, result.Cx, result.Cz, sampleY);
        return section.FindOrAddPaletteEntry(blockId, color, LodBlockPolicy.FlagsFor(block), tintSlot);
    }

    // ---- Persistence ----

    void SaveSomeDirtySections(int budget)
    {
        if (store == null || World.SaveDirty.Count == 0) return;
        if (storageThread != null && storageThread.Backlog >= MaxStorageBacklog) return;

        storageClock.Restart();
        List<long>? saved = null;
        foreach (long key in World.SaveDirty)
        {
            if (World.Sections.TryGetValue(key, out LodSection? section))
            {
                // Copy on this thread, because the section continues to change. Then
                // compress and write on the storage thread.
                var snap = LodSaveSnapshot.Of(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                    section, api.World, World.MipDirty.Contains(key));
                storageThread?.Enqueue(snap);
            }
            (saved ??= new List<long>()).Add(key);
            if (--budget <= 0) break;
        }
        if (saved != null) foreach (long key in saved) World.SaveDirty.Remove(key);

        double ms = storageClock.Elapsed.TotalMilliseconds;
        SaveCalls++;
        SaveMsTotal += ms;
        if (ms > SaveMsMax) SaveMsMax = ms;
    }

    /// <summary>
    /// Write the remaining data and close the cache.
    ///
    /// The order is important. Put each item into the queue. Let the writer finish. Stop the
    /// thread. Only then close the connection that the writer used.
    /// </summary>
    public void Close()
    {
        Active = false;
        World.LoadFromStore = null;
        World.RequestAsyncLoad = null;

        if (store != null)
        {
            SaveSomeDirtySections(int.MaxValue);
            if (storageThread != null)
            {
                storageThread.Drain();
                if (storageThread.Backlog > 0)
                {
                    logger.Warning("Storage drain timed out with {0} sections unwritten", storageThread.Backlog);
                }
                storageThread.Dispose();
                storageThread = null;
            }
            store.Close();
            store.Dispose();
            store = null;
        }

        queuedColumns.Clear();
        pendingColumns.Clear();
        Remote.Clear();
        // The results for the world that the player leaves must not go into the next
        // world.
        while (Worker.CaptureResults.TryDequeue(out _)) { }
        World.Clear();
        CachedSectionsLoaded = 0;
        ColumnsCaptured = 0;
        DbPath = null;
    }

    public void Dispose()
    {
        storageThread?.Drain();
        storageThread?.Dispose();
        storageThread = null;
        store?.Dispose();
        store = null;
        Worker.Dispose();
    }
}
