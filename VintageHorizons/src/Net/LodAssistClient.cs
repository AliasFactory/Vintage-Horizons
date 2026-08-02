using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

/// <summary>
/// Takes a section that arrived from the server. The result is false when the mod did not
/// take it. That occurs when the client has local data for that key already, or when the mod
/// cannot parse the blob.
/// </summary>
public delegate bool LodForeignSectionInstaller(long key, byte[] blob);

/// <summary>
/// The client half of the optional server assist (DESIGN.md section 10).
///
/// Stage 1 is the handshake only. It finds whether a server with the assist is at the other
/// end, and it reports that through `.vhinfo`. No terrain moves yet.
///
/// The failure path is the important one. Most servers never have this mod. On those
/// servers, this full class must be a channel that the mod registers and never uses.
///
/// Thus the mod sends nothing before <see cref="EnumChannelState.Connected"/>. And when a
/// server gives an answer that the mod does not expect, the assist stays off. It does not go
/// into a partly-on state.
/// </summary>
public sealed class LodAssistClient
{
    readonly ICoreClientAPI capi;
    readonly ILogger logger;
    readonly string modVersion;
    IClientNetworkChannel? channel;

    /// <summary>The protocol that both sides agreed on. It is 0 until a usable welcome
    /// arrives.</summary>
    public int NegotiatedProtocol { get; private set; }

    /// <summary>True after a server confirms that it gives terrain.</summary>
    public bool Available => NegotiatedProtocol > 0;

    /// <summary>One line for `.vhinfo`. It always holds text that helps a player
    /// decide.</summary>
    public string Status { get; private set; } = "not connected yet";

    public LodAssistClient(ICoreClientAPI capi, ILogger logger, string modVersion)
    {
        this.capi = capi;
        this.logger = logger;
        this.modVersion = modVersion;
    }

    /// <summary>
    /// Call this one time during StartClientSide. The registration of a channel must occur
    /// before the connection handshake, and that is long before a world exists.
    /// </summary>
    public void Register()
    {
        channel = capi.Network.RegisterChannel(LodAssist.ChannelName)
            .RegisterMessageType<AssistHello>()
            .RegisterMessageType<AssistWelcome>()
            .RegisterMessageType<AssistKeyManifest>()
            .RegisterMessageType<AssistSectionRequest>()
            .RegisterMessageType<AssistSection>()
            .SetMessageHandler<AssistWelcome>(OnWelcome)
            .SetMessageHandler<AssistKeyManifest>(OnKeyManifest)
            .SetMessageHandler<AssistSection>(OnSection);
    }

    /// <summary>
    /// Call this after the world starts, when the channel state is stable.
    ///
    /// This method never throws. The caller is a LevelFinalize handler, and an exception
    /// there stops each remaining step of that handler. On a vanilla server that means the
    /// optional function breaks the mod, for exactly the players that it must not
    /// affect.
    /// </summary>
    public void Greet()
    {
        if (channel == null) return;

        // Use IClientNetworkChannel.Connected, and not INetworkAPI.GetChannelState.
        // Against a vanilla server, GetChannelState reports Connected while the channel is
        // not connected. Then SendPacket throws "Attempting to send data to a not connected
        // channel". The error message of the engine names Connected as the correct test.
        if (!channel.Connected)
        {
            // This is the most common result, and it is not a problem. The server is a
            // plain server.
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

    internal void OnWelcome(AssistWelcome msg)
    {
        // This object came from a deserialize. Thus each reference field holds what the
        // wire gave.
        string reason = msg.Status ?? "";
        string version = msg.ModVersion ?? "unknown";
        ManifestExpected = msg.ManifestKeyCount;

        if (!msg.Enabled)
        {
            NegotiatedProtocol = 0;
            Status = reason.Length > 0
                ? $"server has VintageHorizons but the assist is off ({reason})"
                : "server has VintageHorizons but the assist is off";
            logger.Notification("VintageHorizons: {0}", Status);
            return;
        }

        // Take the lower of the two values. Thus neither side must know what the other
        // side added.
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

    /// <summary>
    /// The keys that the server holds.
    ///
    /// The game tick reads this set and changes it, through <see cref="Pump"/>. The handlers
    /// below run on the thread that the engine uses for packets. A plain HashSet that both
    /// use is a race, whether or not a test shows it.
    ///
    /// Thus each fact that a handler learns goes into a concurrent queue, and the tick
    /// applies it. This also puts each install in the one place that can touch
    /// LodWorld.
    /// </summary>
    public readonly HashSet<long> RemoteKeys = new();

    readonly ConcurrentQueue<(long[] Keys, bool Last)> manifestChunks = new();

    /// <summary>True after the mod applies the last part of the manifest.</summary>
    public bool ManifestComplete { get; private set; }

    /// <summary>The count that the server announced, for a comparison with what
    /// arrived.</summary>
    public int ManifestExpected { get; private set; }

    internal void OnKeyManifest(AssistKeyManifest msg) =>
        manifestChunks.Enqueue((msg.Keys ?? Array.Empty<long>(), msg.Last));

    // ---- Section transfer ----

    /// <summary>The sections that arrived and that wait for the tick. An empty blob means
    /// that the server declined the key.</summary>
    readonly ConcurrentQueue<(long Key, byte[] Blob)> Arrived = new();

    readonly HashSet<long> inFlight = new();

    /// <summary>The keys that the server declined, or that it does not have now. The mod
    /// never asks for them again.</summary>
    readonly HashSet<long> refused = new();

    public int InFlight => inFlight.Count;
    public int SectionsReceived { get; private set; }
    public int SectionsRefused => refused.Count;

    /// <summary>
    /// Ask for the sections that the server has and this client does not, up to the limit
    /// for sections in flight.
    ///
    /// The game tick calls this with the keys that the quadtree wants. Thus the order of the
    /// fetches follows what the player can see. It does not follow the arbitrary order of the
    /// manifest.
    /// </summary>
    public long[] Request(IEnumerable<long> wanted)
    {
        if (!Available || channel == null || !channel.Connected) return Array.Empty<long>();

        List<long>? batch = null;
        foreach (long key in wanted)
        {
            if (inFlight.Count >= LodAssist.MaxSectionsInFlight) break;
            if (!RemoteKeys.Contains(key) || refused.Contains(key) || !inFlight.Add(key)) continue;
            (batch ??= new List<long>()).Add(key);
        }

        if (batch == null) return Array.Empty<long>();

        long[] sent = batch.ToArray();
        try
        {
            channel.SendPacket(new AssistSectionRequest { Keys = sent });
            SectionsRequested += sent.Length;
            return sent;
        }
        catch (Exception e)
        {
            foreach (long key in batch) inFlight.Remove(key);
            logger.Warning("VintageHorizons: section request failed: {0}", e);
            return Array.Empty<long>();
        }
    }

    public int SectionsRequested { get; private set; }

    internal void OnSection(AssistSection msg) =>
        Arrived.Enqueue((msg.Key, msg.Blob ?? Array.Empty<byte>()));

    /// <summary>
    /// Apply each item that the handlers put into the queue. This runs on the game tick.
    ///
    /// The mod gives each section that arrived to <paramref name="install"/>, and that
    /// delegate returns whether the mod took the section. It declines a section that the
    /// client has locally already, because the local capture wins (section 10.5).
    /// </summary>
    public void Pump(LodForeignSectionInstaller install)
    {
        while (manifestChunks.TryDequeue(out (long[] Keys, bool Last) chunk))
        {
            foreach (long key in chunk.Keys)
            {
                if (!refused.Contains(key)) RemoteKeys.Add(key);
            }

            if (!chunk.Last) continue;

            ManifestComplete = true;
            // Compare the announced count with the applied count. A difference means that
            // the server captured or evicted keys during the send. That is normal on a live
            // server. But it is worth a message, because the transfer trusts this set.
            logger.Notification(
                "VintageHorizons: server key manifest complete - {0} keys received{1}",
                RemoteKeys.Count,
                ManifestExpected > 0 && ManifestExpected != RemoteKeys.Count
                    ? $" (server announced {ManifestExpected})" : "");
        }

        while (Arrived.TryDequeue(out (long Key, byte[] Blob) got))
        {
            inFlight.Remove(got.Key);

            // Call install for an empty blob also. Thus the one place that knows a key is
            // unavailable can also stop the wait of the render path.
            //
            // An early exit here left a declined key in LodWorld.LoadsInFlight for the
            // session. That kept its parent coarse.
            if (install(got.Key, got.Blob))
            {
                SectionsReceived++;
                continue;
            }

            // The server declined the key, or the key is gone, or the client holds it
            // locally already. A record of that fact stops the mod from asking in each tick,
            // forever, for something that never arrives.
            refused.Add(got.Key);
            RemoteKeys.Remove(got.Key);
        }
    }

    /// <summary>Reset for the next world. The channel itself continues after the
    /// join.</summary>
    public void Reset()
    {
        NegotiatedProtocol = 0;
        Status = "not connected yet";
        RemoteKeys.Clear();
        ManifestComplete = false;
        ManifestExpected = 0;
        inFlight.Clear();
        refused.Clear();
        SectionsReceived = 0;
        SectionsRequested = 0;
        while (Arrived.TryDequeue(out _)) { }
    }
}
