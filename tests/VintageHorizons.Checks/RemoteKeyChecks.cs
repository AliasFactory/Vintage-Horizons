namespace VintageHorizons.Checks;

/// <summary>
/// The sections that the client gets from a server, and not from its own disk.
///
/// The most expensive defect in the history of this project was here. It survived three
/// diagnoses that came from the counters. Those were the order of the fetches, the mesh
/// throughput, and children that cannot be covered.
///
/// Then a person printed the real state of the branch, and saw the cause in one attempt.
/// DESIGN.md records the lesson: instrument the decision, do not infer it. These checks are
/// the permanent form of that lesson.
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
    /// This is THE regression test.
    ///
    /// The registration of a fine key walks UPWARD. It adds each ancestor to HasDataSet, thus
    /// the quadtree can descend to that key. Those ancestors hold no data of their own. They
    /// are structure only.
    ///
    /// Thus a test of HasDataSet, to answer "can the local disk supply this?", says yes for a
    /// key that the local disk never held.
    ///
    /// The result was not one absent section. It was a permanent absence. The coarse key
    /// stayed out of RemoteOnly. It went to a local store with no such row. It returned null.
    /// Then it arrived in LoadFailed, which nothing clears.
    ///
    /// That terrain can never resolve, at any distance. The symptom was an L5 node with two
    /// children in the state "load-failed", and an idle pipeline.
    /// </summary>
    static void SynthesisedAncestors(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        // A fine key from the local disk. Its registration makes its full chain of
        // ancestors.
        long fine = LodWorld.SectionKey(0, 8, 8);
        remote.AddLocalKey(fine);
        world.InstallStoredKey(0, 8, 8, applyToParent: false);

        long coarse = LodWorld.ParentKey(LodWorld.ParentKey(fine));
        c.True(world.HasDataSet.Contains(coarse),
            "registering a fine key synthesises its ancestors into HasDataSet");
        c.True(LodWorld.KeyLevel(coarse) > 0, "the synthesised ancestor is genuinely coarser");

        // The server offers that same coarse key, and the server really holds it.
        int added = remote.AddRemoteKeys(new[] { coarse });

        c.Eq(1, added, "a server-held coarse key is accepted even though HasDataSet contains it");
        c.True(remote.RemoteOnly.Contains(coarse), "the coarse key is routed to the network, not local disk");
        c.True(remote.WantFromRemote(coarse), "asking to reload it goes to the network");

        // A key that the old defect damaged must also get another opportunity.
        long poisoned = LodWorld.SectionKey(2, 30, 30);
        world.LoadFailed.Add(poisoned);
        world.LoadsInFlight.Add(poisoned);
        remote.AddRemoteKeys(new[] { poisoned });

        c.False(world.LoadFailed.Contains(poisoned), "a previously failed key is un-failed once a source exists");
        c.False(world.LoadsInFlight.Contains(poisoned), "a stranded in-flight key is released");
        c.True(remote.RemoteOnly.Contains(poisoned), "the recovered key is fetchable");
    }

    /// <summary>
    /// A key that the local disk really holds must stay a local read. The capture of the
    /// client is what the client observed, and it includes the edits that the client saw.
    /// Thus it wins against the copy of the server.
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

        // An offer of the same key two times must not count it two times.
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

        // Wanted() must not clear the set. A key that is still in flight must stay wanted.
        // Without that, the mod loses it between the request and the answer.
        c.SeqEq(new[] { a }, remote.Wanted(), "reading the wanted set does not consume it");

        remote.WantFromRemote(b);
        c.Eq(2, remote.Wanted().Length, "a second request joins the set");
    }

    /// <summary>
    /// The in-flight limit holds some keys back. The mod can forget only the keys that it
    /// sent.
    ///
    /// If the mod forgets the others, those keys stop. The render path put them in
    /// LoadsInFlight already, where the mesh scheduler skips them, and nothing asks for them
    /// again.
    /// </summary>
    static void OnlyForgetWhatWasSent(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long[] keys = Enumerable.Range(1, 5).Select(i => LodWorld.SectionKey(0, i, i)).ToArray();
        remote.AddRemoteKeys(keys);
        foreach (long key in keys) remote.WantFromRemote(key);

        // The transport sent the first two keys only.
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

        // But a local capture can win the race, and then the section is resident already. A
        // record of a failure then stops a load after a later eviction from RAM.
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
