using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

/// <summary>
/// Adopts a section that arrived from the server. Returns false if it was not taken -
/// the client already had local data for that key, or the blob would not parse.
/// </summary>
public delegate bool LodForeignSectionInstaller(long key, byte[] blob);

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
            .RegisterMessageType<AssistKeyManifest>()
            .RegisterMessageType<AssistSectionRequest>()
            .RegisterMessageType<AssistSection>()
            .SetMessageHandler<AssistWelcome>(OnWelcome)
            .SetMessageHandler<AssistKeyManifest>(OnKeyManifest)
            .SetMessageHandler<AssistSection>(OnSection);
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

    internal void OnWelcome(AssistWelcome msg)
    {
        // Deserialized, so every reference field is whatever the wire produced.
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

    /// <summary>
    /// Keys the server holds. Read and mutated only from the game tick, via <see cref="Pump"/>
    /// - the handlers below run on whatever thread the engine delivers packets on, and a
    /// plain HashSet shared across those two would be a race whether or not it shows up in
    /// testing. Everything a handler learns goes into a concurrent queue and is applied on
    /// the tick, which also puts installs in the one place allowed to touch LodWorld.
    /// </summary>
    public readonly HashSet<long> RemoteKeys = new();

    readonly ConcurrentQueue<(long[] Keys, bool Last)> manifestChunks = new();

    /// <summary>True once the final manifest chunk has been applied.</summary>
    public bool ManifestComplete { get; private set; }

    /// <summary>What the server said to expect, for comparison against what arrived.</summary>
    public int ManifestExpected { get; private set; }

    internal void OnKeyManifest(AssistKeyManifest msg) =>
        manifestChunks.Enqueue((msg.Keys ?? Array.Empty<long>(), msg.Last));

    // ---- Section transfer ----

    /// <summary>Arrivals awaiting the tick. Empty blob = the server declined the key.</summary>
    readonly ConcurrentQueue<(long Key, byte[] Blob)> Arrived = new();

    readonly HashSet<long> inFlight = new();

    /// <summary>Keys the server declined or no longer has; never asked for again.</summary>
    readonly HashSet<long> refused = new();

    public int InFlight => inFlight.Count;
    public int SectionsReceived { get; private set; }
    public int SectionsRefused => refused.Count;

    /// <summary>
    /// Ask for sections the server has and we do not, up to the in-flight cap. Called from
    /// the game tick with keys the quadtree actually wants, so the fetch order follows what
    /// the player can see rather than the manifest's arbitrary order.
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
    /// Apply everything the handlers have queued, on the game tick. <paramref name="install"/>
    /// is given each arrived section and returns whether it was adopted; a section the
    /// client already has locally is declined there, since local capture wins (§10.5).
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
            // Announced vs applied: a mismatch means keys were captured or evicted
            // mid-send, expected on a live server and worth seeing rather than silently
            // tolerating once transfer starts trusting this set.
            logger.Notification(
                "VintageHorizons: server key manifest complete - {0} keys received{1}",
                RemoteKeys.Count,
                ManifestExpected > 0 && ManifestExpected != RemoteKeys.Count
                    ? $" (server announced {ManifestExpected})" : "");
        }

        while (Arrived.TryDequeue(out (long Key, byte[] Blob) got))
        {
            inFlight.Remove(got.Key);

            // install is called even for an empty blob, so the one place that knows a key
            // is unavailable can also release the render path's wait on it. Short-circuiting
            // here instead left declined keys stuck in LodWorld.LoadsInFlight for the
            // session, which pinned their parent coarse.
            if (install(got.Key, got.Blob))
            {
                SectionsReceived++;
                continue;
            }

            // Declined, gone, or already held locally. Remembering that is what stops us
            // asking every tick forever for something that will never arrive.
            refused.Add(got.Key);
            RemoteKeys.Remove(got.Key);
        }
    }

    /// <summary>Reset for the next world; the channel itself outlives the join.</summary>
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
