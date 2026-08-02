namespace VintageHorizons;

/// <summary>
/// The record of the sections that a remote source offers. It holds the keys that only the
/// remote source has, the keys of those that the view wants now, and the keys that the mod
/// asked for already.
///
/// This class came out of LodPipeline, thus a person can understand it alone, and a test can
/// reach it. It is pure set logic over a LodWorld. But it was inside a class whose
/// constructor needs a game API and starts five threads, thus no test could reach it.
///
/// The most important defect here took three wrong diagnoses from the counters, before
/// anyone examined the branch itself.
/// </summary>
public class LodRemoteKeySet
{
    readonly LodWorld world;

    public LodRemoteKeySet(LodWorld world) => this.world = world;

    /// <summary>
    /// The keys that only a remote source has. This set stays separate from HasDataSet.
    /// Thus the loader can separate "removed from RAM, but still on the disk" from "never
    /// on this disk".
    /// </summary>
    public readonly HashSet<long> RemoteOnly = new();

    /// <summary>
    /// The keys for which this store holds a row, as LoadAllKeys reports them. This set is
    /// different from LodWorld.HasDataSet. HasDataSet also holds the ancestors that the mod
    /// made for the descent of the quadtree. Thus HasDataSet cannot answer the question
    /// "can the local disk supply this key?".
    /// </summary>
    readonly HashSet<long> localKeys = new();

    readonly HashSet<long> remoteWanted = new();

    /// <summary>Record that the local disk holds a row for this key. The scan of the cache
    /// keys calls this.</summary>
    public void AddLocalKey(long key) => localKeys.Add(key);

    /// <summary>
    /// Route a reload request. The result is true when only the network can supply this
    /// key. Then the caller sends the request to the network, and not to the local store.
    /// A key that the local disk never held returns nothing, and it goes into LoadFailed,
    /// which is permanent.
    /// </summary>
    public bool WantFromRemote(long key)
    {
        if (!RemoteOnly.Contains(key)) return false;
        remoteWanted.Add(key);
        return true;
    }

    /// <summary>
    /// Register the keys that a remote source offers. Only a key with no local data becomes
    /// remote-only. A key that the disk holds already stays a local read, because the local
    /// data wins.
    /// </summary>
    public int AddRemoteKeys(IEnumerable<long> keys)
    {
        int added = 0;
        foreach (long key in keys)
        {
            // Test against localKeys, and NOT against HasDataSet. HasDataSet also holds
            // each ancestor that RegisterInTree made during the registration of a finer
            // key. Thus a test against HasDataSet skipped coarse keys that the server can
            // give. The first of a node or its descendants to be processed decided the
            // result for the other one.
            //
            // Those keys stayed out of RemoteOnly. They went to a local store that has no
            // such row, they returned null, and the mod recorded them in LoadFailed, which
            // is permanent. The symptom was a node drawn at L5 with two children in the
            // state "load-failed", and an idle pipeline. That terrain can never resolve, at
            // any distance.
            if (localKeys.Contains(key)) continue;
            if (!RemoteOnly.Add(key)) continue;

            // That defect can damage a key. An earlier miss, before the manifest arrived,
            // can damage a key also. A source exists now, thus each such key gets another
            // opportunity.
            world.LoadFailed.Remove(key);
            world.LoadsInFlight.Remove(key);

            // Put the key into the quadtree structure also. Without this, the descent does
            // not examine the key, and nothing asks for it. This is the same call that the
            // scan of the local keys uses, but without the mip flag. The pyramid of the
            // server is complete already, thus nothing is pending.
            world.InstallStoredKey(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                applyToParent: false);
            added++;
        }
        return added;
    }

    /// <summary>
    /// The keys that the render path asked for, and that only a remote source has. Thus the
    /// order of the fetches follows what the player can see.
    /// </summary>
    public long[] Wanted() =>
        remoteWanted.Count == 0 ? Array.Empty<long>() : remoteWanted.ToArray();

    /// <summary>
    /// Remove the keys that the mod asked for. Remove only those keys, and never the full
    /// set.
    ///
    /// The in-flight limit holds some keys back. Such a key is in LodWorld.LoadsInFlight
    /// already, where the render scheduler skips it. If this method removes that key, the
    /// key stops there for the remainder of the session.
    /// </summary>
    public void MarkRequested(IEnumerable<long> sent)
    {
        foreach (long key in sent) remoteWanted.Remove(key);
    }

    /// <summary>
    /// The remote source does not supply this key. The server declined it, or the key is
    /// gone, or the mod cannot parse it. This method stops the wait of the render path.
    ///
    /// TryGetForRender sets LodWorld.LoadsInFlight, and only InstallLoaded clears it.
    /// Without this method, a declined key stays "in flight" for the session, the mesh
    /// scheduler skips it, and its parent stays coarse forever.
    /// </summary>
    public void MarkUnavailable(long key)
    {
        RemoteOnly.Remove(key);
        remoteWanted.Remove(key);

        // The section is resident already, because a local capture won the race. Stop the
        // wait only. A record of a load failure prevents a reload after a later removal
        // from RAM.
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
