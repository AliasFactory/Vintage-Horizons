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

    /// <summary>
    /// Keys per manifest chunk. 2048 keys is ~16 KB, small enough not to stall a join
    /// that is already loading a world and large enough that a 5581-key world takes
    /// three messages rather than dozens.
    /// </summary>
    public const int ManifestKeysPerMessage = 2048;

    /// <summary>
    /// Most sections a client may have outstanding. At a mean 45.9 KB a section, 16 in
    /// flight is roughly 730 KB - enough to keep a join filling in, small enough that a
    /// player who sprints across unexplored land cannot ask for a whole world at once.
    /// </summary>
    public const int MaxSectionsInFlight = 16;

    /// <summary>
    /// Sections a server will serve one player per second. The cap exists so an admin can
    /// reason about the cost: 8/s is ~370 KB/s per player at the measured mean.
    ///
    /// Enforced server-side rather than trusted to <see cref="MaxSectionsInFlight"/>: a
    /// modified client ignores its own limit, so the client's is a courtesy and this is
    /// the actual bound.
    /// </summary>
    public const int MaxSectionsPerSecondPerPlayer = 8;

    /// <summary>
    /// Sections a server will serve per second in total, across every player. The
    /// per-player cap alone does not bound what the server pays: each section served is a
    /// main-thread SQLite blob read, so twenty players at 8/s each would be 160 reads a
    /// second of tick time. This is the number that protects the server, and the
    /// per-player cap only decides how it is shared.
    /// </summary>
    public const int MaxSectionsPerSecondTotal = 32;
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

    /// <summary>
    /// How many section keys the server is about to send, so a client can report
    /// progress and size its set once instead of rehashing as chunks arrive. Zero when
    /// no manifest follows.
    /// </summary>
    [ProtoMember(5)] public int ManifestKeyCount;
}

/// <summary>
/// Server -> client: which sections the server holds, as packed keys and nothing else.
/// Measured at 8 bytes a key and 5581 keys for a well-travelled world, so ~44 KB total -
/// cheap enough to send in full at join, which is why there is no spatial query here.
///
/// Chunked because one 44 KB message is a needless latency spike on a join that is
/// already busy, not because the reliable channel has a size limit (§10.7).
/// </summary>
[ProtoContract]
public class AssistKeyManifest
{
    [ProtoMember(1)] public int Sequence;

    /// <summary>Set on the final chunk, so the client knows the set is complete.</summary>
    [ProtoMember(2)] public bool Last;

    [ProtoMember(3)] public long[] Keys = Array.Empty<long>();
}

/// <summary>
/// Client -> server: send me these sections. Batched, and the client asks only for keys
/// the manifest offered that it has no local data for, so a request is never a duplicate
/// of something on disk.
///
/// The server is not obliged to answer all of them, or any: it decides what it is willing
/// to send, and an unanswered key is retried later rather than treated as an error.
/// </summary>
[ProtoContract]
public class AssistSectionRequest
{
    [ProtoMember(1)] public long[] Keys = Array.Empty<long>();
}

/// <summary>
/// Server -> client: one section, as the stored blob verbatim.
///
/// Not chunked. Sections measure a mean 45.9 KB and a max 154.5 KB on a real world, and
/// the reliable channel has no size cap (the 508-byte warning is UDP-only, §10.7), so one
/// message per section is the simpler thing that works. If large messages turn out to
/// stall a join, chunking goes here - with the sequence/last pattern the manifest already
/// uses - rather than anywhere else.
///
/// <see cref="Blob"/> empty means "I am not sending this one": either it is gone or the
/// server declined. The client marks the key so it stops asking every few seconds.
/// </summary>
[ProtoContract]
public class AssistSection
{
    [ProtoMember(1)] public long Key;
    [ProtoMember(2)] public byte[] Blob = Array.Empty<byte>();
}
