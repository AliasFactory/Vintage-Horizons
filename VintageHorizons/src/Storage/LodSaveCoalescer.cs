namespace VintageHorizons;

/// <summary>
/// Key -> newest pending save snapshot, with FIFO write order by first appearance.
///
/// The class is pure and free of game types, so its rules get direct checks without
/// a thread. Rule one: a newer snapshot replaces an older one for the same key. A
/// capture burst otherwise queues the same hot section once per tick. Rule two: the
/// older snapshot can hold the mip trigger, so its ApplyToParent flag merges into
/// the survivor. Losing that flag leaves a parent mip stale, with nothing to
/// schedule its refresh.
/// </summary>
public class LodSaveCoalescer
{
    readonly object gate = new();
    readonly Dictionary<long, LodSaveSnapshot> pending = new();
    readonly Queue<long> order = new();

    /// <summary>
    /// Hand over one snapshot. A waiting snapshot for the same key is replaced, and
    /// its ApplyToParent flag merges into the survivor. The result is true when a
    /// waiting snapshot was displaced. The caller treats that as one save settled,
    /// because the storage thread never writes a displaced snapshot. That rule keeps
    /// the backlog exact.
    /// </summary>
    public bool Add(LodSaveSnapshot snapshot)
    {
        long key = LodWorld.SectionKey(snapshot.Level, snapshot.SX, snapshot.SZ);
        bool displaced = false;

        lock (gate)
        {
            if (pending.TryGetValue(key, out LodSaveSnapshot? waiting))
            {
                // The newer snapshot wins on content. The older snapshot can hold the
                // mip trigger, and dropping it leaves the parent stale until some
                // future save of the child. The flag merges instead.
                snapshot.ApplyToParent |= waiting.ApplyToParent;
                displaced = true;
            }
            else
            {
                order.Enqueue(key);
            }

            pending[key] = snapshot;
        }

        return displaced;
    }

    /// <summary>Take at most <paramref name="max"/> snapshots out, in FIFO key order.</summary>
    public LodSaveSnapshot[] Take(int max)
    {
        lock (gate)
        {
            int count = Math.Min(max, order.Count);
            var batch = new LodSaveSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                long key = order.Dequeue();
                batch[i] = pending[key];
                pending.Remove(key);
            }
            return batch;
        }
    }

    /// <summary>Snapshots waiting. Distinct keys, so coalesced repeats do not inflate it.</summary>
    public int Count
    {
        get
        {
            lock (gate) return order.Count;
        }
    }
}
