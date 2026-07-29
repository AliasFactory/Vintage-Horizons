using System.Diagnostics;

namespace VintageHorizons.Checks;

/// <summary>
/// The fast tier of scripts/check.sh: everything that can be proven without a game process.
/// Runs sequentially and in-process, which is not a limitation to work around - LodWorld
/// carries mutable static state (DetailDistance) that several checks set, so sequential is
/// the only correct order anyway.
/// </summary>
public static class Program
{
    static readonly (string Category, string Name, Action<Check> Run)[] Suites =
    {
        ("probe", "assembly loading", ProbeChecks.Run),
        ("pure", "key math", KeyMathChecks.Run),
        ("pure", "section runs", SectionChecks.Run),
        ("pure", "mip downsample", MipChecks.Run),
        ("pure", "mesher", MesherChecks.Run),
        ("pure", "server config", ConfigChecks.Run),
        ("fixture", "blob format", StoreChecks.Run),
        ("fixture", "frustum", FrustumChecks.Run),
        ("fixture", "block policy", PolicyChecks.Run),
        ("pure", "remote keys", RemoteKeyChecks.Run),
        ("fixture", "server assist", ServerAssistChecks.Run),
        ("static", "assets and constants", StaticAssetChecks.Run),
    };

    public static int Main(string[] args)
    {
        // Only a bare word filters; anything dash-prefixed is a host flag that leaked
        // through `dotnet run` rather than something a caller meant for us. Treating one
        // as a suite filter silently runs nothing and still exits zero, which is the
        // worst possible failure mode for a check runner.
        string? only = args.FirstOrDefault(a => !a.StartsWith('-'));

        Console.WriteLine();
        Console.WriteLine("  VintageHorizons fast checks");
        Console.WriteLine("  game: " + GameAssemblies.GamePath);
        Console.WriteLine();

        var total = new Stopwatch();
        total.Start();

        int assertions = 0, failed = 0, ran = 0;

        foreach ((string category, string name, Action<Check> run) in Suites)
        {
            if (only != null && !category.Contains(only, StringComparison.OrdinalIgnoreCase)
                             && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;

            ran++;
            var check = new Check();
            string? crash = null;

            var watch = Stopwatch.StartNew();
            try
            {
                run(check);
            }
            catch (Exception e)
            {
                // A suite that throws is itself a failure, not a reason to abandon the run:
                // one unloadable game type must not hide every check that needs no game type.
                crash = e.ToString();
            }
            watch.Stop();

            assertions += check.Passed + check.Failures.Count;
            failed += check.Failures.Count + (crash != null ? 1 : 0);

            string label = ("  " + category).PadRight(15) + name + " ";
            string dots = new string('.', Math.Max(3, 44 - label.Length));
            string result = crash != null
                ? "CRASHED"
                : check.Failures.Count == 0
                    ? $"{check.Passed} ok"
                    : $"{check.Failures.Count} FAILED of {check.Passed + check.Failures.Count}";

            Console.WriteLine($"{label}{dots} {result}  ({watch.ElapsedMilliseconds}ms)");

            foreach (string failure in check.Failures) Console.WriteLine("      x " + failure);
            if (crash != null) Console.WriteLine("      x suite threw:\n        " + crash.Replace("\n", "\n        "));
        }

        total.Stop();

        Console.WriteLine();

        // A filter that matched nothing must not look like success. Exiting zero on an
        // empty run is how a check suite quietly stops checking.
        if (ran == 0)
        {
            Console.WriteLine($"  no suite matched '{only}' - nothing ran");
            Console.WriteLine();
            return 2;
        }

        Console.WriteLine($"  {assertions} assertions, {failed} failures, {total.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine();

        return failed == 0 ? 0 : 1;
    }
}
