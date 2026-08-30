using System.Collections.Concurrent;

namespace VintageHorizons;

/// <summary>
/// Serializes and writes LOD sections away from the render thread.
///
/// Measured before this existed: save batches cost ~10-22ms of main-thread time on
/// average and peaked near 49ms - an entire game tick - during exploration, which is
/// exactly when the player is moving and a stall is most visible. Deflate happens
/// here, outside the store's transaction lock, so a main-thread demand load waits at
/// most for a row write.
///
/// Coalescing: while a section waits to be written, a newer snapshot for the same
/// key replaces it, and the older ApplyToParent flag merges into the survivor. A
/// capture burst therefore writes each hot section once, not once per tick.
/// Batching: a run of pending sections leaves in one SaveBatch. That is one SQLite
/// transaction and one journal cycle. The per-row path paid that cycle up to 120
/// times a second during a fill. On a hard drive the storage thread never kept up,
/// and the cache rebuilds from capture, so the per-row durability bought nothing.
///
/// Ordering: a single consumer. Pending keys leave in FIFO order of first
/// appearance, and the newest snapshot for a key always wins.
/// </summary>
public class LodStorageThread : IDisposable
{
    readonly LodStore store;
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;

    // Demand reloads. Requests come from the render path, which can tolerate a
    // section arriving a few frames late; the capture path still loads inline
    // because it must merge into stored data before it may create anything.
    readonly ConcurrentQueue<long> loadRequests = new();
    public readonly ConcurrentQueue<(long Key, LodSection? Section)> LoadResults = new();
    Func<long, LodSection?>? loadFunc;

    // Saves go through LodSaveCoalescer. A repeat enqueue of a key collapses into
    // the newest snapshot. The consumer drains runs of distinct keys into one
    // SaveBatch transaction. See that class for the rules.
    readonly LodSaveCoalescer coalescer = new();

    /// <summary>Rows per transaction. A batch serializes outside the store lock first, so this only bounds the row list.</summary>
    const int MaxBatchRows = 64;

    /// <summary>Set by the coordinator: performs one blocking read, called on this thread.</summary>
    public void SetLoader(Func<long, LodSection?> loader) => loadFunc = loader;

    public void RequestLoad(long key)
    {
        loadRequests.Enqueue(key);
        signal.Set();
    }

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

        // A displaced waiting snapshot is never written, so its enqueue settles here.
        // Every enqueue ends in exactly one settle: a write, or a replacement by a
        // newer snapshot. That is what keeps Backlog and Drain honest.
        if (coalescer.Add(snapshot)) Interlocked.Increment(ref completedCount);

        signal.Set();
    }

    /// <summary>Take at most <paramref name="max"/> snapshots out of the coalescer, in FIFO key order.</summary>
    LodSaveSnapshot[] TakeBatch(int max) => coalescer.Take(max);

    /// <summary>Saves queued or executing - surfaced in the stats line and used to bound capture.</summary>
    public long Backlog => Interlocked.Read(ref enqueuedCount) - Interlocked.Read(ref completedCount);

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

            LodSaveSnapshot[] batch = TakeBatch(MaxBatchRows);
            if (batch.Length > 0)
            {
                didWork = true;
                WriteBatch(batch);
            }

            if (!didWork) signal.WaitOne(200);
        }

        // Shutting down: never drop queued work. The rows hold the LOD cache.
        // One final flush for whatever coalescing still holds.
        LodSaveSnapshot[] remaining = TakeBatch(int.MaxValue);
        if (remaining.Length > 0) WriteBatch(remaining);
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

    void WriteBatch(LodSaveSnapshot[] batch)
    {
        int count = batch.Length;
        try
        {
            // Serialize (Deflate) out here, outside the transaction lock of the store.
            // A demand load on the main thread never waits behind compression. The
            // rows land in one transaction: the journal cycle is paid once per batch,
            // not once per row.
            var rows = new (int Level, int SX, int SZ, byte[] Data, bool ApplyToParent)[count];
            for (int i = 0; i < count; i++)
            {
                LodSaveSnapshot snap = batch[i];
                rows[i] = (snap.Level, snap.SX, snap.SZ, LodStore.Serialize(snap), snap.ApplyToParent);
            }

            store.SaveBatch(rows);
            SectionsWritten += count;
        }
        catch (Exception e)
        {
            // One failure fails the whole batch transaction. Count every row in it as
            // not written. The next save of the same key retries. The error must not
            // kill the thread.
            SaveErrors += count;
            try { FirstSaveError ??= e.ToString(); } catch { /* diagnostics must not kill the thread */ }
        }
        finally
        {
            Interlocked.Add(ref completedCount, count);
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

    public void Dispose()
    {
        running = false;
        signal.Set();
        thread.Join(15000);

        // The thread is gone, so the joiner flushes what is left in the coalescer.
        // A snapshot that arrives during exit is never written otherwise.
        LodSaveSnapshot[] remaining = TakeBatch(int.MaxValue);
        if (remaining.Length > 0) WriteBatch(remaining);

        signal.Dispose();
    }
}
