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

        // Enabled stays false until sections can actually move: reporting true here would
        // leave a client waiting for terrain that is not coming. The status line still
        // says whether a cache is being built, which is the difference between "this
        // server will be useful once transfer lands" and "capture is not running".
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        string status = capture?.Capturing == true
            ? $"building its LOD cache ({capture.SectionCount} sections so far); transfer is not implemented yet"
            : "no LOD cache is being built on this server";

        channel.SendPacket(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = Mod.Info.Version,
            Enabled = false,
            Status = status,
        }, fromPlayer);
    }
}
