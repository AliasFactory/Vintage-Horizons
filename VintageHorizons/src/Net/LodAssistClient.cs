using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

/// <summary>
/// Client half of the optional server assist (DESIGN.md §10). Stage 1 is handshake only:
/// it establishes whether an assisting server is on the other end and reports that
/// through .vhinfo. No terrain moves yet.
///
/// The failure path is the one that matters. Most servers will never have this mod, and
/// on those the whole class must amount to a channel that is registered and never used.
/// So nothing is ever sent before <see cref="EnumChannelState.Connected"/>, and a server
/// that answers with something unexpected leaves the assist off rather than half on.
/// </summary>
public sealed class LodAssistClient
{
    readonly ICoreClientAPI capi;
    readonly ILogger logger;
    readonly string modVersion;
    IClientNetworkChannel? channel;

    /// <summary>Negotiated protocol, 0 until a usable welcome arrives.</summary>
    public int NegotiatedProtocol { get; private set; }

    /// <summary>True once a server has confirmed it will serve terrain.</summary>
    public bool Available => NegotiatedProtocol > 0;

    /// <summary>One line for .vhinfo. Always set to something a player can act on.</summary>
    public string Status { get; private set; } = "not connected yet";

    public LodAssistClient(ICoreClientAPI capi, ILogger logger, string modVersion)
    {
        this.capi = capi;
        this.logger = logger;
        this.modVersion = modVersion;
    }

    /// <summary>
    /// Call once during StartClientSide -- channel registration has to happen before the
    /// connection handshake, which is long before a world exists.
    /// </summary>
    public void Register()
    {
        channel = capi.Network.RegisterChannel(LodAssist.ChannelName)
            .RegisterMessageType<AssistHello>()
            .RegisterMessageType<AssistWelcome>()
            .SetMessageHandler<AssistWelcome>(OnWelcome);
    }

    /// <summary>
    /// Call once the world is up, when the channel state has settled. Never throws: the
    /// caller is a LevelFinalize handler, and an exception there aborts every remaining
    /// step of it -- which on a vanilla server would mean the optional feature breaking
    /// the mod for exactly the players it is supposed to leave alone.
    /// </summary>
    public void Greet()
    {
        if (channel == null) return;

        // IClientNetworkChannel.Connected, not INetworkAPI.GetChannelState: against a
        // vanilla server GetChannelState reports Connected while the channel is not,
        // and SendPacket then throws "Attempting to send data to a not connected
        // channel". Connected is what the engine's own error message says to test.
        if (!channel.Connected)
        {
            // Much the commonest outcome, and not a problem: it is a plain server.
            Status = "none (server does not have VintageHorizons)";
            logger.Debug("VintageHorizons: no server assist (channel state {0})",
                capi.Network.GetChannelState(LodAssist.ChannelName));
            return;
        }

        try
        {
            Status = "handshaking";
            channel.SendPacket(new AssistHello { Protocol = LodAssist.Protocol, ModVersion = modVersion });
        }
        catch (Exception e)
        {
            Status = "unavailable (handshake failed)";
            logger.Warning("VintageHorizons: server assist handshake failed, continuing without it: {0}", e);
        }
    }

    void OnWelcome(AssistWelcome msg)
    {
        // Deserialized, so every reference field is whatever the wire produced.
        string reason = msg.Status ?? "";
        string version = msg.ModVersion ?? "unknown";

        if (!msg.Enabled)
        {
            NegotiatedProtocol = 0;
            Status = reason.Length > 0
                ? $"server has VintageHorizons but the assist is off ({reason})"
                : "server has VintageHorizons but the assist is off";
            logger.Notification("VintageHorizons: {0}", Status);
            return;
        }

        // Take the lower of the two so neither side has to know what the other added.
        int negotiated = Math.Min(LodAssist.Protocol, msg.Protocol);
        if (negotiated < 1)
        {
            NegotiatedProtocol = 0;
            Status = $"server assist unusable (its protocol {msg.Protocol}, ours {LodAssist.Protocol})";
            logger.Warning("VintageHorizons: {0}", Status);
            return;
        }

        NegotiatedProtocol = negotiated;
        Status = $"connected to server {version} (protocol {negotiated})";
        logger.Notification("VintageHorizons: server assist {0}", Status);
    }

    /// <summary>Reset for the next world; the channel itself outlives the join.</summary>
    public void Reset()
    {
        NegotiatedProtocol = 0;
        Status = "not connected yet";
    }
}
