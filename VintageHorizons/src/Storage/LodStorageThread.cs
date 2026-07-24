using System.Collections.Concurrent;

namespace VintageHorizons;

/// <summary>
/// Serializes and writes LOD sections away from the render thread.
///
/// Measured before this existed: save batches cost ~10-22ms of main-thread time on
/// average and peaked near 49ms — an entire game tick — during exploration, which is
/// exactly when the player is moving and a stall is most visible. Deflate happens
/// here, outside the store's transaction lock, so a main-thread demand load waits at
/// most for a row write.
///
/// Ordering: a single consumer over a FIFO queue, so repeated saves of the same
/// section land in the order the main thread produced them and the newest snapshot
/// wins.
/// </summary>
public class LodStorageThread : IDisposable
{
    readonly LodStore store;
    readonly ConcurrentQueue<LodSaveSnapshot> queue = new();
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;

    public int Pending => queue.Count;
    public int SaveErrors;
    public string? FirstSaveError;
    public long SectionsWritten;

    // Drain waits on these rather than on an empty queue: the queue goes empty the
    // moment the last item is dequeued, while its write is still in progress.
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
            while (queue.TryDequeue(out LodSaveSnapshot? snap))
            {
                didWork = true;
                WriteOne(snap);
            }
            if (!didWork) signal.WaitOne(200);
        }

        // Shutting down: never drop queued work, the rows are the player's cache.
        while (queue.TryDequeue(out LodSaveSnapshot? snap)) WriteOne(snap);
    }

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
    /// Block until every queued section has been written. Called before the store is
    /// closed on leave-world; without it a crash-free exit could still lose sections.
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

    /// <summary>Sections enqueued but not yet written — surfaced in the stats line.</summary>
    public long Backlog => Interlocked.Read(ref enqueuedCount) - Interlocked.Read(ref completedCount);

    public void Dispose()
    {
        running = false;
        signal.Set();
        thread.Join(15000);
        signal.Dispose();
    }
}
