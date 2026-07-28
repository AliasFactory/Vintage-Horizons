namespace VintageHorizons.Checks;

/// <summary>
/// Which sections the client fetches from a server rather than from its own disk.
///
/// This is where the most expensive bug in the project's history lived. It survived three
/// diagnoses read off counters — fetch ordering, mesh throughput, uncoverable children —
/// before anyone printed the actual branch state and saw it in one shot. The lesson written
/// into DESIGN.md was "instrument the decision, do not infer it"; these checks are the
/// standing version of that.
/// </summary>
public static class RemoteKeyChecks
{
    public static void Run(Check c)
    {
        SynthesisedAncestors(c);
        LocalWins(c);
        WantedFollowsTheView(c);
        OnlyForgetWhatWasSent(c);
        Unavailable(c);
    }

    /// <summary>
    /// THE regression test.
    ///
    /// Registering a fine key walks UPWARD, adding every ancestor to HasDataSet so the
    /// quadtree can descend to it. Those ancestors hold no data of their own — they are
    /// scaffolding. Testing HasDataSet to answer "can local disk supply this?" therefore
    /// says yes for keys local disk has never held.
    ///
    /// The consequence was not a missing section, it was a permanent one: the coarse key
    /// stayed out of RemoteOnly, was routed to a local store with no such row, came back
    /// null, and landed in LoadFailed — which nothing clears. Terrain that could never
    /// resolve, at any distance, showing as L5 nodes with two children stuck "load-failed"
    /// and an idle pipeline.
    /// </summary>
    static void SynthesisedAncestors(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        // A fine key from local disk. Registering it synthesises its whole ancestor chain.
        long fine = LodWorld.SectionKey(0, 8, 8);
        remote.AddLocalKey(fine);
        world.InstallStoredKey(0, 8, 8, applyToParent: false);

        long coarse = LodWorld.ParentKey(LodWorld.ParentKey(fine));
        c.True(world.HasDataSet.Contains(coarse),
            "registering a fine key synthesises its ancestors into HasDataSet");
        c.True(LodWorld.KeyLevel(coarse) > 0, "the synthesised ancestor is genuinely coarser");

        // The server offers that same coarse key, which it really does hold.
        int added = remote.AddRemoteKeys(new[] { coarse });

        c.Eq(1, added, "a server-held coarse key is accepted even though HasDataSet contains it");
        c.True(remote.RemoteOnly.Contains(coarse), "the coarse key is routed to the network, not local disk");
        c.True(remote.WantFromRemote(coarse), "asking to reload it goes to the network");

        // And a key already poisoned by the old bug must get its chance back.
        long poisoned = LodWorld.SectionKey(2, 30, 30);
        world.LoadFailed.Add(poisoned);
        world.LoadsInFlight.Add(poisoned);
        remote.AddRemoteKeys(new[] { poisoned });

        c.False(world.LoadFailed.Contains(poisoned), "a previously failed key is un-failed once a source exists");
        c.False(world.LoadsInFlight.Contains(poisoned), "a stranded in-flight key is released");
        c.True(remote.RemoteOnly.Contains(poisoned), "the recovered key is fetchable");
    }

    /// <summary>
    /// A key local disk really holds must stay a local read. The client's own capture is
    /// what it actually observed, including edits it witnessed, so it beats the server's.
    /// </summary>
    static void LocalWins(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long key = LodWorld.SectionKey(0, 4, 4);
        remote.AddLocalKey(key);

        c.Eq(0, remote.AddRemoteKeys(new[] { key }), "a key local disk holds is not taken from the server");
        c.False(remote.RemoteOnly.Contains(key), "a locally-held key never becomes remote-only");
        c.False(remote.WantFromRemote(key), "reloading a locally-held key goes to disk");

        // Offering the same key twice must not count it twice.
        long fresh = LodWorld.SectionKey(0, 9, 9);
        c.Eq(1, remote.AddRemoteKeys(new[] { fresh }), "a new key counts once");
        c.Eq(0, remote.AddRemoteKeys(new[] { fresh }), "the same key offered again counts zero");
        c.Eq(1, remote.RemoteOnly.Count, "the remote set does not grow on a repeat offer");
    }

    static void WantedFollowsTheView(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long a = LodWorld.SectionKey(0, 1, 1);
        long b = LodWorld.SectionKey(0, 2, 2);
        remote.AddRemoteKeys(new[] { a, b });

        c.Eq(0, remote.Wanted().Length, "nothing is wanted until the render path asks");

        remote.WantFromRemote(a);
        c.SeqEq(new[] { a }, remote.Wanted(), "only what the view asked for is wanted");

        // Wanted() must not clear: a key still in flight has to stay wanted, or it is
        // dropped between the request and the reply.
        c.SeqEq(new[] { a }, remote.Wanted(), "reading the wanted set does not consume it");

        remote.WantFromRemote(b);
        c.Eq(2, remote.Wanted().Length, "a second request joins the set");
    }

    /// <summary>
    /// The in-flight cap holds keys back, and only the ones actually sent may be forgotten.
    /// Forgetting the rest strands them: the render path has already put them in
    /// LoadsInFlight, where the mesh scheduler skips them, and nothing would ever re-ask.
    /// </summary>
    static void OnlyForgetWhatWasSent(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long[] keys = Enumerable.Range(1, 5).Select(i => LodWorld.SectionKey(0, i, i)).ToArray();
        remote.AddRemoteKeys(keys);
        foreach (long key in keys) remote.WantFromRemote(key);

        // The transport sent only the first two.
        remote.MarkRequested(keys.Take(2));

        long[] stillWanted = remote.Wanted();
        c.Eq(3, stillWanted.Length, "keys held back by the cap stay wanted");
        c.False(stillWanted.Contains(keys[0]), "a sent key is forgotten");
        c.False(stillWanted.Contains(keys[1]), "the other sent key is forgotten");
        c.True(stillWanted.Contains(keys[2]), "an unsent key is still wanted");

        remote.MarkRequested(Array.Empty<long>());
        c.Eq(3, remote.Wanted().Length, "sending nothing forgets nothing");
    }

    static void Unavailable(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long key = LodWorld.SectionKey(0, 7, 7);
        remote.AddRemoteKeys(new[] { key });
        remote.WantFromRemote(key);
        world.LoadsInFlight.Add(key);

        remote.MarkUnavailable(key);

        c.False(remote.RemoteOnly.Contains(key), "a declined key stops being remote-only");
        c.Eq(0, remote.Wanted().Length, "a declined key stops being wanted");
        c.False(world.LoadsInFlight.Contains(key), "a declined key stops being in flight");
        c.True(world.LoadFailed.Contains(key), "a declined key is recorded as failed so it is not re-asked forever");

        // But if a local capture won the race and the section is already resident, recording
        // a failure would block reloading it after a future RAM eviction.
        var raced = new LodWorld();
        var racedRemote = new LodRemoteKeySet(raced);
        long resident = LodWorld.SectionKey(0, 3, 3);
        racedRemote.AddRemoteKeys(new[] { resident });
        raced.LoadsInFlight.Add(resident);
        raced.Sections[resident] = new LodSection();

        racedRemote.MarkUnavailable(resident);

        c.False(raced.LoadsInFlight.Contains(resident), "a resident section stops waiting");
        c.False(raced.LoadFailed.Contains(resident),
            "a resident section is not marked failed, so it can reload after eviction");
    }
}
