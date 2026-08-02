using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageHorizons;

/// <summary>A view of a section that does not change, for meshing on another thread. The mod
/// never edits an array in place. It replaces the full array.</summary>
public class SectionSnapshot
{
    public required ulong[] Runs;
    public required int[] ColumnStart;
    public required bool[] Captured;
    public required int[] PaletteColors;
    public required byte[] PaletteFlags;
    public required byte[] PaletteTintSlots;

    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public static SectionSnapshot Of(LodSection s)
    {
        var colors = new int[s.Palette.Count];
        var flags = new byte[s.Palette.Count];
        var slots = new byte[s.Palette.Count];
        for (int i = 0; i < s.Palette.Count; i++)
        {
            colors[i] = s.Palette[i].Color;
            flags[i] = s.Palette[i].Flags;
            slots[i] = s.Palette[i].TintSlot;
        }
        return new SectionSnapshot
        {
            Runs = s.Runs,
            ColumnStart = s.ColumnStart,
            Captured = (bool[])s.Captured.Clone(),
            PaletteColors = colors,
            PaletteFlags = flags,
            PaletteTintSlots = slots,
        };
    }
}

public class CaptureJob
{
    public int Cx, Cz;
    public required IWorldChunk?[] Chunks; // indexed by chunkY
    public required ushort[] RainMap;      // copied on the main thread
}

/// <summary>A run holds a raw BLOCK id, and not a palette id. The main thread changes them
/// at apply.</summary>
public class CaptureResult
{
    public long SectionKey;
    public int Cx, Cz;
    public required ulong[]?[] RunsByColumn; // GridSize² entries, only this chunk column's 16×16 filled
}

public class MeshJob
{
    public long Key;
    public required SectionSnapshot Self;
    public required SectionSnapshot?[] Neighbors; // W, E, N, S
}

public class MeshResult
{
    public long Key;
    public required float[] Xyz;
    public required byte[] Rgba;
    public required int[] Indices;
    public int VertexCount;
    public int IndexCount;

    // Water and other translucent geometry. The mod draws it in a second blended pass.
    public float[]? WaterXyz;
    public byte[]? WaterRgba;
    public int[]? WaterIndices;
    public int WaterVertexCount;
    public int WaterIndexCount;
}

/// <summary>
/// The background thread. It converts the block data of a chunk into RLE columns, which is
/// the capture. It also converts a section into vertex data, which is the meshing.
///
/// A capture job has the higher priority. A mesh is only as correct as the data below it.
///
/// Each access to the game state uses a reference that the main thread gave. A read of a
/// chunk also has a guard, because the engine can remove that chunk at the same time.
/// </summary>
public class LodWorker : IDisposable
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    readonly ConcurrentQueue<CaptureJob> captureJobs = new();
    readonly ConcurrentQueue<MeshJob> meshJobs = new();
    public readonly ConcurrentQueue<CaptureResult> CaptureResults = new();
    public readonly ConcurrentQueue<MeshResult> MeshResults = new();

    /// <summary>Wakes the capture thread. There is one job and one thread, thus auto-reset
    /// is correct.</summary>
    readonly AutoResetEvent captureSignal = new(false);

    /// <summary>
    /// One permit for each mesh job in the queue. Thus N threads that wait wake for N jobs.
    /// An AutoResetEvent wakes exactly one thread, whatever the number of jobs in the
    /// queue.
    /// </summary>
    readonly SemaphoreSlim meshSignal = new(0);

    readonly Thread captureThread;
    readonly Thread[] meshThreads;
    volatile bool running = true;

    /// <summary>
    /// The threads that build meshes.
    ///
    /// Meshing reads SectionSnapshot objects only, and those do not change. That is the
    /// reason for the snapshot rule. Thus meshing runs on many threads with no lock.
    ///
    /// Capture does not get the same treatment. It reads live IWorldChunk objects that the
    /// engine owns. More threads there multiply the risk, and the gain is much smaller.
    ///
    /// This count leaves two cores for the render thread and the simulation thread of the
    /// game.
    /// </summary>
    static int MeshThreadCount => Math.Clamp(Environment.ProcessorCount - 2, 1, 4);

    public int MeshThreads => meshThreads.Length;

    public int PendingCaptures => captureJobs.Count;
    public int PendingMeshes => meshJobs.Count;

    public int CaptureErrors;
    public int MeshErrors;

    /// <summary>The first exception of each kind that the worker caught. A person uses these
    /// to diagnose a long test run.</summary>
    public string? FirstCaptureError;
    public string? FirstMeshError;

    public LodWorker()
    {
        captureThread = new Thread(CaptureLoop)
        {
            Name = "vintagehorizons-capture",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        captureThread.Start();

        meshThreads = new Thread[MeshThreadCount];
        for (int i = 0; i < meshThreads.Length; i++)
        {
            meshThreads[i] = new Thread(MeshLoop)
            {
                Name = "vintagehorizons-mesh-" + i,
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            meshThreads[i].Start();
        }
    }

    public void EnqueueCapture(CaptureJob job)
    {
        captureJobs.Enqueue(job);
        captureSignal.Set();
    }

    public void EnqueueMesh(MeshJob job)
    {
        meshJobs.Enqueue(job);
        meshSignal.Release();
    }

    // There are two loops, and not one. The old shared loop took EACH capture from the queue
    // before it took one mesh job. Thus exploration stopped the meshing, and a coarse parent
    // stayed on the screen for minutes. Exploration is exactly when new terrain most needs a
    // mesh.

    void CaptureLoop()
    {
        while (running)
        {
            bool didWork = false;
            while (captureJobs.TryDequeue(out CaptureJob? job))
            {
                didWork = true;
                try
                {
                    CaptureResult? result = Capture(job);
                    if (result != null) CaptureResults.Enqueue(result);
                }
                catch (Exception e)
                {
                    // The engine removed the chunk during the read, or something similar
                    // occurred. The column returns to the queue at its next ChunkDirty.
                    Interlocked.Increment(ref CaptureErrors);
                    Interlocked.CompareExchange(ref FirstCaptureError, e.ToString(), null);
                }
            }

            if (!didWork) captureSignal.WaitOne(250);
        }
    }

    void MeshLoop()
    {
        while (running)
        {
            // The wait has a time limit, and it is not indefinite. Thus a shutdown never
            // waits for a permit.
            if (!meshSignal.Wait(250)) continue;
            if (!meshJobs.TryDequeue(out MeshJob? job)) continue;

            try
            {
                MeshResults.Enqueue(LodMesher.BuildMesh(job));
            }
            catch (Exception e)
            {
                // The snapshot is not consistent. The mod meshes the section again at its
                // next change.
                Interlocked.Increment(ref MeshErrors);
                Interlocked.CompareExchange(ref FirstMeshError, e.ToString(), null);
            }
        }
    }

    // ---- Capture: a chunk column becomes RLE columns that hold raw block ids ----

    static CaptureResult? Capture(CaptureJob job)
    {
        const int step = LodSection.ColumnStepBlocks;
        const int colsPerChunk = ChunkSize / step;

        int baseX = job.Cx * ChunkSize;
        int baseZ = job.Cz * ChunkSize;
        int sectionX = baseX / LodSection.SectionBlocks;
        int sectionZ = baseZ / LodSection.SectionBlocks;
        int colOffsetX = (baseX % LodSection.SectionBlocks) / step;
        int colOffsetZ = (baseZ % LodSection.SectionBlocks) / step;

        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        var runs = new List<ulong>(24);
        bool anyColumn = false;

        // A value in the rain map can be at the map height or above it, on a column that
        // arrived just now. That value is an uninitialized marker. Clamp it, thus the walk
        // over y stays inside the chunk stack.
        int maxY = job.Chunks.Length * ChunkSize - 1;

        for (int cz = 0; cz < colsPerChunk; cz++)
        {
            for (int cx = 0; cx < colsPerChunk; cx++)
            {
                int lx = cx * step;
                int lz = cz * step;
                int startY = Math.Min(job.RainMap[lz * ChunkSize + lx], maxY);
                if (startY <= 0) continue;

                runs.Clear();
                int currentBlock = 0;
                int runTop = 0;
                bool complete = true;

                for (int y = startY; y >= 1; y--)
                {
                    IWorldChunk? chunk = job.Chunks[y / ChunkSize];
                    if (chunk == null || chunk.Disposed)
                    {
                        complete = false;
                        break;
                    }

                    int blockId = chunk.UnpackAndReadBlock(
                        ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                        BlockLayersAccess.FluidOrSolid);

                    if (blockId != currentBlock)
                    {
                        if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, y + 1));
                        currentBlock = blockId;
                        runTop = y + 1;
                    }
                }

                if (!complete) continue;
                if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, 1));

                batch[LodSection.ColumnIndex(colOffsetX + cx, colOffsetZ + cz)] = runs.ToArray();
                anyColumn = true;
            }
        }

        if (!anyColumn) return null;

        return new CaptureResult
        {
            SectionKey = LodWorld.SectionKey(0, sectionX, sectionZ),
            Cx = job.Cx,
            Cz = job.Cz,
            RunsByColumn = batch,
        };
    }

    public void Dispose()
    {
        running = false;
        captureSignal.Set();
        meshSignal.Release(meshThreads.Length);

        captureThread.Join(2000);
        foreach (Thread t in meshThreads) t.Join(2000);

        captureSignal.Dispose();
        meshSignal.Dispose();
    }
}
