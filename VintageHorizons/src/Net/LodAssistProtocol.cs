using ProtoBuf;

namespace VintageHorizons.Net;

/// <summary>
/// The wire contract for the optional server assist (DESIGN.md section 10). Both sides use
/// this file.
///
/// The assist only adds. Against a server with no assist, a client with the mod must operate
/// exactly as it did before the assist existed. A server with the assist must change nothing
/// for a player who does not have the mod.
///
/// Thus each message here is either a request that can get no answer, or an answer that the
/// receiver can ignore.
/// </summary>
public static class LodAssist
{
    /// <summary>
    /// The name of the channel. It must be identical on both sides, or the two sides do not
    /// connect. The registration is safe against a vanilla server, because the channel never
    /// reaches the Connected state there.
    /// </summary>
    public const string ChannelName = "vintagehorizons";

    /// <summary>
    /// The version of the wire protocol. Increase it when a message changes its meaning.
    /// Do not increase it when a message gains a field, because protobuf ignores a field
    /// that it does not know. Thus a new field is not a break.
    ///
    /// Both sides use <c>min(mine, theirs)</c>. Thus a new server continues to operate with
    /// the clients of version 0.1.1 that users have already.
    /// </summary>
    public const int Protocol = 1;

    /// <summary>
    /// The number of keys in one part of the manifest. 2048 keys is approximately 16 KB.
    /// That size is small enough to not delay a join that loads a world already. It is also
    /// large enough that a world of 5581 keys takes three messages, and not tens of them.
    /// </summary>
    public const int ManifestKeysPerMessage = 2048;

    /// <summary>
    /// The maximum number of sections that one client can have open. At the mean of
    /// 45.9 KB for a section, 16 open sections are approximately 730 KB. That is enough to
    /// keep a join filling in. It is also small enough that a player who runs fast across
    /// unexplored land cannot ask for a full world at one time.
    /// </summary>
    public const int MaxSectionsInFlight = 16;

    /// <summary>
    /// The number of sections that a server gives to one player each second. This limit
    /// exists so that an admin can calculate the cost. At the measured mean, 8 each second
    /// is approximately 370 KB/s for each player.
    ///
    /// The server enforces this limit. It does not trust
    /// <see cref="MaxSectionsInFlight"/>, because a modified client ignores its own limit.
    /// Thus the limit of the client is a courtesy, and this one is the real bound.
    /// </summary>
    public const int MaxSectionsPerSecondPerPlayer = 8;

    /// <summary>
    /// The number of sections that a server gives each second, across all players.
    ///
    /// The limit for each player does not bound the cost to the server. Each section that
    /// the server gives is a SQLite blob read on the main thread. Thus twenty players at 8
    /// each second make 160 reads each second of the tick time.
    ///
    /// This value protects the server. The limit for each player decides only how the
    /// players share it.
    /// </summary>
    public const int MaxSectionsPerSecondTotal = 32;
}

/// <summary>Client to server, one time for each join, and only when the channel is
/// Connected.</summary>
[ProtoContract]
public class AssistHello
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";
}

/// <summary>
/// Server to client, as the answer to <see cref="AssistHello"/>.
///
/// <see cref="Enabled"/> is separate from the protocol test on purpose. When an admin turns
/// the assist off, the client still gets a correct answer that says so. A person can
/// diagnose that state. Silence is worse, because it looks the same as a vanilla server.
/// </summary>
[ProtoContract]
public class AssistWelcome
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";
    [ProtoMember(3)] public bool Enabled;

    /// <summary>A reason for a person to read. `.vhinfo` shows this text without a change.
    /// Nothing parses it.</summary>
    [ProtoMember(4)] public string Status = "";

    /// <summary>
    /// The number of section keys that the server sends next. Thus a client can report the
    /// progress, and it can size its set one time. Without this count, the client makes the
    /// hashes again as each part arrives. The value is zero when no manifest follows.
    /// </summary>
    [ProtoMember(5)] public int ManifestKeyCount;
}

/// <summary>
/// Server to client. This message gives the sections that the server holds, as packed keys
/// and nothing else.
///
/// The measurement is 8 bytes for each key, and 5581 keys for a world that players
/// travelled. That is approximately 44 KB in total. This is cheap enough to send in full at
/// a join. That is the reason why there is no spatial query here.
///
/// The mod divides the manifest into parts. One message of 44 KB is an unnecessary delay on
/// a join that is busy already. The reliable channel has no size limit (section 10.7).
/// </summary>
[ProtoContract]
public class AssistKeyManifest
{
    [ProtoMember(1)] public int Sequence;

    /// <summary>True on the last part. Thus the client knows that the set is
    /// complete.</summary>
    [ProtoMember(2)] public bool Last;

    [ProtoMember(3)] public long[] Keys = Array.Empty<long>();
}

/// <summary>
/// Client to server. This message asks for a group of sections.
///
/// The client asks only for a key that the manifest offered and that it has no local data
/// for. Thus a request is never a duplicate of data on the disk.
///
/// The server does not have to answer all of the keys, or any of them. It decides what it
/// gives. A key with no answer is not an error, and the client asks for it again later.
/// </summary>
[ProtoContract]
public class AssistSectionRequest
{
    [ProtoMember(1)] public long[] Keys = Array.Empty<long>();
}

/// <summary>
/// Server to client. This message gives one section, as the stored blob without a change.
///
/// The mod does not divide this message into parts. On a real world a section measures a
/// mean of 45.9 KB and a maximum of 154.5 KB. The reliable channel has no size limit,
/// because the warning about 508 bytes applies to UDP only (section 10.7). Thus one message
/// for each section is the simpler solution, and it operates.
///
/// If a large message delays a join, put the division into parts here. Use the sequence and
/// last pattern that the manifest uses already. Do not put it anywhere else.
///
/// An empty <see cref="Blob"/> means "the server does not send this one". The section is
/// gone, or the server declined it. The client marks the key, thus it stops asking every few
/// seconds.
/// </summary>
[ProtoContract]
public class AssistSection
{
    [ProtoMember(1)] public long Key;
    [ProtoMember(2)] public byte[] Blob = Array.Empty<byte>();
}
