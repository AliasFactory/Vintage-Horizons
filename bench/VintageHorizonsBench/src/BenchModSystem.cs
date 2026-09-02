using System.Globalization;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageHorizonsBench;

/// <summary>
/// Drives a fixed route and records what each LOD mod does with it.
///
/// The point is like-for-like comparison: the same world, the same waypoints, the same
/// camera angles, the same time of day and weather, with only the mod under test
/// changed. Anything that varies run to run makes the numbers and the screenshots
/// incomparable, so this harness pins all of it.
///
/// It deliberately does NOT judge image quality. It produces frame-time statistics for
/// the numbers, and one screenshot per waypoint per mod for a human to compare
/// side by side.
///
/// Enabled only when VHBENCH_ROUTE is set:
///   VHBENCH_ROUTE   path to a route file (see routes/*.txt)
///   VHBENCH_LABEL   name of the configuration under test, used in output filenames
///   VHBENCH_OUT     output directory (default: &lt;dataPath&gt;/bench)
///   VHBENCH_SETTLE  MINIMUM seconds to wait at each waypoint before measuring (default 20)
///   VHBENCH_SETTLE_MAX  give up waiting for stability after this many seconds (default 90)
///   VHBENCH_MEASURE seconds to measure at each waypoint (default 10)
///   VHBENCH_LAPS    MEASURED laps of the route, aggregated per waypoint (default 1)
///   VHBENCH_WARMUP_LAPS  laps walked first and thrown away (default 1)
///   VHBENCH_TIME     time-of-day pin, 24h "HH:MM" or a word like "sunset"
///                    (default: 12:00, as always)
///   VHBENCH_MONTH    calendar month pin, jan..dec (default: leave the date alone)
///   VHBENCH_FREEZE_TIME  "1" stops the calendar after pinning, so a long settle
///                    cannot drift the hour. Off by default.
///
/// A colour comparison needs the same hour and the same season in every run, so a
/// label can pin them. Month goes first: setmonth fast-forwards the calendar and
/// drags the time of day with it, so the hour is set after the month.
///
/// On settling, and why it is not a fixed sleep. A teleport starts a burst of streaming,
/// capture, meshing and upload, and how long that burst lasts depends on the mod, the
/// waypoint and what is already cached. A fixed timer either cuts into the burst or
/// wastes time after it, and the first of those is worse: it measures loading, not
/// drawing, and it does so by an amount that varies run to run.
///
/// So settling watches the frame times themselves and waits for them to stop moving.
/// That criterion needs to know nothing about the mod under test, which matters because
/// this harness also runs against Farseer and against no LOD mod at all.
///
/// On laps. One visit to a waypoint is one sample, and a 1% low drawn from ~1500 frames
/// is the mean of the worst 15, which moves a lot. Walking the route several times and
/// taking the median across laps costs wall clock and buys the ability to tell a real
/// change from noise. The spread across laps is written into the CSV as well, so the
/// noise floor is visible in every result rather than needing a separate experiment.
///
/// On the warm-up lap, and why laps are not simply averaged. The first lap loads the
/// world: it captures, meshes and uploads terrain that every later lap then finds
/// already there. Measured, the first lap ran several times slower than the second at
/// every waypoint, so averaging across laps blends two different regimes and the spread
/// that comes out measures the warm-up rather than the noise. The first lap or two are
/// therefore walked and discarded, and only the steady-state laps are aggregated.
/// </summary>
public class BenchModSystem : ModSystem, IRenderer
{
    public double RenderOrder => 1.0; // after everything else drawing this frame
    public int RenderRange => 9999;

    ICoreClientAPI capi = null!;
    BenchRoute? route;
    string label = "unlabelled";
    string outDir = "";
    double settleSec = 20;
    double settleMaxSec = 90;
    double measureSec = 10;
    int laps = 1;
    int warmupLaps = 1;
    string timePin = "";
    string monthPin = "";
    bool timeFrozen;
    int TotalLaps => warmupLaps + laps;
    bool Measuring => lap >= warmupLaps;

    enum Phase { WaitingForJoin, Settling, Measuring, Done }

    Phase phase = Phase.WaitingForJoin;
    int waypointIndex = -1;
    int lap;
    double phaseStartedAt;
    double nowSec;

    readonly List<double> frameMs = new(4096);
    readonly List<string> csvRows = new();

    /// <summary>One visit to one waypoint.</summary>
    readonly record struct Sample(int Frames, double Mean, double Median, double WorstMean,
        long ManagedMb, long RssMb, double SettledAfter, bool Stabilised);

    /// <summary>Samples per waypoint, one per lap.</summary>
    List<Sample>[] samples = Array.Empty<List<Sample>>();

    // Stability detection during settling.
    readonly List<double> settleWindow = new(1024);
    readonly List<double> windowMedians = new(8);
    double windowStartedAt;

    /// <summary>
    /// How long one stability window is, and how many must agree before settling ends.
    ///
    /// Judged across the whole span rather than pair by pair, and that distinction is not
    /// academic: comparing each window only with the one before it accepts a steady climb
    /// forever, because every single step is inside the tolerance. Measured, that is the
    /// shape this actually has. A waypoint reported settled and then ran several times
    /// faster on a later lap, which is a slow drift that pairwise comparison cannot see.
    /// </summary>
    const double StabilityWindowSec = 3.0;

    /// <summary>
    /// Settling ends when the recent half of these windows agrees with the older half,
    /// to within the tolerance, in EITHER direction.
    ///
    /// Not "are the frame times quiet". A waypoint with genuine stutter can never be
    /// quiet: ridge-east runs at 13-19ms with 75ms worst frames and timed out at every
    /// attempt while its neighbours settled in 12s. Comparing halves is noise-tolerant
    /// because each half is a median of three windows, and the thing being detected is a
    /// TREND, which noise is not.
    ///
    /// Two-sided, not "has it stopped getting faster". A one-sided test fires while the
    /// frame time is climbing, which is exactly what happens as terrain loads in.
    ///
    /// The baseline has to be long. Two adjacent windows differ by very little during a
    /// slow drift, so a short comparison accepts one indefinitely; six windows is 18s,
    /// over which a real warm-up moves far more than the tolerance.
    ///
    /// Checked against traces with known answers rather than guessed. It settles a flat
    /// trace and a noisy flat one at 18s, waits out a 5x warm-up until 54s and a 2x one
    /// until 42s, waits past the peak of a load-in hump, and refuses to settle at all on
    /// a sustained 2%/s drift.
    /// </summary>
    const int StabilityWindows = 6;
    const double StabilityTolerance = 0.05;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        string? routePath = Environment.GetEnvironmentVariable("VHBENCH_ROUTE");
        if (string.IsNullOrEmpty(routePath))
        {
            Mod.Logger.Notification("Bench idle (set VHBENCH_ROUTE to run).");
            return;
        }

        label = Environment.GetEnvironmentVariable("VHBENCH_LABEL") ?? "unlabelled";
        outDir = Environment.GetEnvironmentVariable("VHBENCH_OUT") ?? Path.Combine(GamePaths.DataPath, "bench");
        settleSec = ReadDouble("VHBENCH_SETTLE", 20);
        measureSec = ReadDouble("VHBENCH_MEASURE", 10);
        laps = Math.Max(1, (int)ReadDouble("VHBENCH_LAPS", 1));

        // Both of these default to the OLD behaviour, and that is deliberate. A caller
        // that does not ask for adaptive settling gets a fixed settle of exactly
        // VHBENCH_SETTLE, and a caller that does not ask for warm-up laps walks the route
        // once. check-matrix.sh drives this harness through the environment directly, and
        // when these defaulted to the new behaviour it silently started walking two laps
        // and reporting every waypoint as timed out, because a settle whose floor equals
        // its ceiling can never end early.
        settleMaxSec = Math.Max(settleSec, ReadDouble("VHBENCH_SETTLE_MAX", settleSec));
        warmupLaps = Math.Max(0, (int)ReadDouble("VHBENCH_WARMUP_LAPS", 0));

        // Optional colour pins. Empty keeps the old behaviour exactly.
        timePin = Environment.GetEnvironmentVariable("VHBENCH_TIME") ?? "";
        monthPin = Environment.GetEnvironmentVariable("VHBENCH_MONTH") ?? "";
        timeFrozen = (Environment.GetEnvironmentVariable("VHBENCH_FREEZE_TIME") ?? "") == "1";

        try
        {
            route = BenchRoute.Load(routePath);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Bench route {0} could not be read: {1}", routePath, e);
            return;
        }

        samples = new List<Sample>[route.Waypoints.Count];
        for (int i = 0; i < samples.Length; i++) samples[i] = new List<Sample>();

        Directory.CreateDirectory(outDir);
        Mod.Logger.Notification(
            "Bench armed: label '{0}', {1} waypoints x ({2} warm-up + {3} measured) laps, "
            + "settle {4}-{5}s, measure {6}s, out {7}",
            label, route.Waypoints.Count, warmupLaps, laps, settleSec, settleMaxSec,
            measureSec, outDir);

        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Done, "vintagehorizonsbench");
    }

    static double ReadDouble(string envName, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        return double.TryParse(raw, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    void OnLevelFinalize()
    {
        // Fix everything that would otherwise differ between runs. Creative first so
        // the teleports are permitted at all.
        capi.SendChatMessage("/gamemode creative");
        if (monthPin.Length > 0)
            capi.Event.RegisterCallback(_ => capi.SendChatMessage($"/time setmonth {monthPin}"), 1500);
        // The hour is pinned with or without VHBENCH_TIME: 12:00 stays the default.
        // setmonth shifts the clock, so this callback runs after it.
        string hour = timePin.Length > 0 ? timePin : "12:00";
        capi.Event.RegisterCallback(_ => capi.SendChatMessage($"/time set {hour}"), 2000);
        capi.Event.RegisterCallback(_ => capi.SendChatMessage("/weather setprecip 0"), 2500);
        if (timeFrozen)
            capi.Event.RegisterCallback(_ => capi.SendChatMessage("/time stop"), 2800);
        capi.Event.RegisterCallback(_ => AdvanceToNextWaypoint(), 4000);
    }

    void AdvanceToNextWaypoint()
    {
        if (route == null) return;

        waypointIndex++;
        if (waypointIndex >= route.Waypoints.Count)
        {
            if (++lap >= TotalLaps)
            {
                Finish();
                return;
            }
            waypointIndex = 0;
            Mod.Logger.Notification("Bench lap {0}/{1}{2}", lap + 1, TotalLaps,
                Measuring ? "" : " (warm-up, discarded)");
        }

        BenchWaypoint wp = route.Waypoints[waypointIndex];
        capi.SendChatMessage($"/tp ={wp.X.ToString("0.##", CultureInfo.InvariantCulture)} " +
                             $"{wp.Y.ToString("0.##", CultureInfo.InvariantCulture)} " +
                             $"={wp.Z.ToString("0.##", CultureInfo.InvariantCulture)}");

        phase = Phase.Settling;
        phaseStartedAt = nowSec;
        settleWindow.Clear();
        windowMedians.Clear();
        windowStartedAt = nowSec;

        Mod.Logger.Notification("Bench lap {0}/{1} waypoint {2}/{3} '{4}': settling, {5}-{6}s",
            lap + 1, TotalLaps, waypointIndex + 1, route.Waypoints.Count, wp.Name, settleSec, settleMaxSec);
    }

    /// <summary>
    /// True once the frame times have stopped moving, or once patience runs out.
    /// Reports which of those it was, because a waypoint that never stabilised is a
    /// result about the harness and must not pass silently as one about the mod.
    /// </summary>
    bool Settled(double elapsed, double deltaMs, out bool stabilised)
    {
        stabilised = false;
        settleWindow.Add(deltaMs);

        if (nowSec - windowStartedAt >= StabilityWindowSec && settleWindow.Count > 8)
        {
            settleWindow.Sort();
            windowMedians.Add(settleWindow[settleWindow.Count / 2]);
            if (windowMedians.Count > StabilityWindows) windowMedians.RemoveAt(0);

            settleWindow.Clear();
            windowStartedAt = nowSec;

            if (elapsed >= settleSec && HalvesAgree())
            {
                stabilised = true;
                return true;
            }
        }

        return elapsed >= settleMaxSec;
    }

    /// <summary>
    /// The recent half of the settle window looks like the older half. Medians of halves
    /// rather than individual windows, so one stuttery window cannot decide it either way.
    /// </summary>
    bool HalvesAgree()
    {
        if (windowMedians.Count < StabilityWindows) return false;

        int half = StabilityWindows / 2;
        double older = MedianOf(windowMedians, 0, half);
        double recent = MedianOf(windowMedians, half, StabilityWindows);

        return older > 0 && Math.Abs(older - recent) / older < StabilityTolerance;
    }

    static double MedianOf(List<double> values, int from, int to)
    {
        var slice = values.GetRange(from, to - from);
        slice.Sort();
        return slice[slice.Count / 2];
    }

    /// <summary>
    /// Dismiss any open dialog. An unattended run has no window focus, so the client
    /// puts up its "Game is still running" menu and sits there - the first benchmark
    /// measured frame times with that overlay covering the view, which is not the
    /// gameplay it was supposed to measure.
    /// </summary>
    void CloseBlockingDialogs()
    {
        List<GuiDialog> open = capi.Gui.OpenedGuis;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            GuiDialog dlg = open[i];
            if (dlg.DialogType == EnumDialogType.HUD) continue; // hotbar, health: harmless
            dlg.TryClose();
        }
    }

    /// <summary>Camera is re-pinned every frame: mouse input and physics both fight it.</summary>
    void PinCamera(BenchWaypoint wp)
    {
        IClientPlayer player = capi.World.Player;
        player.CameraYaw = wp.Yaw;
        player.CameraPitch = wp.Pitch;
        capi.Input.MouseYaw = wp.Yaw;
        player.Entity.Pos.Yaw = wp.Yaw;
        player.Entity.Pos.Pitch = wp.Pitch;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (route == null || phase == Phase.Done) return;

        nowSec += deltaTime;
        if (phase == Phase.WaitingForJoin) return;

        CloseBlockingDialogs();

        BenchWaypoint wp = route.Waypoints[waypointIndex];
        PinCamera(wp);

        double elapsed = nowSec - phaseStartedAt;

        if (phase == Phase.Settling)
        {
            // Wait for the mod under test to finish streaming, building and uploading its
            // terrain, judged by the frame times going quiet rather than by a clock.
            if (Settled(elapsed, deltaTime * 1000.0, out bool stabilised))
            {
                settledAfter = elapsed;

                // A fixed settle is not a failed adaptive one. When the floor and the
                // ceiling are the same the caller asked for a plain wait, so reaching the
                // ceiling is the expected outcome and warning about it is noise.
                bool adaptive = settleMaxSec > settleSec;
                settledCleanly = stabilised || !adaptive;

                if (adaptive && !stabilised)
                {
                    Mod.Logger.Warning(
                        "Bench '{0}' at '{1}': frame times still had a trend after {2}s; "
                        + "this sample includes load, treat it with suspicion.",
                        label, wp.Name, settleMaxSec);
                }

                frameMs.Clear();
                phase = Phase.Measuring;
                phaseStartedAt = nowSec;
            }
            return;
        }

        frameMs.Add(deltaTime * 1000.0);

        if (elapsed >= measureSec)
        {
            RecordWaypoint(wp);
            // One screenshot per waypoint, from the first MEASURED lap: a warm-up lap can
            // still be filling terrain in, and a half-loaded picture is misleading in
            // exactly the way a screenshot comparison is meant to catch.
            if (lap == warmupLaps) CaptureScreenshot(wp);
            AdvanceToNextWaypoint();
        }
    }

    double settledAfter;
    bool settledCleanly;

    void RecordWaypoint(BenchWaypoint wp)
    {
        if (frameMs.Count == 0) return;

        var sorted = new List<double>(frameMs);
        sorted.Sort();

        double total = 0;
        foreach (double ms in frameMs) total += ms;
        double mean = total / frameMs.Count;

        // "1% low FPS" the way benchmarks usually mean it: the mean of the worst 1% of
        // frames, which is what stutter actually feels like.
        int worstCount = Math.Max(1, sorted.Count / 100);
        double worstTotal = 0;
        for (int i = sorted.Count - worstCount; i < sorted.Count; i++) worstTotal += sorted[i];
        double worstMean = worstTotal / worstCount;

        double median = sorted[sorted.Count / 2];
        long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
        long rssMb = Environment.WorkingSet / (1024 * 1024);

        // Warm-up laps are walked for their side effects on the world, not for their
        // numbers: the first visit pays for capture, meshing and upload that every later
        // one finds already done, and mixing the two makes the spread meaningless.
        if (Measuring)
        {
            samples[waypointIndex].Add(new Sample(frameMs.Count, mean, median, worstMean,
                managedMb, rssMb, settledAfter, settledCleanly));
        }

        Mod.Logger.Notification(
            "Bench '{0}' lap {1}{2} at '{3}': {4:0.0} fps avg, {5:0.0} fps 1% low, {6} frames, "
            + "settled after {7:0.0}s{8}",
            label, lap + 1, Measuring ? "" : " (warm-up)", wp.Name, 1000.0 / mean,
            1000.0 / worstMean, frameMs.Count, settledAfter, settledCleanly ? "" : " (TIMED OUT)");
    }

    static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// How far apart the laps landed, as a percentage of the median. This is the number
    /// that says whether any difference between two runs means anything, so it belongs
    /// in the results rather than in a separate experiment somebody has to remember to do.
    /// </summary>
    static double SpreadPct(List<double> values)
    {
        if (values.Count < 2) return 0;
        double med = Median(values);
        if (med <= 0) return 0;
        double min = values[0], max = values[0];
        foreach (double v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (max - min) / med * 100.0;
    }

    void CaptureScreenshot(BenchWaypoint wp)
    {
        try
        {
            // Full framebuffer resolution: these are for a human to compare, and
            // downscaling would hide exactly the detail differences under test.
            using BitmapRef bmp = capi.Render.GrabScreenshot(
                capi.Render.FrameWidth, capi.Render.FrameHeight, false, true);
            bmp.Save(Path.Combine(outDir, $"{Sanitize(label)}--{Sanitize(wp.Name)}.png"));
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Bench screenshot at '{0}' failed: {1}", wp.Name, e.Message);
        }
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    void Finish()
    {
        phase = Phase.Done;

        if (route != null)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                List<Sample> laps = samples[i];
                if (laps.Count == 0) continue;

                BenchWaypoint wp = route.Waypoints[i];
                var means = laps.ConvertAll(s => s.Mean);
                var worsts = laps.ConvertAll(s => s.WorstMean);
                var medians = laps.ConvertAll(s => s.Median);

                double mean = Median(means);
                double worstMean = Median(worsts);
                double median = Median(medians);
                int timedOut = laps.FindAll(s => !s.Stabilised).Count;

                csvRows.Add(string.Join(",", new[]
                {
                    Csv(label), Csv(wp.Name),
                    wp.X.ToString("0.##", CultureInfo.InvariantCulture),
                    wp.Y.ToString("0.##", CultureInfo.InvariantCulture),
                    wp.Z.ToString("0.##", CultureInfo.InvariantCulture),
                    laps.Count.ToString(CultureInfo.InvariantCulture),
                    laps[^1].Frames.ToString(CultureInfo.InvariantCulture),
                    (1000.0 / mean).ToString("0.0", CultureInfo.InvariantCulture),
                    (1000.0 / median).ToString("0.0", CultureInfo.InvariantCulture),
                    (1000.0 / worstMean).ToString("0.0", CultureInfo.InvariantCulture),
                    mean.ToString("0.00", CultureInfo.InvariantCulture),
                    worstMean.ToString("0.00", CultureInfo.InvariantCulture),
                    SpreadPct(means).ToString("0.0", CultureInfo.InvariantCulture),
                    SpreadPct(worsts).ToString("0.0", CultureInfo.InvariantCulture),
                    Median(laps.ConvertAll(s => (double)s.ManagedMb)).ToString("0", CultureInfo.InvariantCulture),
                    Median(laps.ConvertAll(s => (double)s.RssMb)).ToString("0", CultureInfo.InvariantCulture),
                    Median(laps.ConvertAll(s => s.SettledAfter)).ToString("0.0", CultureInfo.InvariantCulture),
                    timedOut.ToString(CultureInfo.InvariantCulture),
                }));
            }
        }

        string csvPath = Path.Combine(outDir, $"{Sanitize(label)}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("label,waypoint,x,y,z,laps,frames,fps_avg,fps_median,fps_1pct_low,"
            + "frame_ms_avg,frame_ms_1pct_low,spread_pct,spread_1pct_pct,managed_mb,rss_mb,"
            + "settled_after_s,settle_timeouts");
        foreach (string row in csvRows) sb.AppendLine(row);
        File.WriteAllText(csvPath, sb.ToString());

        // The orchestration script watches for this file, then stops the client through
        // its pidfile. Writing a marker beats having the mod try to close the game.
        File.WriteAllText(Path.Combine(outDir, $"{Sanitize(label)}.done"), csvPath + "\n");

        Mod.Logger.Notification("Bench '{0}' complete: {1} waypoints written to {2}", label, csvRows.Count, csvPath);
    }

    // Satisfies both ModSystem.Dispose and the IDisposable that IRenderer requires;
    // there is nothing of our own to release.
    public override void Dispose() { }
}
