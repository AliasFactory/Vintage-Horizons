using System.Diagnostics;

namespace VintageHorizons.Checks;

/// <summary>
/// The fast tier of scripts/check.sh. It holds each check that needs no game process.
///
/// The suites run one after the other, in this process. That is not a limitation to remove.
/// LodWorld holds mutable static state, which is DetailDistance, and more than one check sets
/// it. Thus one suite at a time is the only correct order.
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
        // A word with no dash is a filter. A word that starts with a dash is a flag of the
        // host, which came through `dotnet run`. A caller did not mean it for this program.
        //
        // A flag that this program uses as a suite filter runs nothing, and it still exits
        // with 0. That is the worst possible failure for a program that runs checks.
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
                // A suite that throws is a failure. It is not a reason to stop the run. One
                // game type that the runtime cannot load must not hide each check that needs
                // no game type.
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

        // A filter that matched nothing must not appear to be a success. An exit with 0
        // after an empty run is how a check suite stops checking, with no message.
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
