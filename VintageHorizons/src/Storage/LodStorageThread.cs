using System.Collections.Concurrent;

namespace VintageHorizons;

/// <summary>
/// Serializes LOD sections and writes them, off the render thread.
///
/// This was measured before this class existed. A save batch cost 10 ms to 22 ms of main
/// thread time on average. It reached approximately 49 ms, which is one full game tick.
///
/// That occurred during exploration. Exploration is exactly when the player moves, and a
/// delay is most visible then.
///
/// The deflate occurs here, outside the transaction lock of the store. Thus a load on the
/// main thread waits for a row write at most.
///
/// The order is correct because one consumer reads a FIFO queue. Thus more than one save of
/// the same section arrives in the order that the main thread made them, and the newest
/// snapshot wins.
/// </summary>
public class LodStorageThread : IDisposable
{
    readonly LodStore store;
    readonly ConcurrentQueue<LodSaveSnapshot> queue = new();
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;

    // Loads that come from demand. The requests come from the render path, which accepts a
    // section that arrives a few frames late. The capture path still loads in the same call,
    // because it must merge into the stored data before it makes anything.
    readonly ConcurrentQueue<long> loadRequests = new();
    public readonly ConcurrentQueue<(long Key, LodSection? Section)> LoadResults = new();
    Func<long, LodSection?>? loadFunc;

    /// <summary>The coordinator sets this. It does one read that blocks, and this thread
    /// calls it.</summary>
    public void SetLoader(Func<long, LodSection?> loader) => loadFunc = loader;

    public void RequestLoad(long key)
    {
        loadRequests.Enqueue(key);
        signal.Set();
    }

    public int Pending => queue.Count;
    public int SaveErrors;
    public string? FirstSaveError;
    public long SectionsWritten;

    // Drain waits on these values, and not on an empty queue. The queue becomes empty at
    // the moment when the mod takes the last item, and the write of that item is still in
    // progress.
    long enqueuedCount;
    long completedCount;

    public LodStorageThread(LodStore store)
    {
        this.store = store;
        thread = new Thread(Loop)
        {
            Name = "vintagehorizons-storage",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public void Enqueue(LodSaveSnapshot snapshot)
    {
        Interlocked.Increment(ref enqueuedCount);
        queue.Enqueue(snapshot);
        signal.Set();
    }

    void Loop()
    {
        while (running)
        {
            bool didWork = false;

            // Do the loads first. A read that waits stops terrain from appearing. A write
            // that waits stops nothing.
            while (loadRequests.TryDequeue(out long key))
            {
                didWork = true;
                ReadOne(key);
            }

            if (queue.TryDequeue(out LodSaveSnapshot? snap))
            {
                didWork = true;
                WriteOne(snap);
            }

            if (!didWork) signal.WaitOne(200);
        }

        // The mod is stopping. Never discard the work in the queue, because those rows are
        // the cache of the player.
        while (queue.TryDequeue(out LodSaveSnapshot? snap)) WriteOne(snap);
    }

    void ReadOne(long key)
    {
        LodSection? section = null;
        try
        {
            section = loadFunc?.Invoke(key);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref LoadErrors);
            try { FirstLoadError ??= e.ToString(); } catch { /* diagnostics must not kill the thread */ }
        }

        if (section != null) SectionsRead++;

        // Always answer, for a failure and for a miss also. The requester clears its
        // in-flight mark from this queue. Without an answer, it never tries that key
        // again.
        LoadResults.Enqueue((key, section));
    }

    public int LoadErrors;
    public string? FirstLoadError;
    public long SectionsRead;

    void WriteOne(LodSaveSnapshot snap)
    {
        try
        {
            store.SaveBlob(snap.Level, snap.SX, snap.SZ, LodStore.Serialize(snap), snap.ApplyToParent);
            SectionsWritten++;
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref SaveErrors);
            try { FirstSaveError ??= e.ToString(); } catch { /* never let diagnostics kill the thread */ }
        }
        finally
        {
            Interlocked.Increment(ref completedCount);
        }
    }

    /// <summary>
    /// Wait until the mod writes each section in the queue. The caller uses this before it
    /// closes the store, when the player leaves the world. Without it, an exit with no crash
    /// still loses sections.
    /// </summary>
    public void Drain(int timeoutMs = 15000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        signal.Set();
        while (Interlocked.Read(ref completedCount) < Interlocked.Read(ref enqueuedCount)
               && clock.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>The sections that are in the queue and that the mod did not write yet. The
    /// statistics line shows this count.</summary>
    public long Backlog => Interlocked.Read(ref enqueuedCount) - Interlocked.Read(ref completedCount);

    public void Dispose()
    {
        running = false;
        signal.Set();
        thread.Join(15000);
        signal.Dispose();
    }
}
