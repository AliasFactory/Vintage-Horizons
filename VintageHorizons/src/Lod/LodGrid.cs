using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// One LOD region: 64×64 surface samples. At level L each sample covers
/// (SampleStep &lt;&lt; L) blocks, so the region footprint is RegionBlocks &lt;&lt; L.
/// </summary>
public class LodRegion
{
    public const int GridSize = LodGrid.RegionBlocks / LodGrid.SampleStep;

    public readonly float[] Heights = new float[GridSize * GridSize];
    public readonly int[] Colors = new int[GridSize * GridSize]; // RGBA packed, R in low byte
    public readonly bool[] HasData = new bool[GridSize * GridSize];
    public int FilledSamples;
}

/// <summary>
/// Multi-level in-memory LOD store (a heightmap mip pyramid, DH-style) fed from
/// chunks the client receives. Level 0 is written directly by chunk ingestion;
/// coarser levels are produced by budgeted child→parent downsampling whose dirty
/// flags are persisted alongside the regions, so pyramid consistency survives
/// crashes (the propagation queue is rebuilt from ApplyToParent flags on load).
/// </summary>
public class LodGrid
{
    public const int RegionBlocks = 256; // level-0 footprint
    public const int SampleStep = 4;     // level-0 blocks per sample
    public const int MaxLevel = 5;       // level-5 regions span 8192 blocks
    const int ChunkSize = GlobalConstants.ChunkSize;
    const int SamplesPerChunkEdge = ChunkSize / SampleStep;

    readonly ICoreClientAPI capi;
    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos samplePos = new(0, 0, 0);

    public readonly Dictionary<long, LodRegion> Regions = new();
    public readonly HashSet<long> DirtyRegions = new();
    public readonly HashSet<long> SaveDirtyRegions = new();

    /// <summary>Regions whose parent still needs to absorb their current content. Persisted as ApplyToParent.</summary>
    public readonly HashSet<long> MipDirty = new();

    /// <summary>Every key (all levels) that holds data or has any descendant with data. Drives quadtree descent.</summary>
    public readonly HashSet<long> HasDataSet = new();

    /// <summary>Top-level (MaxLevel) ancestor keys of everything we hold — the quadtree roots.</summary>
    public readonly HashSet<long> TopLevelKeys = new();

    public int ColumnsProcessed { get; private set; }
    public int PendingColumns => pendingColumns.Count;

    public LodGrid(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // ---- Key packing: level(4) | rz(30) | rx(30). VS world coords are non-negative. ----

    public static long ColumnKey(int cx, int cz) => ((long)cz << 32) | (uint)cx;

    public static long RegionKey(int level, int rx, int rz) =>
        ((long)level << 60) | ((long)(rz & 0x3FFFFFFF) << 30) | (uint)(rx & 0x3FFFFFFF);

    public static int KeyLevel(long key) => (int)(key >>> 60);
    public static int KeyRx(long key) => (int)(key & 0x3FFFFFFF);
    public static int KeyRz(long key) => (int)((key >> 30) & 0x3FFFFFFF);

    public static long ParentKey(long key) =>
        RegionKey(KeyLevel(key) + 1, KeyRx(key) >> 1, KeyRz(key) >> 1);

    public static long ChildKey(long key, int qx, int qz) =>
        RegionKey(KeyLevel(key) - 1, (KeyRx(key) << 1) + qx, (KeyRz(key) << 1) + qz);

    /// <summary>Region footprint in blocks at this key's level.</summary>
    public static int KeyFootprintBlocks(long key) => RegionBlocks << KeyLevel(key);

    // ---- Ingestion (level 0) ----

    /// <summary>May be called from any thread (ChunkDirty can fire off the main thread).</summary>
    public void EnqueueColumn(int cx, int cz)
    {
        long key = ColumnKey(cx, cz);
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    /// <summary>Main thread only. Processes up to maxColumns queued chunk columns.</summary>
    public void ProcessPending(int maxColumns)
    {
        for (int n = 0; n < maxColumns && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            ProcessColumn((int)(key & 0xFFFFFFFF), (int)(key >> 32));
        }
    }

    void ProcessColumn(int cx, int cz)
    {
        IMapChunk? mapChunk = capi.World.BlockAccessor.GetMapChunk(cx, cz);
        ushort[]? rainMap = mapChunk?.RainHeightMap;
        if (rainMap == null) return;

        // Rain height includes tree canopies (spiky); worldgen terrain height is the
        // pre-vegetation ground. Height comes from the ground, color from the canopy —
        // so forests read green without turning into cone fields. Water/ice keeps the
        // rain height: the terrain map points at the seafloor there.
        ushort[]? terrainMap = mapChunk!.WorldGenTerrainHeightMap;

        int baseX = cx * ChunkSize;
        int baseZ = cz * ChunkSize;

        for (int sz = 0; sz < SamplesPerChunkEdge; sz++)
        {
            for (int sx = 0; sx < SamplesPerChunkEdge; sx++)
            {
                int lx = sx * SampleStep + SampleStep / 2;
                int lz = sz * SampleStep + SampleStep / 2;
                int rainHeight = rainMap[lz * ChunkSize + lx];
                if (rainHeight <= 0) continue;

                IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cx, rainHeight / ChunkSize, cz);
                if (chunk == null || chunk.Disposed) continue; // filled in when that chunk arrives

                int blockIndex = ((rainHeight % ChunkSize) * ChunkSize + lz) * ChunkSize + lx;
                int blockId = chunk.UnpackAndReadBlock(blockIndex, BlockLayersAccess.FluidOrSolid);
                if (blockId == 0) continue;

                Block block = capi.World.Blocks[blockId];
                samplePos.Set(baseX + lx, rainHeight, baseZ + lz);
                int color = block.GetColor(capi, samplePos);

                int height = rainHeight;
                bool onWater = block.BlockMaterial == EnumBlockMaterial.Water
                    || block.BlockMaterial == EnumBlockMaterial.Lava
                    || block.BlockMaterial == EnumBlockMaterial.Ice
                    || block.BlockMaterial == EnumBlockMaterial.Snow;
                if (!onWater && terrainMap != null)
                {
                    int terrainHeight = terrainMap[lz * ChunkSize + lx];
                    if (terrainHeight > 0) height = terrainHeight;
                }

                WriteSample(baseX + lx, baseZ + lz, height + 1, color);
            }
        }

        ColumnsProcessed++;
    }

    void WriteSample(int blockX, int blockZ, float height, int color)
    {
        int rx = blockX / RegionBlocks;
        int rz = blockZ / RegionBlocks;
        long rkey = RegionKey(0, rx, rz);

        LodRegion region = GetOrCreateRegion(rkey);

        int gx = (blockX % RegionBlocks) / SampleStep;
        int gz = (blockZ % RegionBlocks) / SampleStep;
        int idx = gz * LodRegion.GridSize + gx;

        // Change gating: identical re-received data costs a compare, not a mesh rebuild + DB write.
        if (region.HasData[idx] && region.Heights[idx] == height && region.Colors[idx] == color) return;

        if (!region.HasData[idx]) region.FilledSamples++;
        region.Heights[idx] = height;
        region.Colors[idx] = color;
        region.HasData[idx] = true;

        MarkChanged(rkey, rx, rz, gx, gz);
    }

    void MarkChanged(long rkey, int rx, int rz, int gx, int gz)
    {
        DirtyRegions.Add(rkey);
        SaveDirtyRegions.Add(rkey);
        MipDirty.Add(rkey);

        // Meshes stitch to their east/south neighbors' first row/column, so new data on
        // our west/north edge means the west/north neighbor's mesh is stale.
        int level = KeyLevel(rkey);
        if (gx == 0) DirtyRegions.Add(RegionKey(level, rx - 1, rz));
        if (gz == 0) DirtyRegions.Add(RegionKey(level, rx, rz - 1));
        if (gx == 0 && gz == 0) DirtyRegions.Add(RegionKey(level, rx - 1, rz - 1));
    }

    LodRegion GetOrCreateRegion(long rkey)
    {
        if (Regions.TryGetValue(rkey, out LodRegion? region)) return region;

        Regions[rkey] = region = new LodRegion();
        RegisterInTree(rkey);
        return region;
    }

    void RegisterInTree(long rkey)
    {
        long key = rkey;
        while (true)
        {
            HasDataSet.Add(key);
            int level = KeyLevel(key);
            if (level == MaxLevel)
            {
                TopLevelKeys.Add(key);
                return;
            }
            key = ParentKey(key);
        }
    }

    // ---- Mip propagation (child → parent) ----

    /// <summary>
    /// Main thread. Folds up to maxRegions changed regions into their parents.
    /// Repeated child writes coalesce while queued; climbing stops early when a
    /// parent quadrant absorbs a change without actually changing (Voxy-style).
    /// </summary>
    public void ProcessPropagation(int maxRegions)
    {
        if (MipDirty.Count == 0) return;

        List<long>? batch = null;
        foreach (long key in MipDirty)
        {
            (batch ??= new List<long>()).Add(key);
            if (batch.Count >= maxRegions) break;
        }
        if (batch == null) return;

        foreach (long childKey in batch)
        {
            MipDirty.Remove(childKey);
            // The flag is persisted with the row; re-save the child with it cleared.
            SaveDirtyRegions.Add(childKey);

            if (KeyLevel(childKey) >= MaxLevel) continue;
            if (!Regions.TryGetValue(childKey, out LodRegion? child)) continue;

            long parentKey = ParentKey(childKey);
            LodRegion parent = GetOrCreateRegion(parentKey);

            if (DownsampleIntoParent(child, parent, KeyRx(childKey) & 1, KeyRz(childKey) & 1))
            {
                int prx = KeyRx(parentKey);
                int prz = KeyRz(parentKey);
                // A parent quadrant changed on its west/north edge exactly when the child
                // touched it; conservatively mark neighbor meshes stale via quadrant origin.
                MarkChanged(parentKey, prx, prz, (KeyRx(childKey) & 1) * (LodRegion.GridSize / 2),
                    (KeyRz(childKey) & 1) * (LodRegion.GridSize / 2));
            }
        }
    }

    /// <summary>2:1 downsample of the whole child into one parent quadrant. Returns true if anything changed.</summary>
    static bool DownsampleIntoParent(LodRegion child, LodRegion parent, int qx, int qz)
    {
        const int gs = LodRegion.GridSize;
        const int half = gs / 2;
        int ox = qx * half;
        int oz = qz * half;
        bool changed = false;

        for (int pz = 0; pz < half; pz++)
        {
            for (int px = 0; px < half; px++)
            {
                // Average the (up to) four children — heights and colors both. Max-style
                // representative sampling turns every bump into a spike after a few levels.
                float sumH = 0;
                int r = 0, g = 0, b = 0;
                int cnt = 0;

                for (int dz = 0; dz < 2; dz++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int ci = (pz * 2 + dz) * gs + (px * 2 + dx);
                        if (!child.HasData[ci]) continue;
                        sumH += child.Heights[ci];
                        int c = child.Colors[ci];
                        r += c & 0xFF;
                        g += (c >> 8) & 0xFF;
                        b += (c >> 16) & 0xFF;
                        cnt++;
                    }
                }

                if (cnt == 0) continue;

                float avgH = sumH / cnt;
                int avgC = (r / cnt) | ((g / cnt) << 8) | ((b / cnt) << 16);

                int pi = (oz + pz) * gs + (ox + px);
                if (parent.HasData[pi] && parent.Heights[pi] == avgH && parent.Colors[pi] == avgC) continue;

                if (!parent.HasData[pi]) parent.FilledSamples++;
                parent.Heights[pi] = avgH;
                parent.Colors[pi] = avgC;
                parent.HasData[pi] = true;
                changed = true;
            }
        }

        return changed;
    }

    // ---- Lookup / load ----

    /// <summary>Sample lookup in a level's global sample-grid coordinates (blockPos / (SampleStep &lt;&lt; level)).</summary>
    public bool TryGetSample(int level, int sampleGlobalX, int sampleGlobalZ, out float height, out int color)
    {
        height = 0;
        color = 0;
        if (sampleGlobalX < 0 || sampleGlobalZ < 0) return false;

        long rkey = RegionKey(level, sampleGlobalX / LodRegion.GridSize, sampleGlobalZ / LodRegion.GridSize);
        if (!Regions.TryGetValue(rkey, out LodRegion? region)) return false;

        int idx = (sampleGlobalZ % LodRegion.GridSize) * LodRegion.GridSize + (sampleGlobalX % LodRegion.GridSize);
        if (!region.HasData[idx]) return false;

        height = region.Heights[idx];
        color = region.Colors[idx];
        return true;
    }

    /// <summary>Adds a region loaded from the persistent cache (render-dirty; mip-dirty only if flagged).</summary>
    public void InstallLoadedRegion(int level, int rx, int rz, LodRegion region, bool applyToParent)
    {
        long rkey = RegionKey(level, rx, rz);
        Regions[rkey] = region;
        RegisterInTree(rkey);
        DirtyRegions.Add(rkey);
        if (applyToParent && level < MaxLevel) MipDirty.Add(rkey);
    }

    public void Clear()
    {
        Regions.Clear();
        DirtyRegions.Clear();
        SaveDirtyRegions.Clear();
        MipDirty.Clear();
        HasDataSet.Clear();
        TopLevelKeys.Clear();
        queuedColumns.Clear();
        while (pendingColumns.TryDequeue(out _)) { }
        ColumnsProcessed = 0;
    }

    public string DescribeLevels()
    {
        var counts = new int[MaxLevel + 1];
        foreach (long key in Regions.Keys) counts[KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }
}
