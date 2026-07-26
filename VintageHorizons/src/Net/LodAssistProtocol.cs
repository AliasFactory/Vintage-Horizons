using ProtoBuf;

namespace VintageHorizons.Net;

/// <summary>
/// Wire contract for the optional server assist (DESIGN.md §10), shared by both sides.
///
/// The assist is strictly additive: a client with the mod must behave exactly as it did
/// before this existed when the server has no assist, and a server with the assist must
/// not change anything for players who do not have the mod. Every message here is
/// therefore either a request that may go unanswered or an answer that may be ignored.
/// </summary>
public static class LodAssist
{
    /// <summary>
    /// Channel name, identical on both sides or they do not link up. Registering it is
    /// safe against a vanilla server: the channel simply never reaches Connected.
    /// </summary>
    public const string ChannelName = "vintagehorizons";

    /// <summary>
    /// Wire protocol version, bumped whenever a message changes meaning rather than
    /// gaining a field -- protobuf already ignores fields it does not know, so adding
    /// one is not a break. Both sides run <c>min(mine, theirs)</c> so a new server keeps
    /// working with the 0.1.1-era clients that are already in the wild.
    /// </summary>
    public const int Protocol = 1;
}

/// <summary>Client -> server, once per join, only when the channel is Connected.</summary>
[ProtoContract]
public class AssistHello
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";
}

/// <summary>
/// Server -> client, in reply to <see cref="AssistHello"/>. <see cref="Enabled"/> is
/// separate from the protocol check on purpose: an admin who has turned the assist off
/// still gets a well-formed answer saying so, which is a diagnosable state, rather than
/// silence that looks identical to a vanilla server.
/// </summary>
[ProtoContract]
public class AssistWelcome
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";
    [ProtoMember(3)] public bool Enabled;

    /// <summary>Human-readable reason, surfaced verbatim by .vhinfo. Not parsed.</summary>
    [ProtoMember(4)] public string Status = "";
}
