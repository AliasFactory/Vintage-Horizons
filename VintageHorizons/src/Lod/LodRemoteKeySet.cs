namespace VintageHorizons;

/// <summary>
/// Bookkeeping for sections a remote source offers: which keys only it has, which of those
/// the view currently wants, and which have been asked for already.
///
/// Split out of LodPipeline so it can be reasoned about - and tested - on its own. It is
/// pure set logic over a LodWorld, but living inside a class whose constructor needs a game
/// API and starts five threads made it unreachable, and the one bug that mattered most here
/// took three wrong diagnoses read off counters before anyone looked at the branch itself.
/// </summary>
public class LodRemoteKeySet
{
    readonly LodWorld world;

    public LodRemoteKeySet(LodWorld world) => this.world = world;

    /// <summary>
    /// Keys only a remote source has. Kept separate from HasDataSet so the loader can tell
    /// "evicted from RAM, still on disk" from "never been on this disk".
    /// </summary>
    public readonly HashSet<long> RemoteOnly = new();

    /// <summary>
    /// Keys this store actually holds a row for, as reported by LoadAllKeys. Distinct from
    /// LodWorld.HasDataSet, which also holds ancestors synthesised for quadtree descent and
    /// so cannot answer "can local disk supply this?".
    /// </summary>
    readonly HashSet<long> localKeys = new();

    readonly HashSet<long> remoteWanted = new();

    /// <summary>Record that local disk holds a row for this key. Called during the cache key scan.</summary>
    public void AddLocalKey(long key) => localKeys.Add(key);

    /// <summary>
    /// Route a reload request. True when only the network can supply this key, so the
    /// caller sends it there instead of to the local store - a key local disk has never
    /// held would come back empty and land in LoadFailed, which is permanent.
    /// </summary>
    public bool WantFromRemote(long key)
    {
        if (!RemoteOnly.Contains(key)) return false;
        remoteWanted.Add(key);
        return true;
    }

    /// <summary>
    /// Register keys a remote source offers. Only those with no local data become
    /// remote-only; anything already on disk stays a local read, because local wins.
    /// </summary>
    public int AddRemoteKeys(IEnumerable<long> keys)
    {
        int added = 0;
        foreach (long key in keys)
        {
            // Against localKeys, NOT HasDataSet. HasDataSet also contains every ancestor
            // that RegisterInTree synthesised while registering a finer key, so testing it
            // skipped coarse keys the server really could serve - whichever of a node and
            // its descendants happened to be processed first decided the other's fate.
            // Those keys stayed out of RemoteOnly, routed to a local store with no such
            // row, came back null, and were recorded in LoadFailed, which is permanent.
            // Observed as nodes drawn at L5 with two children "load-failed" and the
            // pipeline idle: terrain that could never resolve, at any distance.
            if (localKeys.Contains(key)) continue;
            if (!RemoteOnly.Add(key)) continue;

            // A key poisoned by that bug, or by an earlier miss before the manifest
            // arrived, has to be given back its chance now that a source exists.
            world.LoadFailed.Remove(key);
            world.LoadsInFlight.Remove(key);

            // Into the quadtree skeleton too, or descent will not even consider the key
            // and nothing would ever ask for it. Same call the local key scan uses, minus
            // the mip flag: the server's pyramid is already built, so nothing is pending.
            world.InstallStoredKey(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                applyToParent: false);
            added++;
        }
        return added;
    }

    /// <summary>
    /// Keys the render path asked for that only a remote source has. Fetch order therefore
    /// follows what the player can actually see.
    /// </summary>
    public long[] Wanted() =>
        remoteWanted.Count == 0 ? Array.Empty<long>() : remoteWanted.ToArray();

    /// <summary>
    /// Drop the keys that were actually asked for. Only these, never the whole set: a key
    /// held back by the in-flight cap is already in LodWorld.LoadsInFlight, where the
    /// render scheduler skips it, and forgetting it here would strand it there for the rest
    /// of the session.
    /// </summary>
    public void MarkRequested(IEnumerable<long> sent)
    {
        foreach (long key in sent) remoteWanted.Remove(key);
    }

    /// <summary>
    /// The remote source will not supply this key - declined, gone, or unparseable. Clears
    /// the render path's wait on it: LodWorld.LoadsInFlight is set by TryGetForRender and
    /// otherwise only cleared by InstallLoaded, so without this a declined key stays
    /// "in flight" for the session, the mesh scheduler skips it, and its parent is pinned
    /// coarse forever.
    /// </summary>
    public void MarkUnavailable(long key)
    {
        RemoteOnly.Remove(key);
        remoteWanted.Remove(key);

        // Already resident (a local capture won the race): just stop waiting. Recording a
        // load failure would block reloading it after a future RAM eviction.
        if (world.Sections.ContainsKey(key))
        {
            world.LoadsInFlight.Remove(key);
            return;
        }

        world.InstallLoaded(key, null);
    }

    public void Clear()
    {
        localKeys.Clear();
        RemoteOnly.Clear();
        remoteWanted.Clear();
    }
}
