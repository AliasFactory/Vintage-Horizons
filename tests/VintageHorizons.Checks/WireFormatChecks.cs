using System.Reflection;
using ProtoBuf;
using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The packet layout on the wire, pinned.
///
/// One mod covers all four install combinations, so a 0.2.0 client can meet a 0.1.1
/// server and the reverse. Protobuf identifies a field by its number, not its name, so
/// renumbering a member or changing its type silently reinterprets an old peer's bytes.
/// The compiler cannot see it, the fast tier would not notice, and the symptom reaches a
/// player as garbled terrain or a handshake that fails for no stated reason.
///
/// So the numbers are asserted here rather than trusted. Adding a field with a NEW number
/// is safe and needs only a line below. Changing an existing number or type is a protocol
/// break, and this suite is where that decision gets made deliberately.
/// </summary>
public static class WireFormatChecks
{
    public static void Run(Check c)
    {
        FieldNumbersAreStable(c);
        PacketsRoundTrip(c);
    }

    /// <summary>Every [ProtoMember] on a type, as "number:name:type".</summary>
    static List<string> Layout(Type type)
    {
        var found = new List<string>();
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var tag = f.GetCustomAttribute<ProtoMemberAttribute>();
            if (tag != null) found.Add($"{tag.Tag}:{f.Name}:{f.FieldType.Name}");
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static void FieldNumbersAreStable(Check c)
    {
        c.SeqEq(new[] { "1:Protocol:Int32", "2:ModVersion:String" },
            Layout(typeof(AssistHello)), "AssistHello field numbers");

        c.SeqEq(new[]
        {
            "1:Protocol:Int32", "2:ModVersion:String", "3:Enabled:Boolean",
            "4:Status:String", "5:ManifestKeyCount:Int32",
        }, Layout(typeof(AssistWelcome)), "AssistWelcome field numbers");

        c.SeqEq(new[] { "1:Sequence:Int32", "2:Last:Boolean", "3:Keys:Int64[]" },
            Layout(typeof(AssistKeyManifest)), "AssistKeyManifest field numbers");

        c.SeqEq(new[] { "1:Keys:Int64[]" },
            Layout(typeof(AssistSectionRequest)), "AssistSectionRequest field numbers");

        c.SeqEq(new[] { "1:Key:Int64", "2:Blob:Byte[]" },
            Layout(typeof(AssistSection)), "AssistSection field numbers");

        // The negotiated protocol number itself. A client takes Math.Min of its own and
        // the server's, so bumping this is a deliberate compatibility decision.
        c.Eq(1, LodAssist.Protocol, "the protocol version is 1");
    }

    /// <summary>
    /// Each packet survives a serialize and deserialize. The field numbers above say the
    /// contract has not moved. This says protobuf can actually carry it - including the
    /// empty blob that means "refused", which must not come back as null.
    /// </summary>
    static void PacketsRoundTrip(Check c)
    {
        var hello = Roundtrip(new AssistHello { Protocol = 1, ModVersion = "0.2.0" });
        c.Eq(1, hello.Protocol, "hello protocol survives");
        c.Eq("0.2.0", hello.ModVersion, "hello version survives");

        var welcome = Roundtrip(new AssistWelcome
        {
            Protocol = 1, ModVersion = "0.2.0", Enabled = true,
            Status = "serving from 5 cached sections", ManifestKeyCount = 5,
        });
        c.Eq(true, welcome.Enabled, "welcome enabled flag survives");
        c.Eq(5, welcome.ManifestKeyCount, "welcome manifest count survives");
        c.Eq("serving from 5 cached sections", welcome.Status, "welcome status survives");

        var keys = new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue };
        var manifest = Roundtrip(new AssistKeyManifest { Sequence = 2, Last = true, Keys = keys });
        c.SeqEq(keys, manifest.Keys, "manifest keys survive, including the extremes");
        c.Eq(true, manifest.Last, "the manifest last flag survives");

        var request = Roundtrip(new AssistSectionRequest { Keys = keys });
        c.SeqEq(keys, request.Keys, "request keys survive");

        var section = Roundtrip(new AssistSection { Key = 42, Blob = new byte[] { 9, 8, 7 } });
        c.Eq(42L, section.Key, "section key survives");
        c.SeqEq(new byte[] { 9, 8, 7 }, section.Blob, "section blob survives");

        // The refusal. An empty blob is the server saying no, and the client keys its
        // whole in-flight release on receiving one - so it must arrive as an empty array
        // or a null, never as something the handler mistakes for data.
        var refusal = Roundtrip(new AssistSection { Key = 42 });
        c.Eq(42L, refusal.Key, "a refusal carries its key");
        c.Eq(0, refusal.Blob?.Length ?? 0, "a refusal carries no blob bytes");
    }

    static T Roundtrip<T>(T value)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, value);
        buffer.Position = 0;
        return Serializer.Deserialize<T>(buffer);
    }
}
