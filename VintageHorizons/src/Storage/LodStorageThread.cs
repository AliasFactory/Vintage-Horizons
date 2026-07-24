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

    // Demand reloads. Requests come from the render path, which can tolerate a
    // section arriving a few frames late; the capture path still loads inline
    // because it must merge into stored data before it may create anything.
    readonly ConcurrentQueue<long> loadRequests = new();
    public readonly ConcurrentQueue<(long Key, LodSection? Section)> LoadResults = new();
    Func<long, LodSection?>? loadFunc;

    /// <summary>Set by the coordinator: performs one blocking read, called on this thread.</summary>
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

            // Loads first: a pending read is blocking terrain from appearing, a
            // pending write is not blocking anything.
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

        // Shutting down: never drop queued work, the rows are the player's cache.
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

        // Always answer, even on failure/miss: the requester clears its in-flight
        // marker from this queue and would otherwise never retry the key.
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
