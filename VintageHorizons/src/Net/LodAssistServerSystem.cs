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
            .SetMessageHandler<AssistHello>(OnHello);

        Mod.Logger.Notification(
            "VintageHorizons {0} server assist listening (stage 1: handshake only, no terrain served). "
            + "Players without the mod are unaffected and do not need to install anything.",
            Mod.Info.Version);
    }

    void OnHello(IServerPlayer fromPlayer, AssistHello msg)
    {
        Mod.Logger.Debug("VintageHorizons: assist hello from {0} (client {1}, protocol {2})",
            fromPlayer.PlayerName, msg.ModVersion, msg.Protocol);

        // Answered from the main thread, one tick later, rather than from here. Message
        // handlers do not run on the main thread, and both the key set and its count come
        // from a HashSet the capture pipeline mutates every tick — reading it here is a
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
        long[] keys = capture?.Capturing == true ? capture.SnapshotKeys() : Array.Empty<long>();

        channel.SendPacket(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = Mod.Info.Version,
            Enabled = false,
            Status = keys.Length > 0
                ? $"holds {keys.Length} sections; transfer is not implemented yet"
                : "no LOD cache is being built on this server",
            ManifestKeyCount = keys.Length,
        }, player);

        if (keys.Length > 0) SendManifest(player, keys);
    }

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
