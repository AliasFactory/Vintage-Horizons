using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// One LOD region: a RegionBlocks × RegionBlocks world area holding surface height +
/// color samples every SampleStep blocks. M1 data model — replaced by the full
/// column-RLE section pyramid in M3 (see DESIGN.md §4).
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
/// In-memory LOD store fed from chunks the client receives. ChunkDirty events enqueue
/// chunk columns (thread-safe, deduped); the main thread drains the queue with a
/// per-tick budget, sampling surface height from the synced RainHeightMap and surface
/// color from the actual surface block.
/// </summary>
public class LodGrid
{
    public const int RegionBlocks = 256;
    public const int SampleStep = 4;
    const int ChunkSize = GlobalConstants.ChunkSize; // 32
    const int SamplesPerChunkEdge = ChunkSize / SampleStep;

    readonly ICoreClientAPI capi;
    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos samplePos = new(0, 0, 0);

    public readonly Dictionary<long, LodRegion> Regions = new();
    public readonly HashSet<long> DirtyRegions = new();

    public int ColumnsProcessed { get; private set; }
    public int PendingColumns => pendingColumns.Count;

    public LodGrid(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public static long ColumnKey(int cx, int cz) => ((long)cz << 32) | (uint)cx;
    public static long RegionKey(int rx, int rz) => ((long)rz << 32) | (uint)rx;

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
        ushort[]? heightMap = mapChunk?.RainHeightMap;
        if (heightMap == null) return;

        int baseX = cx * ChunkSize;
        int baseZ = cz * ChunkSize;

        for (int sz = 0; sz < SamplesPerChunkEdge; sz++)
        {
            for (int sx = 0; sx < SamplesPerChunkEdge; sx++)
            {
                int lx = sx * SampleStep + SampleStep / 2;
                int lz = sz * SampleStep + SampleStep / 2;
                int height = heightMap[lz * ChunkSize + lx];
                if (height <= 0) continue;

                IWorldChunk? chunk = capi.World.BlockAccessor.GetChunk(cx, height / ChunkSize, cz);
                if (chunk == null || chunk.Disposed) continue; // filled in when that chunk arrives

                int blockIndex = ((height % ChunkSize) * ChunkSize + lz) * ChunkSize + lx;
                int blockId = chunk.UnpackAndReadBlock(blockIndex, BlockLayersAccess.FluidOrSolid);
                if (blockId == 0) continue;

                Block block = capi.World.Blocks[blockId];
                samplePos.Set(baseX + lx, height, baseZ + lz);
                int color = block.GetColor(capi, samplePos);

                WriteSample(baseX + lx, baseZ + lz, height + 1, color);
            }
        }

        ColumnsProcessed++;
    }

    void WriteSample(int blockX, int blockZ, float height, int color)
    {
        int rx = blockX / RegionBlocks;
        int rz = blockZ / RegionBlocks;
        long rkey = RegionKey(rx, rz);

        if (!Regions.TryGetValue(rkey, out LodRegion? region))
        {
            Regions[rkey] = region = new LodRegion();
        }

        int gx = (blockX % RegionBlocks) / SampleStep;
        int gz = (blockZ % RegionBlocks) / SampleStep;
        int idx = gz * LodRegion.GridSize + gx;

        if (!region.HasData[idx]) region.FilledSamples++;
        region.Heights[idx] = height;
        region.Colors[idx] = color;
        region.HasData[idx] = true;

        DirtyRegions.Add(rkey);

        // Meshes stitch to their east/south neighbors' first row/column, so new data on
        // our west/north edge means the west/north neighbor's mesh is stale.
        if (gx == 0) DirtyRegions.Add(RegionKey(rx - 1, rz));
        if (gz == 0) DirtyRegions.Add(RegionKey(rx, rz - 1));
        if (gx == 0 && gz == 0) DirtyRegions.Add(RegionKey(rx - 1, rz - 1));
    }

    /// <summary>Sample lookup in global sample-grid coordinates (blockPos / SampleStep).</summary>
    public bool TryGetSample(int sampleGlobalX, int sampleGlobalZ, out float height, out int color)
    {
        height = 0;
        color = 0;
        if (sampleGlobalX < 0 || sampleGlobalZ < 0) return false;

        long rkey = RegionKey(sampleGlobalX / LodRegion.GridSize, sampleGlobalZ / LodRegion.GridSize);
        if (!Regions.TryGetValue(rkey, out LodRegion? region)) return false;

        int idx = (sampleGlobalZ % LodRegion.GridSize) * LodRegion.GridSize + (sampleGlobalX % LodRegion.GridSize);
        if (!region.HasData[idx]) return false;

        height = region.Heights[idx];
        color = region.Colors[idx];
        return true;
    }

    public void Clear()
    {
        Regions.Clear();
        DirtyRegions.Clear();
        queuedColumns.Clear();
        while (pendingColumns.TryDequeue(out _)) { }
        ColumnsProcessed = 0;
    }
}
