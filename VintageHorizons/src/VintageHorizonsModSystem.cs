using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

public class VintageHorizonsConfig
{
    /// <summary>0 = unlimited.</summary>
    public int FarViewDistanceCap = 0;
}

/// <summary>
/// Entry point and main-thread coordinator. Client-only: builds LODs purely from
/// chunk data the client receives, so it works on any server. Chunk columns are
/// captured into RLE sections on a worker thread; this class owns all mutation of
/// the LodWorld (palette remaps, applies, mip propagation, saves) on game ticks.
/// See DESIGN.md at the repo root.
/// </summary>
public class VintageHorizonsModSystem : ModSystem
{
    const int CaptureSchedulesPerTick = 8;
    const int CaptureAppliesPerTick = 8;
    const int PropagationsPerTick = 3;
    const int SectionSavesPerTick = 6;
    const int MaxWorkerCaptureBacklog = 24;
    const int ChunkSize = GlobalConstants.ChunkSize;

    ICoreClientAPI capi = null!;
    LodWorld world = null!;
    LodWorker worker = null!;
    LodTerrainRenderer renderer = null!;
    LodStore? store;
    LodStorageThread? storageThread;
    long tickListenerId;
    int cachedSectionsLoaded;
    int columnsCaptured;

    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    // Dev auto-explore (unattended stress testing): teleport along an expanding
    // spiral so fresh chunks stream through the pipeline continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        VintageHorizonsConfig config;
        try
        {
            config = capi.LoadModConfig<VintageHorizonsConfig>("vintagehorizons.json") ?? new VintageHorizonsConfig();
        }
        catch
        {
            config = new VintageHorizonsConfig();
        }

        world = new LodWorld();
        worker = new LodWorker();
        renderer = new LodTerrainRenderer(capi, world, worker)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            FarViewDistanceCap = config.FarViewDistanceCap,
        };

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);

        RegisterCommands();

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        long key = ((long)chunkCoord.Z << 32) | (uint)chunkCoord.X;
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    int tickCounter;
    bool pipelineActive;

    void OnGameTick(float dt)
    {
        // Nothing may touch sections before the store loader exists: applying a
        // capture to a freshly-created empty section would shadow (and later
        // overwrite) the stored row. The column queue holds work until then.
        if (!pipelineActive) return;

        InstallLoadedSections();
        ScheduleCaptures();
        ApplyCaptureResults();
        world.ProcessPropagation(PropagationsPerTick);
        SaveSomeDirtySections(SectionSavesPerTick);

        // RAM eviction sweep every ~5s; only meaningful when reload-from-disk exists.
        if (++tickCounter % 100 == 0 && world.LoadFromStore != null)
        {
            var pos = capi.World.Player.Entity.Pos;
            world.EvictColdSections(pos.X, pos.Z, 50);
            if (tickCounter % 1200 == 0)
            {
                Mod.Logger.Notification("Evict sweep at {0},{1}: checked {2}, pinned {3}, cold {4}, total evicted {5}",
                    (int)pos.X, (int)pos.Z, world.LastSweepChecked, world.LastSweepPinned,
                    world.LastSweepCold, world.EvictedSectionsTotal);
            }
        }
    }

    /// <summary>
    /// Adopt sections the storage thread finished reading. Cheap: the decompress
    /// already happened off-thread, this only publishes the reference.
    /// </summary>
    void InstallLoadedSections()
    {
        if (storageThread == null) return;

        while (storageThread.LoadResults.TryDequeue(out (long Key, LodSection? Section) result))
        {
            // Palette ids are resolved here, on the main thread, before anything can
            // read them: the storage thread must not touch the block registry.
            if (result.Section != null && store != null)
            {
                store.ResolvePendingPalette(result.Section, capi.World);
            }
            world.InstallLoaded(result.Key, result.Section);
        }
    }

    // ---- Capture scheduling (main thread gathers refs, worker reads blocks) ----

    void ScheduleCaptures()
    {
        if (worker.PendingCaptures >= MaxWorkerCaptureBacklog) return;

        int chunkYCount = capi.World.BlockAccessor.MapSizeY / ChunkSize;

        for (int n = 0; n < CaptureSchedulesPerTick && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            int cx = (int)(key & 0xFFFFFFFF);
            int cz = (int)(key >> 32);

            IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(cx, cz);
            ushort[]? rainMap = mapChunk?.RainHeightMap;
            if (rainMap == null) continue;

            var chunks = new IWorldChunk?[chunkYCount];
            for (int cy = 0; cy < chunkYCount; cy++)
            {
                chunks[cy] = capi.World.BlockAccessor.GetChunk(cx, cy, cz);
            }

            worker.EnqueueCapture(new CaptureJob
            {
                Cx = cx,
                Cz = cz,
                Chunks = chunks,
                RainMap = (ushort[])rainMap.Clone(),
            });
        }
    }

    // ---- Applying capture results: block ids → section palette ids ----

    void ApplyCaptureResults()
    {
        for (int n = 0; n < CaptureAppliesPerTick && worker.CaptureResults.TryDequeue(out CaptureResult? result); n++)
        {
            LodSection section = world.GetOrCreateSection(result.SectionKey);

            var pidByBlockId = new Dictionary<int, int>();
            ulong[]?[] batch = result.RunsByColumn;

            for (int col = 0; col < batch.Length; col++)
            {
                ulong[]? runs = batch[col];
                if (runs == null) continue;

                for (int i = 0; i < runs.Length; i++)
                {
                    int blockId = LodSection.RunPaletteId(runs[i]); // raw block id from capture
                    if (!pidByBlockId.TryGetValue(blockId, out int pid))
                    {
                        pid = RegisterPaletteEntry(section, result, blockId, LodSection.RunYTop(runs[i]));
                        pidByBlockId[blockId] = pid;
                    }
                    runs[i] = LodSection.PackRun(pid, LodSection.RunYTop(runs[i]), LodSection.RunYBottom(runs[i]));
                }
            }

            columnsCaptured++;
            if (section.ReplaceColumns(batch))
            {
                world.MarkChanged(result.SectionKey);
            }
        }
    }

    int RegisterPaletteEntry(LodSection section, CaptureResult result, int blockId, int sampleY)
    {
        Block block = capi.World.Blocks[blockId];

        // Store the UNTINTED color; the shader applies the current season/climate tint
        // live, so cached horizons follow the calendar instead of freezing the season
        // they were captured in.
        paletteSamplePos.Set(result.Cx * ChunkSize + ChunkSize / 2, sampleY, result.Cz * ChunkSize + ChunkSize / 2);
        int color = block.GetColorWithoutTint(capi, paletteSamplePos);

        byte flags = 0;
        if (block.BlockMaterial == EnumBlockMaterial.Water
            || block.BlockMaterial == EnumBlockMaterial.Lava
            || block.BlockMaterial == EnumBlockMaterial.Ice)
        {
            flags |= LodPaletteEntry.FlagWater;
        }
        else if (block.SeasonColorMapResolved != null)
        {
            flags |= LodPaletteEntry.FlagTintFoliage;
        }
        else if (block.ClimateColorMapResolved != null)
        {
            flags |= LodPaletteEntry.FlagTintGrass;
        }

        return section.FindOrAddPaletteEntry(blockId, color, flags);
    }

    // ---- Persistence ----

    // Main-thread storage cost, measured to decide whether moving SQLite work to a
    // background thread is worth its complexity.
    readonly System.Diagnostics.Stopwatch storageClock = new();
    public double SaveMsMax { get; private set; }
    public double SaveMsTotal { get; private set; }
    public int SaveCalls { get; private set; }
    public double LoadMsMax { get; private set; }
    public double LoadMsTotal { get; private set; }
    public int LoadCalls { get; private set; }

    void ResetStorageStats()
    {
        SaveMsMax = SaveMsTotal = LoadMsMax = LoadMsTotal = 0;
        SaveCalls = LoadCalls = 0;
    }

    /// <summary>
    /// Queued snapshots hold copies of their section's run data, so an unbounded queue
    /// is an unbounded memory leak if the disk can't keep up. Past this depth the
    /// sections simply stay dirty (and therefore RAM-resident) and retry later.
    /// </summary>
    const int MaxStorageBacklog = 256;

    void SaveSomeDirtySections(int budget)
    {
        if (store == null || world.SaveDirty.Count == 0) return;
        if (storageThread != null && storageThread.Backlog >= MaxStorageBacklog) return;

        storageClock.Restart();
        List<long>? saved = null;
        foreach (long key in world.SaveDirty)
        {
            if (world.Sections.TryGetValue(key, out LodSection? section))
            {
                // Freeze on this thread (the section keeps mutating), compress and
                // write on the storage thread.
                var snap = LodSaveSnapshot.Of(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                    section, capi.World, world.MipDirty.Contains(key));
                storageThread?.Enqueue(snap);
            }
            (saved ??= new List<long>()).Add(key);
            if (--budget <= 0) break;
        }
        if (saved != null) foreach (long key in saved) world.SaveDirty.Remove(key);

        double ms = storageClock.Elapsed.TotalMilliseconds;
        SaveCalls++;
        SaveMsTotal += ms;
        if (ms > SaveMsMax) SaveMsMax = ms;
    }

    void OnLevelFinalize()
    {
        renderer.ApplyZFar();
        OpenLodCache();
        pipelineActive = true;

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} sections from cache{1}).",
            cachedSectionsLoaded, renderer.AutoUnpause ? ", auto-unpause on" : "");

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Dev mode: continuous telemetry for unattended runs.
        if (renderer.AutoUnpause)
        {
            capi.Event.RegisterGameTickListener(_ => LogStats("Stats"), 60000);
        }

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";
        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterCallback(_ => capi.SendChatMessage("/gamemode creative"), 10000);
            capi.Event.RegisterGameTickListener(_ => ExploreHop(), 60000);
            Mod.Logger.Notification("Auto-explore active (spiral teleports every 60s)");
        }
    }

    static readonly int ExploreHopBlocks =
        int.TryParse(Environment.GetEnvironmentVariable("VINTAGEHORIZONS_EXPLORE_HOP"), out int h) && h > 0 ? h : 350;

    void ExploreHop()
    {
        int hop = ExploreHopBlocks;

        exploreX += exploreDirX * hop;
        exploreZ += exploreDirZ * hop;

        // Square spiral: legs lengthen every second turn.
        if (++exploreStep >= exploreLeg / 2 + 1)
        {
            exploreStep = 0;
            exploreLeg++;
            (exploreDirX, exploreDirZ) = (-exploreDirZ, exploreDirX);
        }

        int y = capi.World.SeaLevel + 140;
        capi.SendChatMessage($"/tp ={(int)exploreX} {y} ={(int)exploreZ}");
    }

    bool loggedFirstCaptureError, loggedFirstMeshError, loggedFirstSaveError;

    void LogStats(string prefix)
    {
        if (!loggedFirstCaptureError && worker.FirstCaptureError != null)
        {
            loggedFirstCaptureError = true;
            Mod.Logger.Warning("First capture error was: {0}", worker.FirstCaptureError);
        }
        if (!loggedFirstMeshError && worker.FirstMeshError != null)
        {
            loggedFirstMeshError = true;
            Mod.Logger.Warning("First mesh error was: {0}", worker.FirstMeshError);
        }

        Mod.Logger.Notification(
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted), " +
            "{7} selected [{8}] minus {9} frustum-culled, {10} columns captured, {11} pending, " +
            "worker: {12} captures / {13} meshes queued / {14}+{15} errors, {16} awaiting mip, {17} render-dirty, {18} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, cachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, columnsCaptured, pendingColumns.Count, worker.PendingCaptures, worker.PendingMeshes,
            worker.CaptureErrors, worker.MeshErrors, world.MipDirty.Count, world.RenderDirty.Count,
            world.SaveDirty.Count);

        Mod.Logger.Notification(
            "  storage on main thread since last report: snapshot {0} calls, {1:0.00}ms avg, {2:0.00}ms max | " +
            "inline loads {3} calls, {4:0.00}ms avg, {5:0.00}ms max | storage thread: {6} write backlog, " +
            "{7} written, {8} write errors, {11} read, {9} async loads in flight, {10} read errors",
            SaveCalls, SaveCalls > 0 ? SaveMsTotal / SaveCalls : 0, SaveMsMax,
            LoadCalls, LoadCalls > 0 ? LoadMsTotal / LoadCalls : 0, LoadMsMax,
            storageThread?.Backlog ?? 0, storageThread?.SectionsWritten ?? 0, storageThread?.SaveErrors ?? 0,
            world.LoadsInFlight.Count, storageThread?.LoadErrors ?? 0, storageThread?.SectionsRead ?? 0);

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        ResetStorageStats();
    }

    void OpenLodCache()
    {
        string worldKey = capi.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + capi.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = capi.GetOrCreateDataPath("ModData/vintagehorizons");
        string dbPath = Path.Combine(dir, worldKey + ".db");

        var newStore = new LodStore(Mod.Logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            return; // no persistence this session; everything else still works
        }

        store = newStore;
        storageThread = new LodStorageThread(newStore);

        // Background reloads for the render path. The loader runs on the storage
        // thread; results are installed on the main thread in OnGameTick.
        storageThread.SetLoader(key => newStore.LoadSection(
            LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), capi.World, resolveBlockIds: false));
        world.RequestAsyncLoad = key => storageThread?.RequestLoad(key);

        world.LoadFromStore = key =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LodSection? loaded = store?.LoadSection(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), capi.World);
            double ms = clock.Elapsed.TotalMilliseconds;
            LoadCalls++;
            LoadMsTotal += ms;
            if (ms > LoadMsMax) LoadMsMax = ms;
            return loaded;
        };
        cachedSectionsLoaded = store.LoadAllKeys(world.InstallStoredKey);
        Mod.Logger.Notification("LOD cache: {0}", dbPath);
    }

    void OnLeaveWorld()
    {
        pipelineActive = false;
        world.LoadFromStore = null;
        world.RequestAsyncLoad = null;
        if (store != null)
        {
            // Order matters: queue everything, let the writer finish, stop the thread,
            // and only then close the connection it was writing through.
            SaveSomeDirtySections(int.MaxValue);
            if (storageThread != null)
            {
                storageThread.Drain();
                if (storageThread.Backlog > 0)
                {
                    Mod.Logger.Warning("Storage drain timed out with {0} sections unwritten", storageThread.Backlog);
                }
                storageThread.Dispose();
                storageThread = null;
            }
            store.Close();
            store.Dispose();
            store = null;
        }
        cachedSectionsLoaded = 0;
        columnsCaptured = 0;
        queuedColumns.Clear();
        while (pendingColumns.TryDequeue(out _)) { }
        while (worker.CaptureResults.TryDequeue(out _)) { }
        while (worker.MeshResults.TryDequeue(out _)) { }
        world.Clear();
        renderer.ClearMeshes();
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("vhinfo")
            .WithDescription("VintageHorizons status")
            .HandleWith(_ => TextCommandResult.Success(
                $"[VintageHorizons] sections: {world.Sections.Count} [{world.DescribeLevels()}] " +
                $"({cachedSectionsLoaded} from cache), meshes: {renderer.MeshCount}, drawn: {renderer.LastDrawCount}, " +
                $"columns captured: {columnsCaptured}, pending: {pendingColumns.Count}, " +
                $"worker: {worker.PendingCaptures}c/{worker.PendingMeshes}m, awaiting mip: {world.MipDirty.Count}, " +
                $"unsaved: {world.SaveDirty.Count}, persistence: {(store != null ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}"));

        capi.ChatCommands.Create("vhfar")
            .WithDescription("Cap VintageHorizons render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                capi.StoreModConfig(new VintageHorizonsConfig
                {
                    FarViewDistanceCap = renderer.FarViewDistanceCap,
                }, "vintagehorizons.json");
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[VintageHorizons] render distance capped at {renderer.FarViewDistanceCap} (saved)"
                    : "[VintageHorizons] render distance unlimited (saved)");
            });
    }

    public override void Dispose()
    {
        if (capi != null)
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LeaveWorld -= OnLeaveWorld;
            capi.Event.UnregisterGameTickListener(tickListenerId);
            // Stop the writer before the connection it writes through.
            storageThread?.Drain();
            storageThread?.Dispose();
            storageThread = null;
            store?.Dispose();
            worker?.Dispose();
            renderer?.Dispose();
        }
    }
}
