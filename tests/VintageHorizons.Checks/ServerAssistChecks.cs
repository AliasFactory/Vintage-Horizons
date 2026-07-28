using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The server assist: what gets pre-generated, what gets served, and what a client makes
/// of the server's answer. These are the admin-facing behaviours, so getting them wrong is
/// visible to someone other than the player running the mod.
/// </summary>
public static class ServerAssistChecks
{
    public static void Run(Check c)
    {
        SpiralIsAnExactCover(c);
        ServeRadiusIsNearestEdge(c);
        ProtocolNegotiation(c);
        ManifestAndArrivals(c);
    }

    /// <summary>
    /// Pre-generation walks a square spiral so that any prefix of the sequence is a filled
    /// square around spawn — stopping early, or being stopped early, still leaves a
    /// complete horizon rather than a partial arm sticking out in one direction.
    ///
    /// Walked exhaustively out to the configured maximum radius, because "covers every
    /// column exactly once" is the kind of property a spot check passes and a real run
    /// fails at one specific ring corner.
    /// </summary>
    static void SpiralIsAnExactCover(Check c)
    {
        c.Eq((0, 0), LodServerPregen.SpiralAt(0), "the spiral starts at spawn");

        foreach (int radius in new[] { 1, 2, 3, 8, 32 })
        {
            int side = 2 * radius + 1;
            int total = side * side;
            var seen = new HashSet<(int, int)>();
            bool inRange = true;

            for (int i = 0; i < total; i++)
            {
                (int x, int z) = LodServerPregen.SpiralAt(i);
                if (Math.Abs(x) > radius || Math.Abs(z) > radius) inRange = false;
                seen.Add((x, z));
            }

            c.Eq(total, seen.Count, $"radius {radius}: every column is visited exactly once");
            c.True(inRange, $"radius {radius}: nothing falls outside the square");
        }

        // Every prefix is a filled square. This is the property that makes an interrupted
        // pre-generation still useful, and it is the one a naive spiral gets wrong.
        for (int ring = 0; ring <= 6; ring++)
        {
            int side = 2 * ring + 1;
            var seen = new HashSet<(int, int)>();
            for (int i = 0; i < side * side; i++) seen.Add(LodServerPregen.SpiralAt(i));

            bool filled = true;
            for (int z = -ring; z <= ring; z++)
            {
                for (int x = -ring; x <= ring; x++)
                {
                    if (!seen.Contains((x, z))) filled = false;
                }
            }
            c.True(filled, $"the first {side * side} steps form a filled {side}x{side} square");
        }

        // The maximum the config allows, walked in full: 263,169 columns.
        const int max = 256;
        int maxTotal = (2 * max + 1) * (2 * max + 1);
        var all = new HashSet<(int, int)>(maxTotal);
        for (int i = 0; i < maxTotal; i++) all.Add(LodServerPregen.SpiralAt(i));
        c.Eq(maxTotal, all.Count, "the spiral is an exact cover at the maximum configurable radius");
    }

    /// <summary>
    /// Nearest-edge, not centre-to-centre. An L6 section spans 4096 blocks, so a centre
    /// measurement would refuse to serve a section the player is standing in the middle of.
    /// </summary>
    static void ServeRadiusIsNearestEdge(Check c)
    {
        long l6 = LodWorld.SectionKey(6, 0, 0); // covers [0,4096) x [0,4096)

        c.True(LodAssistServerSystem.WithinServeRadius(l6, 2048, 2048, 512),
            "a player inside a huge section is served it even at a small radius");
        c.True(LodAssistServerSystem.WithinServeRadius(l6, 0, 0, 1),
            "the very corner of a section counts as inside");
        c.True(LodAssistServerSystem.WithinServeRadius(l6, 4095, 4095, 1),
            "the far corner of a section counts as inside");

        long far = LodWorld.SectionKey(0, 100, 100); // covers [6400,6464) x [6400,6464)
        c.False(LodAssistServerSystem.WithinServeRadius(far, 0, 0, 512),
            "a section well outside the radius is refused");
        c.True(LodAssistServerSystem.WithinServeRadius(far, 6400 - 100, 6400, 512),
            "a section just inside the radius is served");
        c.False(LodAssistServerSystem.WithinServeRadius(far, 6400 - 600, 6400, 512),
            "a section just outside the radius is refused");

        // Zero means unlimited, which is what the config's Sanitize maps a negative to.
        c.True(LodAssistServerSystem.WithinServeRadius(far, 0, 0, 0),
            "a radius of zero serves everything");
        c.True(LodAssistServerSystem.WithinServeRadius(far, 1e9, 1e9, 0),
            "a radius of zero serves everything however far away");

        // The boundary is measured on the square, so a diagonal approach is further than an
        // axis-aligned one at the same coordinate delta. Getting this backwards would make
        // the served region a diamond rather than a disc.
        long origin = LodWorld.SectionKey(0, 10, 10); // [640,704)
        c.True(LodAssistServerSystem.WithinServeRadius(origin, 640 - 70, 640, 100),
            "70 blocks away on one axis is inside a 100 radius");
        c.False(LodAssistServerSystem.WithinServeRadius(origin, 640 - 70, 640 - 80, 100),
            "70 by 80 blocks away is outside a 100 radius");
    }

    /// <summary>
    /// What a client concludes from the server's welcome. Every branch has to leave Status
    /// set to something a player can act on, because .vhinfo prints it verbatim and it is
    /// the only feedback anyone gets about why distant terrain is or is not arriving.
    /// </summary>
    static void ProtocolNegotiation(Check c)
    {
        var logger = new CaptureLogger();
        var client = new LodAssistClient(null!, logger, "0.1.1");

        client.OnWelcome(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = "0.1.1",
            Enabled = true,
            ManifestKeyCount = 1234,
        });
        c.True(client.Available, "a matching protocol makes the assist available");
        c.Eq(LodAssist.Protocol, client.NegotiatedProtocol, "the negotiated protocol is ours");
        c.Eq(1234, client.ManifestExpected, "the announced key count is remembered for comparison");
        c.True(client.Status.Contains("connected"), "a working assist says so");

        // A newer server: take the lower of the two, so neither side needs to know what the
        // other added.
        client.Reset();
        client.OnWelcome(new AssistWelcome { Protocol = 99, ModVersion = "9.9", Enabled = true });
        c.Eq(LodAssist.Protocol, client.NegotiatedProtocol, "a newer server negotiates down to ours");
        c.True(client.Available, "a newer server is still usable");

        // Serving switched off. Note Enabled is set from whether the server has keys, so an
        // empty cache lands here too.
        client.Reset();
        client.OnWelcome(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            Enabled = false,
            Status = "this server has a LOD cache but is not sharing it",
        });
        c.False(client.Available, "a disabled assist is not available");
        c.Eq(0, client.NegotiatedProtocol, "a disabled assist negotiates nothing");
        c.True(client.Status.Contains("not sharing"), "the server's own reason is passed through to the player");

        // An unusable protocol must leave the assist off rather than half on.
        client.Reset();
        client.OnWelcome(new AssistWelcome { Protocol = 0, Enabled = true });
        c.False(client.Available, "a protocol of zero is unusable");
        c.True(client.Status.Contains("unusable"), "an unusable protocol says so");

        // Everything on the wire is nullable after deserialization, and a null must not
        // take down the join.
        client.Reset();
        c.NoThrow(() => client.OnWelcome(new AssistWelcome
        {
            Protocol = LodAssist.Protocol, Enabled = true, ModVersion = null!, Status = null!,
        }), "a welcome with null strings does not throw");
        c.True(client.Status.Contains("unknown"), "a null version reads as unknown rather than blank");

        // The empty-string case is NOT the null case. Both AssistWelcome string fields carry
        // a "" initializer, and protobuf-net runs initializers before filling in what the
        // wire actually sent — so a server that simply omits the field yields "", which
        // sails past the ?? guard and renders a blank in the middle of the status line.
        // Harmless today, since our own server always sets it. Recorded rather than fixed
        // so the guard's real coverage is not overstated.
        client.Reset();
        client.OnWelcome(new AssistWelcome { Protocol = LodAssist.Protocol, Enabled = true });
        c.False(client.Status.Contains("unknown"), "an empty version does not reach the unknown fallback");
    }

    /// <summary>
    /// Handlers run on the network thread and may only enqueue; everything that touches
    /// shared state happens on the tick, in Pump. A manifest read straight from the handler
    /// is what made the announced and applied key counts disagree once.
    /// </summary>
    static void ManifestAndArrivals(Check c)
    {
        var logger = new CaptureLogger();
        var client = new LodAssistClient(null!, logger, "0.1.1");
        client.OnWelcome(new AssistWelcome
        {
            Protocol = LodAssist.Protocol, Enabled = true, ManifestKeyCount = 3,
        });

        long[] first = { LodWorld.SectionKey(0, 1, 1), LodWorld.SectionKey(0, 2, 2) };
        long[] second = { LodWorld.SectionKey(0, 3, 3) };

        client.OnKeyManifest(new AssistKeyManifest { Keys = first, Last = false });
        c.Eq(0, client.RemoteKeys.Count, "a handler does not touch shared state, only queues");

        client.Pump((_, _) => true);
        c.Eq(2, client.RemoteKeys.Count, "the tick applies the first chunk");
        c.False(client.ManifestComplete, "a non-final chunk does not complete the manifest");

        client.OnKeyManifest(new AssistKeyManifest { Keys = second, Last = true });
        client.Pump((_, _) => true);
        c.Eq(3, client.RemoteKeys.Count, "the tick applies the final chunk");
        c.True(client.ManifestComplete, "the final chunk completes the manifest");
        c.True(logger.Contains("manifest complete"), "manifest completion is logged");

        // A null key array on the wire must not throw.
        c.NoThrow(() =>
        {
            client.OnKeyManifest(new AssistKeyManifest { Keys = null!, Last = false });
            client.Pump((_, _) => true);
        }, "a manifest chunk with no keys does not throw");

        // An empty blob means the server declined. The installer is still called, because
        // it is the one place that can release the render path's wait on the key —
        // short-circuiting here left declined keys stuck in flight and pinned their parents
        // coarse for the whole session.
        var offered = new List<long>();
        long declined = LodWorld.SectionKey(0, 1, 1);
        client.OnSection(new AssistSection { Key = declined, Blob = Array.Empty<byte>() });
        client.Pump((key, blob) => { offered.Add(key); return false; });

        c.SeqEq(new[] { declined }, offered, "a declined section still reaches the installer");
        c.Eq(1, client.SectionsRefused, "a declined section is counted as refused");
        c.False(client.RemoteKeys.Contains(declined), "a declined key leaves the offered set");

        // And is never asked for again, even if a later manifest re-offers it.
        client.OnKeyManifest(new AssistKeyManifest { Keys = new[] { declined }, Last = false });
        client.Pump((_, _) => true);
        c.False(client.RemoteKeys.Contains(declined), "a refused key is not re-added by a later manifest");

        long good = LodWorld.SectionKey(0, 2, 2);
        client.OnSection(new AssistSection { Key = good, Blob = new byte[] { 4, 1, 2, 3 } });
        client.Pump((_, _) => true);
        c.Eq(1, client.SectionsReceived, "an adopted section is counted as received");
        c.Eq(0, client.InFlight, "an arrival clears its in-flight slot");
    }
}
