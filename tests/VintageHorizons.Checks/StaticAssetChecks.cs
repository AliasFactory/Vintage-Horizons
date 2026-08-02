using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// The rules that apply across more than one file, where no compiler and no runtime check
/// can reach. Each check here reads a committed file from the disk, and it touches no game
/// type.
/// </summary>
public static class StaticAssetChecks
{
    public static void Run(Check c)
    {
        AsciiOnly(c);
        TintSlotAgreement(c);
        AlphaPacking(c);
        VersionAgreement(c);
    }

    /// <summary>
    /// The source of a shader must hold ASCII characters only.
    ///
    /// OpenTK gives a managed string to GL with a character count, and the driver reads UTF-8
    /// bytes. Thus one character that is not ASCII cuts the source by (UTF-8 bytes minus
    /// characters) characters. The end of the shader is gone. There is no error, and the
    /// output is wrong.
    ///
    /// This check scans the full asset tree, and not a list of known shaders. Thus it covers
    /// a file that someone adds later, and nobody must remember to add it here.
    /// </summary>
    static void AsciiOnly(Check c)
    {
        string assets = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets");
        c.True(Directory.Exists(assets), "asset directory exists");

        var offenders = new List<string>();
        int scanned = 0;

        foreach (string path in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            // This check skips a binary asset. A PNG holds high bytes by definition.
            if (Path.GetExtension(path) is ".png" or ".jpg" or ".ogg" or ".wav") continue;

            scanned++;
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= 0x80)
                {
                    offenders.Add($"{Path.GetRelativePath(GameAssemblies.RepoRoot, path)} byte {i} = 0x{bytes[i]:X2}");
                    break;
                }
            }
        }

        c.True(scanned > 0, "found asset files to scan");
        c.SeqEq(Array.Empty<string>(), offenders, $"all {scanned} text assets are pure ASCII");
    }

    /// <summary>
    /// Each shader carries its own `const int TINT_SLOTS`, because this version of the game
    /// cannot inject a #define. A difference decodes water as opaque, and thin plants as
    /// water, with no compile error.
    ///
    /// A guard at shader load compared MaxSlots against a second C# constant. A person
    /// maintained that constant as a copy of the shader value. That is two constants in one
    /// file, and it cannot find an edit to a shader at all. The compiler said the same, and
    /// the branch raised CS0162, unreachable code. The copy and the dead guard are both gone.
    ///
    /// A read of the shader files is the only check that closes this hole. It also finds a
    /// disagreement between the .vsh and the .fsh, which nothing did before.
    /// </summary>
    static void TintSlotAgreement(Check c)
    {
        string shaders = Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaders");

        var found = new Dictionary<string, int>();
        foreach (string path in Directory.EnumerateFiles(shaders, "*.*sh"))
        {
            Match m = Regex.Match(File.ReadAllText(path), @"const\s+int\s+TINT_SLOTS\s*=\s*(\d+)\s*;");
            if (m.Success) found[Path.GetFileName(path)] = int.Parse(m.Groups[1].Value);
        }

        c.True(found.ContainsKey("lodterrain.vsh"), "lodterrain.vsh declares TINT_SLOTS");
        c.True(found.ContainsKey("lodterrain.fsh"), "lodterrain.fsh declares TINT_SLOTS");

        foreach ((string file, int value) in found)
        {
            c.Eq(LodTintRegistry.MaxSlots, value, $"{file} TINT_SLOTS matches LodTintRegistry.MaxSlots");
        }

        c.Eq(1, found.Values.Distinct().Count(), "the vertex and fragment shaders agree with each other");
    }

    /// <summary>
    /// LodMesher packs the tint slot into the alpha byte of a vertex, in three bands. Opaque
    /// is at slot. Water is at MaxSlots + slot. Thin is at MaxSlots * 2 + slot.
    ///
    /// Alpha is one byte. Thus the largest value that it holds is MaxSlots * 3 - 1. A
    /// MaxSlots value above 85 moves the thin band into the opaque band, with no error. Then
    /// a thin plant draws as solid terrain, with an arbitrary tint.
    /// </summary>
    static void AlphaPacking(Check c)
    {
        c.True(LodTintRegistry.MaxSlots * 3 <= 256,
            $"tint bands fit in a byte (MaxSlots {LodTintRegistry.MaxSlots} * 3 <= 256)");
    }

    /// <summary>
    /// The script scripts/package.sh takes the name of the release zip from modinfo.json.
    /// The identity of the assembly comes from the csproj. A difference between the two ships
    /// a file whose name disagrees with the version that the game reports.
    /// </summary>
    static void VersionAgreement(Check c)
    {
        CheckPair(c, "VintageHorizons", Path.Combine("VintageHorizons", "VintageHorizons.csproj"),
            Path.Combine("VintageHorizons", "modinfo.json"));
        CheckPair(c, "VintageHorizonsBench",
            Path.Combine("bench", "VintageHorizonsBench", "VintageHorizonsBench.csproj"),
            Path.Combine("bench", "VintageHorizonsBench", "modinfo.json"));
    }

    static void CheckPair(Check c, string label, string csprojRel, string modinfoRel)
    {
        string csproj = Path.Combine(GameAssemblies.RepoRoot, csprojRel);
        string modinfo = Path.Combine(GameAssemblies.RepoRoot, modinfoRel);

        if (!File.Exists(csproj) || !File.Exists(modinfo))
        {
            c.True(false, $"{label}: both csproj and modinfo.json exist");
            return;
        }

        Match fromCsproj = Regex.Match(File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
        Match fromModinfo = Regex.Match(File.ReadAllText(modinfo), @"""version""\s*:\s*""([^""]+)""");

        c.True(fromCsproj.Success, $"{label}: csproj declares a Version");
        c.True(fromModinfo.Success, $"{label}: modinfo.json declares a version");

        if (fromCsproj.Success && fromModinfo.Success)
        {
            c.Eq(fromCsproj.Groups[1].Value, fromModinfo.Groups[1].Value,
                $"{label}: csproj Version matches modinfo.json version");
        }
    }
}
