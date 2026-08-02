using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Server half of the optional assist (DESIGN.md §10). Stage 1 answers the handshake and
/// nothing else, which is the point: it proves the mod can go Universal without changing
/// anything for anyone, before any terrain is put on the wire.
///
/// This is a separate ModSystem rather than a branch inside the client one. The client
/// system casts World to ClientMain, compiles shaders and registers a renderer; the
/// robust guarantee that none of that runs on a server is that the code is not there,
/// not a side check that one refactor could get wrong.
/// </summary>
public class LodAssistServerSystem : ModSystem
{
    ICoreServerAPI sapi = null!;
    IServerNetworkChannel channel = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        channel = api.Network.RegisterChannel(LodAssist.ChannelName)
            .RegisterMessageType<AssistHello>()
            .RegisterMessageType<AssistWelcome>()
            .RegisterMessageType<AssistKeyManifest>()
            .RegisterMessageType<AssistSectionRequest>()
            .RegisterMessageType<AssistSection>()
            .SetMessageHandler<AssistHello>(OnHello)
            .SetMessageHandler<AssistSectionRequest>(OnSectionRequest);

        // Once a second, not every tick: the per-second serve cap then IS the batch size,
        // with no token bucket to get subtly wrong.
        api.Event.RegisterGameTickListener(_ => ServePending(), 1000);

        api.ChatCommands.Create("vhserver")
            .WithDescription("VintageHorizons server assist status")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ =>
            {
                LodServerCaptureSystem? capture = api.ModLoader.GetModSystem<LodServerCaptureSystem>();
                LodServerConfig config = capture?.Config ?? new LodServerConfig();
                return TextCommandResult.Success(
                    $"[VintageHorizons] {config.Describe()}. Cache: {capture?.SectionCount ?? 0} sections, "
                    + $"{capture?.ColumnsCaptured ?? 0} columns captured. Served {sectionsServed} sections "
                    + $"({bytesServed / 1e6:0.0} MB, {(sectionsServed > 0 ? blobReadMs / sectionsServed : 0):0.00}ms avg read), "
                    + $"{sectionsOutsideRadius} refused as out of radius, {sectionsRefused} refused unserved, "
                    + $"{pendingByPlayer.Count} players waiting. "
                    + (capture?.SweepStatus is string sw ? sw + ". " : "")
                    + (capture?.PregenStatus is string pg ? pg + ". " : "")
                    + (capture?.GenerateStatus is string gn ? gn + ". " : "")
                    + "Settings live in ModConfig/vintagehorizons-server.json (restart to apply).");
            });

        Mod.Logger.Notification(
            "VintageHorizons {0} server assist listening. Players without the mod are "
            + "unaffected and do not need to install anything.",
            Mod.Info.Version);
    }

    void OnHello(IServerPlayer fromPlayer, AssistHello msg)
    {
        Mod.Logger.Debug("VintageHorizons: assist hello from {0} (client {1}, protocol {2})",
            fromPlayer.PlayerName, msg.ModVersion, msg.Protocol);

        // Answered from the main thread, one tick later, rather than from here. Message
        // handlers do not run on the main thread, and both the key set and its count come
        // from a HashSet the capture pipeline mutates every tick - reading it here is a
        // torn read, and the count would disagree with the manifest that follows it
        // (observed: announced 5634, sent 5638, four sections captured in between).
        sapi.Event.EnqueueMainThreadTask(() => Answer(fromPlayer), "vintagehorizons-hello");
    }

    /// <summary>
    /// Welcome plus the key manifest, from one snapshot so the announced count is a fact
    /// rather than an estimate. Enabled stays false until sections can actually move:
    /// reporting true would leave a client waiting for terrain that is not coming.
    /// </summary>
    void Answer(IServerPlayer player)
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        LodServerConfig config = capture?.Config ?? new LodServerConfig();
        bool serving = capture?.Capturing == true && config.EnableServing;
        long[] keys = serving ? capture!.SnapshotKeys() : Array.Empty<long>();

        channel.SendPacket(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = Mod.Info.Version,
            Enabled = keys.Length > 0,
            Status = keys.Length > 0
                ? $"serving from {keys.Length} cached sections"
                  + (config.ServeRadiusBlocks > 0 ? $" within {config.ServeRadiusBlocks} blocks" : "")
                : capture?.Capturing != true
                    ? "no LOD cache is being built on this server"
                    : "this server has a LOD cache but is not sharing it",
            ManifestKeyCount = keys.Length,
        }, player);

        if (keys.Length > 0) SendManifest(player, keys);
    }

    /// <summary>
    /// Pending section requests, per player, oldest first. Held here rather than answered
    /// inline so the per-second cap has something to meter, and so a player who asks for a
    /// hundred sections gets them steadily instead of in one spike.
    /// </summary>
    readonly Dictionary<string, Queue<long>> pendingByPlayer = new();

    void OnSectionRequest(IServerPlayer fromPlayer, AssistSectionRequest msg)
    {
        if (msg.Keys == null || msg.Keys.Length == 0) return;

        // Onto the main thread for the same reason as the manifest: this touches shared
        // state, and the blob read has to be ordered against the capture that writes it.
        long[] keys = msg.Keys;
        string uid = fromPlayer.PlayerUID;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (!pendingByPlayer.TryGetValue(uid, out Queue<long>? queue))
            {
                pendingByPlayer[uid] = queue = new Queue<long>();
            }

            // Bounded: the client is supposed to limit itself, but a server must not
            // depend on a client behaving.
            int room = Math.Max(0, MaxQueuedPerPlayer - queue.Count);
            foreach (long key in keys.Take(room)) queue.Enqueue(key);

            // Anything past the cap is refused OUT LOUD. This used to drop silently,
            // with a comment saying the client would re-ask - it cannot. A client marks
            // a key in flight when it sends it and only forgets it when a reply comes
            // back, so a dropped key is stranded for the session. With a 16-key in-flight
            // cap that is the whole client, permanently stuck. Same rule as M7 stage 4:
            // only keys actually answered may be forgotten.
            foreach (long key in keys.Skip(room)) Refuse(fromPlayer, key);
        }, "vintagehorizons-request");
    }

    const int MaxQueuedPerPlayer = 256;

    /// <summary>
    /// Tell a client we will not serve this key. An empty blob is the refusal, and the
    /// client needs it: silence is indistinguishable from a lost packet, and it leaves
    /// the key in flight forever.
    /// </summary>
    void Refuse(IServerPlayer player, long key)
    {
        channel.SendPacket(new AssistSection { Key = key }, player);
        sectionsRefused++;
    }

    int sectionsRefused;

    /// <summary>
    /// Serve at most the per-second cap to each waiting player. Called once a second, so
    /// the cap is simply the batch size - no token bucket to get wrong.
    /// </summary>
    void ServePending()
    {
        if (pendingByPlayer.Count == 0) return;

        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        if (capture?.Capturing != true || !capture.Config.EnableServing)
        {
            // Refuse every queued key rather than dropping them. This path runs when the
            // cache is not open yet, or when serving is off, and it used to clear the
            // queues in silence. A client whose keys vanish here never asks again: it
            // holds them in flight waiting for a reply that will never come, and its
            // in-flight cap then blocks every later request for the whole session.
            //
            // A race at join time makes that reachable in ordinary play - the client can
            // ask before the server's pipeline is open. It fits the intermittent stall
            // seen on 2026-08-02, though that was never caught with logging in place, so
            // this is a defect fixed on its own merits rather than a proven diagnosis.
            foreach ((string uid, Queue<long> queue) in pendingByPlayer)
            {
                if (sapi.World.PlayerByUid(uid) is not IServerPlayer waiting) continue;
                foreach (long key in queue) Refuse(waiting, key);
            }
            pendingByPlayer.Clear();
            return;
        }

        LodServerConfig config = capture.Config;

        // Round-robin from a rotating start, so the global budget below cannot be
        // monopolised by whichever player happens to sort first in the dictionary.
        List<string> uids = pendingByPlayer.Keys.ToList();
        uids.Sort(StringComparer.Ordinal);
        int start = uids.Count == 0 ? 0 : (int)(serveRound++ % (uint)uids.Count);

        int globalBudget = config.MaxSectionsPerSecondTotal;
        List<string>? emptied = null;

        for (int n = 0; n < uids.Count && globalBudget > 0; n++)
        {
            string uid = uids[(start + n) % uids.Count];
            Queue<long> queue = pendingByPlayer[uid];

            if (sapi.World.PlayerByUid(uid) is not IServerPlayer player
                || player.ConnectionState != EnumClientState.Playing)
            {
                (emptied ??= new List<string>()).Add(uid);
                continue;
            }

            int budget = Math.Min(config.MaxSectionsPerSecondPerPlayer, globalBudget);
            while (budget-- > 0 && queue.Count > 0)
            {
                long key = queue.Dequeue();

                // Radius is checked here, against where the player is NOW, rather than when
                // the request was queued: a request that waited in the queue must not be
                // honoured for somewhere the player has since left.
                if (!WithinServeRadius(key, player, config.ServeRadiusBlocks))
                {
                    channel.SendPacket(new AssistSection { Key = key }, player);
                    sectionsOutsideRadius++;
                    globalBudget--;
                    continue;
                }

                serveClock.Restart();
                byte[] blob = capture.LoadBlob(key) ?? Array.Empty<byte>();
                blobReadMs += serveClock.Elapsed.TotalMilliseconds;

                // Empty blob rather than silence for a miss: the client needs to know to
                // stop asking, and cannot tell "declined" from "lost" otherwise.
                channel.SendPacket(new AssistSection { Key = key, Blob = blob }, player);
                sectionsServed++;
                bytesServed += blob.Length;
                globalBudget--;
            }

            if (queue.Count == 0) (emptied ??= new List<string>()).Add(uid);
        }

        if (emptied != null) foreach (string uid in emptied) pendingByPlayer.Remove(uid);

        // Report what serving actually costs the tick, so the caps above can be judged
        // against a measurement instead of an estimate.
        if (sectionsServed - lastReportedServed >= 200)
        {
            lastReportedServed = sectionsServed;
            Mod.Logger.Notification(
                "Assist served {0} sections ({1:0.0} MB), blob reads {2:0.00}ms total, {3:0.00}ms avg",
                sectionsServed, bytesServed / 1e6, blobReadMs, blobReadMs / sectionsServed);
        }
    }

    /// <summary>
    /// Nearest-edge distance from the player to the section, not centre-to-centre: an L6
    /// section spans 4096 blocks, so centre distance would refuse sections the player is
    /// standing inside.
    /// </summary>
    /// <summary>
    /// Radius check for a player. Separate from the math below so the deref of a player
    /// who has no entity yet (mid-join) keeps its own answer: with an unlimited radius
    /// there is nothing to compare against, so the absent position does not matter.
    /// </summary>
    static bool WithinServeRadius(long key, IServerPlayer player, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        var pos = player.Entity?.Pos;
        if (pos == null) return false;

        return WithinServeRadius(key, pos.X, pos.Z, radiusBlocks);
    }

    public static bool WithinServeRadius(long key, double x, double z, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz <= (double)radiusBlocks * radiusBlocks;
    }

    readonly System.Diagnostics.Stopwatch serveClock = new();
    long sectionsOutsideRadius;
    uint serveRound;
    long sectionsServed, lastReportedServed, bytesServed;
    double blobReadMs;

    /// <summary>Keys the server holds, in chunks. Main thread only.</summary>
    void SendManifest(IServerPlayer player, long[] keys)
    {
        int sent = 0, sequence = 0;
        while (sent < keys.Length)
        {
            int take = Math.Min(LodAssist.ManifestKeysPerMessage, keys.Length - sent);
            var chunk = new long[take];
            Array.Copy(keys, sent, chunk, 0, take);
            sent += take;

            channel.SendPacket(new AssistKeyManifest
            {
                Sequence = sequence++,
                Last = sent >= keys.Length,
                Keys = chunk,
            }, player);
        }

        Mod.Logger.Debug("VintageHorizons: sent {0} keys to {1} in {2} chunks",
            keys.Length, player.PlayerName, sequence);
    }
}
