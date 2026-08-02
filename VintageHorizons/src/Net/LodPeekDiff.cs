using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Measures what a Terrain-pass peek produces, against two references.
///
/// EXPERIMENT A (/vhgen diff) compares a peek against a full generation of the same
/// coordinates. This is the record behind the "generated terrain is missing things"
/// caveat. The caveat used to say "no trees", which came from reading the
/// EnumWorldGenPass doc comments rather than from measurement - the same method that
/// under-reported the sweep's neighbour dependency by three rings. The passes a peek
/// skips also carry ores, worldgen structures, above-sealevel lakes, hot springs,
/// shrubs, rivulets and the snow layer, so the report lists what is actually absent
/// instead of naming one example.
///
/// EXPERIMENT B (/vhgen edittest) places a marker block, reads it back from the loaded
/// world, then peeks the same coordinate and looks for it there. This is the evidence
/// behind LodColumnMap.Classify never peeking a column that exists. The rule is
/// currently justified by the API contract - PeekChunkColumn generates "from scratch
/// without keeping it in the list of loaded chunks", so a peek and the savegame are
/// two independent generations - and by a fast-tier assertion over a HashSet. Neither
/// demonstrates that a peek really loses an edit.
///
/// Both experiments read blocks directly rather than through LodPipeline. The question
/// is what worldgen produced, so the palette remapping the capture path performs sits
/// between us and the answer. The walk is LodWorker.Capture's own idiom and works
/// identically for a peeked IServerChunk[] and a loaded one.
/// </summary>
public class LodPeekDiff
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    /// <summary>
    /// Half-width of the block of columns generated around the one being compared.
    /// Passes above Terrain need neighbours, so a lone load would under-report exactly
    /// the content this measures. 2 gives a 5x5; widen and re-run to test whether the
    /// answer moves.
    /// </summary>
    const int ReferenceBorder = 2;

    /// <summary>Blocks in the marker stack that experiment B places.</summary>
    const int MarkerHeight = 4;

    readonly ICoreServerAPI sapi;
    readonly ILogger logger;

    public LodPeekDiff(ICoreServerAPI sapi, ILogger logger)
    {
        this.sapi = sapi;
        this.logger = logger;
    }

    // ---- Reading a column, either peeked or loaded -------------------------------

    /// <summary>
    /// What one chunk column contains: how many blocks of each id, and the height of
    /// the topmost non-air block in each of the 32x32 positions.
    /// </summary>
    public class ColumnContent
    {
        public readonly Dictionary<int, int> CountByBlockId = new();
        public readonly int[] SurfaceY = new int[ChunkSize * ChunkSize];
        public int TotalBlocks;

        public bool Has(int blockId) => CountByBlockId.ContainsKey(blockId);
    }

    /// <summary>
    /// Walk every block of a chunk column. Ids only: resolving an id to a Block lazily
    /// mutates a registry dictionary, so that happens on the main thread when the
    /// report is built, never here.
    /// </summary>
    static ColumnContent Read(IWorldChunk?[] chunks)
    {
        var content = new ColumnContent();
        int maxY = chunks.Length * ChunkSize - 1;

        for (int lz = 0; lz < ChunkSize; lz++)
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int surface = 0;
            for (int y = maxY; y >= 1; y--)
            {
                IWorldChunk? chunk = chunks[y / ChunkSize];
                if (chunk == null || chunk.Disposed) continue;

                int blockId = chunk.UnpackAndReadBlock(
                    ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                    BlockLayersAccess.FluidOrSolid);
                if (blockId == 0) continue;

                if (surface == 0) surface = y;
                content.CountByBlockId.TryGetValue(blockId, out int seen);
                content.CountByBlockId[blockId] = seen + 1;
                content.TotalBlocks++;
            }
            content.SurfaceY[lz * ChunkSize + lx] = surface;
        }
        return content;
    }

    /// <summary>Read one block from a column the caller holds. -1 when unreadable.</summary>
    static int BlockAt(IWorldChunk?[] chunks, int lx, int y, int lz)
    {
        if (y < 0 || y / ChunkSize >= chunks.Length) return -1;
        IWorldChunk? chunk = chunks[y / ChunkSize];
        if (chunk == null || chunk.Disposed) return -1;
        return chunk.UnpackAndReadBlock(
            ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx, BlockLayersAccess.FluidOrSolid);
    }

    /// <summary>The loaded column at these coordinates, or null when it is not resident.</summary>
    IWorldChunk?[]? LoadedColumn(int cx, int cz)
    {
        int count = sapi.World.BlockAccessor.MapSizeY / ChunkSize;
        var chunks = new IWorldChunk?[count];
        bool any = false;
        for (int cy = 0; cy < count; cy++)
        {
            chunks[cy] = sapi.World.BlockAccessor.GetChunk(cx, cy, cz);
            any |= chunks[cy] != null;
        }
        return any ? chunks : null;
    }

    string CodeOf(int blockId)
    {
        Block? block = blockId > 0 ? sapi.World.GetBlock(blockId) : null;
        return block?.Code?.ToString() ?? "id:" + blockId;
    }

    // ---- Experiment A: a peek against a full generation ---------------------------

    /// <summary>
    /// Peek a block of columns, then generate the same coordinates for real, and diff
    /// the centre column. Peek first: the savegame is untouched until the load.
    /// </summary>
    public void RunDiff(int centreCx, int centreCz, Action<string> report)
    {
        int span = 2 * ReferenceBorder + 1;
        int outstanding = span * span;
        var peeked = new Dictionary<long, IServerChunk[]>();

        logger.Notification(
            "Peek diff: peeking {0} columns around chunk {1},{2} at pass {3}, then generating "
            + "the same coordinates for real to compare.", outstanding, centreCx, centreCz,
            LodPlayerPregen.Pass);

        for (int dz = -ReferenceBorder; dz <= ReferenceBorder; dz++)
        for (int dx = -ReferenceBorder; dx <= ReferenceBorder; dx++)
        {
            int cx = centreCx + dx, cz = centreCz + dz;
            sapi.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
            {
                UntilPass = LodPlayerPregen.Pass,
                OnGenerated = columns =>
                {
                    IServerChunk[]? column = null;
                    columns?.TryGetValue(new Vec2i(cx, cz), out column);
                    sapi.Event.EnqueueMainThreadTask(() =>
                    {
                        if (column is { Length: > 0 }) peeked[LodColumnMap.Key(cx, cz)] = column;
                        if (--outstanding == 0) PeeksDone(centreCx, centreCz, peeked, report);
                    }, "vh-diff-peeked");
                },
            });
        }
    }

    void PeeksDone(int centreCx, int centreCz, Dictionary<long, IServerChunk[]> peeked,
        Action<string> report)
    {
        if (!peeked.TryGetValue(LodColumnMap.Key(centreCx, centreCz), out IServerChunk[]? centre))
        {
            report("the centre column never came back from the peek");
            return;
        }

        ColumnContent fromPeek = Read(centre);

        // Now the reference. Loading generates these columns for real, which is the
        // point - and the reason this is not the nondestructive scenario.
        int span = 2 * ReferenceBorder + 1;
        int outstanding = span * span;
        for (int dz = -ReferenceBorder; dz <= ReferenceBorder; dz++)
        for (int dx = -ReferenceBorder; dx <= ReferenceBorder; dx++)
        {
            sapi.WorldManager.LoadChunkColumnPriority(centreCx + dx, centreCz + dz,
                new ChunkLoadOptions
                {
                    KeepLoaded = true, // the reference must still be resident to read
                    OnLoaded = () =>
                    {
                        if (--outstanding == 0) LoadsDone(centreCx, centreCz, fromPeek, report);
                    },
                });
        }
    }

    void LoadsDone(int centreCx, int centreCz, ColumnContent fromPeek, Action<string> report)
    {
        IWorldChunk?[]? loadedChunks = LoadedColumn(centreCx, centreCz);
        if (loadedChunks == null)
        {
            report("the centre column did not become resident after loading");
            return;
        }

        ColumnContent fromLoad = Read(loadedChunks);

        var onlyLoaded = new List<string>();
        var onlyPeeked = new List<string>();
        foreach ((int id, int count) in fromLoad.CountByBlockId)
        {
            if (!fromPeek.Has(id)) onlyLoaded.Add($"{CodeOf(id)} x{count}");
        }
        foreach ((int id, int count) in fromPeek.CountByBlockId)
        {
            if (!fromLoad.Has(id)) onlyPeeked.Add($"{CodeOf(id)} x{count}");
        }
        onlyLoaded.Sort();
        onlyPeeked.Sort();

        // Surface height. A median of 0 says the Terrain pass alone sets the ground,
        // which is the assumption the whole feature rests on. Anything else is a find.
        var deltas = new List<int>(ChunkSize * ChunkSize);
        int shifted = 0;
        for (int i = 0; i < fromPeek.SurfaceY.Length; i++)
        {
            int delta = fromLoad.SurfaceY[i] - fromPeek.SurfaceY[i];
            deltas.Add(delta);
            if (delta != 0) shifted++;
        }
        deltas.Sort();
        int median = deltas[deltas.Count / 2];

        logger.Notification(
            "Peek diff at chunk {0},{1}: peek produced {2} blocks over {3} distinct ids; the "
            + "full generation produced {4} blocks over {5} ids.",
            centreCx, centreCz, fromPeek.TotalBlocks, fromPeek.CountByBlockId.Count,
            fromLoad.TotalBlocks, fromLoad.CountByBlockId.Count);
        logger.Notification("Peek diff: ONLY IN THE FULL GENERATION ({0}): {1}",
            onlyLoaded.Count, onlyLoaded.Count == 0 ? "nothing" : string.Join(", ", onlyLoaded));
        logger.Notification("Peek diff: ONLY IN THE PEEK ({0}): {1}",
            onlyPeeked.Count, onlyPeeked.Count == 0 ? "nothing" : string.Join(", ", onlyPeeked));
        logger.Notification(
            "Peek diff: surface height delta median {0}, {1} of {2} positions differ, range {3}..{4}",
            median, shifted, deltas.Count, deltas[0], deltas[^1]);

        report($"{onlyLoaded.Count} block types exist only in a real generation, "
            + $"{onlyPeeked.Count} only in the peek, surface median delta {median}. "
            + "The full lists are in the server log.");
    }

    // ---- Experiment B: a peek against a player edit -------------------------------

    /// <summary>
    /// Place a marker in the loaded world, confirm it reads back, then peek the same
    /// coordinate and look for it there. Writes to the world, so the caller gates this
    /// behind the dev-tools switch.
    /// </summary>
    public void RunEditTest(int cx, int cz, Action<string> report)
    {
        IWorldChunk?[]? loadedChunks = LoadedColumn(cx, cz);
        if (loadedChunks == null)
        {
            report("that column is not loaded, so there is nothing to edit. Stand near it and retry.");
            return;
        }

        Block? marker = sapi.World.GetBlock(new AssetLocation("game", "glass-plain"));
        if (marker == null)
        {
            report("could not find game:glass-plain to use as a marker");
            return;
        }

        // Above the surface, so worldgen has no reason to put anything here and the
        // marker cannot be mistaken for terrain the peek legitimately produced.
        const int lx = 16, lz = 16;
        int surface = 0;
        int maxY = loadedChunks.Length * ChunkSize - 1;
        for (int y = maxY; y >= 1 && surface == 0; y--)
        {
            if (BlockAt(loadedChunks, lx, y, lz) > 0) surface = y;
        }
        if (surface == 0)
        {
            report("found no solid ground in that column to place a marker above");
            return;
        }

        int baseY = surface + 1;
        for (int i = 0; i < MarkerHeight; i++)
        {
            sapi.World.BlockAccessor.SetBlock(marker.BlockId,
                new BlockPos(cx * ChunkSize + lx, baseY + i, cz * ChunkSize + lz));
        }

        // Read it back before peeking. Without this step an absent marker in the peek
        // could equally mean the placement silently failed.
        int placedReadback = BlockAt(LoadedColumn(cx, cz) ?? loadedChunks, lx, baseY, lz);
        bool presentWhenLoaded = placedReadback == marker.BlockId;

        logger.Notification(
            "Edit test: placed {0} x{1} at {2},{3},{4}. Reading the loaded world back at that "
            + "position gives {5}.", marker.Code, MarkerHeight,
            cx * ChunkSize + lx, baseY, cz * ChunkSize + lz, CodeOf(placedReadback));

        sapi.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
        {
            UntilPass = LodPlayerPregen.Pass,
            OnGenerated = columns =>
            {
                IServerChunk[]? column = null;
                columns?.TryGetValue(new Vec2i(cx, cz), out column);
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    if (column is not { Length: > 0 })
                    {
                        report("the peek returned no column, so the comparison could not run");
                        return;
                    }

                    int peekedAt = BlockAt(column!, lx, baseY, lz);
                    bool presentWhenPeeked = peekedAt == marker.BlockId;

                    logger.Notification(
                        "Edit test: the peek of the same coordinate gives {0} at that position. "
                        + "MARKER PRESENT WHEN LOADED: {1}. MARKER PRESENT WHEN PEEKED: {2}.",
                        CodeOf(peekedAt), presentWhenLoaded, presentWhenPeeked);

                    if (presentWhenLoaded && !presentWhenPeeked)
                    {
                        logger.Notification(
                            "Edit test: as expected. A peek regenerates from the seed and cannot "
                            + "see the savegame, so peeking a column that exists would cache "
                            + "terrain as it was before anyone built there. This is what "
                            + "LodColumnMap.Classify prevents by never peeking an existing column.");
                    }
                    else if (!presentWhenLoaded)
                    {
                        logger.Warning(
                            "Edit test: the marker did not read back from the loaded world, so "
                            + "this run proves nothing about the peek. The placement failed.");
                    }
                    else
                    {
                        logger.Warning(
                            "Edit test: the peek CONTAINED the placed marker. That contradicts "
                            + "the API contract this feature relies on. Investigate before "
                            + "trusting /vhgen anywhere near edited terrain.");
                    }

                    report($"marker present when loaded: {presentWhenLoaded}, present when "
                        + $"peeked: {presentWhenPeeked}. Details in the server log.");
                }, "vh-edittest-peeked");
            },
        });
    }
}
