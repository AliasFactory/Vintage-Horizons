using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// The server half of the optional assist (DESIGN.md section 10).
///
/// Stage 1 answers the handshake and does nothing else. That is the purpose. It proves that
/// the mod can become Universal and change nothing for any user, before any terrain goes
/// onto the wire.
///
/// This is a separate ModSystem, and not a branch inside the client system. The client
/// system casts World to ClientMain, compiles shaders and registers a renderer. The strong
/// guarantee that none of that runs on a server is that the code is not there. A test of the
/// side is weaker, because one refactor can make it incorrect.
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

        // One time each second, and not in each tick. Thus the serve limit for each second
        // is also the batch size. There is no token bucket that can be incorrect.
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
                    + $"{sectionsOutsideRadius} refused as out of radius, {pendingByPlayer.Count} players waiting. "
                    + (capture?.SweepStatus is string sw ? sw + ". " : "")
                    + (capture?.PregenStatus is string pg ? pg + ". " : "")
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

        // The answer comes from the main thread, one tick later, and not from here. A
        // message handler does not run on the main thread. The key set and its count both
        // come from a HashSet that the capture pipeline changes in each tick.
        //
        // Thus a read here is a torn read, and the count does not agree with the manifest
        // that follows it. This was observed: the mod announced 5634 and sent 5638, because
        // it captured four sections between the two.
        sapi.Event.EnqueueMainThreadTask(() => Answer(fromPlayer), "vintagehorizons-hello");
    }

    /// <summary>
    /// The welcome message and the key manifest, from one snapshot. Thus the announced count
    /// is a fact, and not an estimate.
    ///
    /// Enabled stays false until sections can move. A value of true leaves a client that
    /// waits for terrain that does not arrive.
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
    /// The section requests that wait, for each player, with the oldest first.
    ///
    /// The mod holds them here, and it does not answer them immediately. Thus the limit for
    /// each second has something to measure. A player who asks for one hundred sections also
    /// gets them at a constant rate, and not in one large group.
    /// </summary>
    readonly Dictionary<string, Queue<long>> pendingByPlayer = new();

    void OnSectionRequest(IServerPlayer fromPlayer, AssistSectionRequest msg)
    {
        if (msg.Keys == null || msg.Keys.Length == 0) return;

        // Move to the main thread, for the same reason as the manifest. This code touches
        // shared state. The blob read must also have an order against the capture that
        // writes it.
        long[] keys = msg.Keys;
        string uid = fromPlayer.PlayerUID;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (!pendingByPlayer.TryGetValue(uid, out Queue<long>? queue))
            {
                pendingByPlayer[uid] = queue = new Queue<long>();
            }

            // This queue has a limit. The client limits itself, but a server must not
            // depend on the behaviour of a client. Above the limit, the mod drops the newest
            // requests, and the client asks again later.
            int room = Math.Max(0, MaxQueuedPerPlayer - queue.Count);
            foreach (long key in keys.Take(room)) queue.Enqueue(key);
        }, "vintagehorizons-request");
    }

    const int MaxQueuedPerPlayer = 256;

    /// <summary>
    /// Give at most the limit for one second to each player that waits. The mod calls this
    /// one time each second. Thus the limit is the batch size, and there is no token bucket
    /// that can be incorrect.
    /// </summary>
    void ServePending()
    {
        if (pendingByPlayer.Count == 0) return;

        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        if (capture?.Capturing != true || !capture.Config.EnableServing)
        {
            pendingByPlayer.Clear();
            return;
        }

        LodServerConfig config = capture.Config;

        // Go through the players in turn, from a start point that rotates. Thus the player
        // that sorts first in the dictionary cannot take the full budget below.
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

                // Test the radius here, against the position of the player NOW. Do not use
                // the position from the time of the request. A request that waited in the
                // queue must not be honoured for a place that the player left.
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

                // Send an empty blob for a miss, and not silence. The client must know to
                // stop asking. Without an answer, the client cannot separate "declined" from
                // "lost".
                channel.SendPacket(new AssistSection { Key = key, Blob = blob }, player);
                sectionsServed++;
                bytesServed += blob.Length;
                globalBudget--;
            }

            if (queue.Count == 0) (emptied ??= new List<string>()).Add(uid);
        }

        if (emptied != null) foreach (string uid in emptied) pendingByPlayer.Remove(uid);

        // Report the real cost of the serving to the tick. Thus a person can judge the
        // limits above against a measurement, and not against an estimate.
        if (sectionsServed - lastReportedServed >= 200)
        {
            lastReportedServed = sectionsServed;
            Mod.Logger.Notification(
                "Assist served {0} sections ({1:0.0} MB), blob reads {2:0.00}ms total, {3:0.00}ms avg",
                sectionsServed, bytesServed / 1e6, blobReadMs, blobReadMs / sectionsServed);
        }
    }

    /// <summary>
    /// The distance from the player to the nearest edge of the section. This is not the
    /// distance from center to center. An L6 section covers 4096 blocks. Thus a center
    /// distance refuses a section that the player is inside.
    /// </summary>
    /// <summary>
    /// The radius test for a player.
    ///
    /// This is separate from the arithmetic below. A player in the middle of a join has no
    /// entity yet, and this method keeps its own answer for that case. With an unlimited
    /// radius there is nothing to compare against, thus the absent position has no
    /// effect.
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

    /// <summary>The keys that the server holds, in parts. Use this on the main thread
    /// only.</summary>
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
