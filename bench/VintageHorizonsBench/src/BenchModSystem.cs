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
///   VHBENCH_SETTLE  seconds to wait at each waypoint before measuring (default 20)
///   VHBENCH_MEASURE seconds to measure at each waypoint (default 10)
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
    double measureSec = 10;

    enum Phase { WaitingForJoin, Settling, Measuring, Done }

    Phase phase = Phase.WaitingForJoin;
    int waypointIndex = -1;
    double phaseStartedAt;
    double nowSec;

    readonly List<double> frameMs = new(4096);
    readonly List<string> csvRows = new();

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

        try
        {
            route = BenchRoute.Load(routePath);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Bench route {0} could not be read: {1}", routePath, e);
            return;
        }

        Directory.CreateDirectory(outDir);
        Mod.Logger.Notification("Bench armed: label '{0}', {1} waypoints, settle {2}s, measure {3}s, out {4}",
            label, route.Waypoints.Count, settleSec, measureSec, outDir);

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
        capi.Event.RegisterCallback(_ => capi.SendChatMessage("/time set 12:00"), 1500);
        capi.Event.RegisterCallback(_ => capi.SendChatMessage("/weather setprecip 0"), 2500);
        capi.Event.RegisterCallback(_ => AdvanceToNextWaypoint(), 4000);
    }

    void AdvanceToNextWaypoint()
    {
        if (route == null) return;

        waypointIndex++;
        if (waypointIndex >= route.Waypoints.Count)
        {
            Finish();
            return;
        }

        BenchWaypoint wp = route.Waypoints[waypointIndex];
        capi.SendChatMessage($"/tp ={wp.X.ToString("0.##", CultureInfo.InvariantCulture)} " +
                             $"{wp.Y.ToString("0.##", CultureInfo.InvariantCulture)} " +
                             $"={wp.Z.ToString("0.##", CultureInfo.InvariantCulture)}");

        phase = Phase.Settling;
        phaseStartedAt = nowSec;
        Mod.Logger.Notification("Bench waypoint {0}/{1} '{2}': settling {3}s",
            waypointIndex + 1, route.Waypoints.Count, wp.Name, settleSec);
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
            // Give the mod under test time to stream, build and upload its terrain, so
            // the measurement reflects steady state rather than the load burst.
            if (elapsed >= settleSec)
            {
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
            CaptureScreenshot(wp);
            AdvanceToNextWaypoint();
        }
    }

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

        csvRows.Add(string.Join(",", new[]
        {
            Csv(label), Csv(wp.Name),
            wp.X.ToString("0.##", CultureInfo.InvariantCulture),
            wp.Y.ToString("0.##", CultureInfo.InvariantCulture),
            wp.Z.ToString("0.##", CultureInfo.InvariantCulture),
            frameMs.Count.ToString(CultureInfo.InvariantCulture),
            (1000.0 / mean).ToString("0.0", CultureInfo.InvariantCulture),
            (1000.0 / median).ToString("0.0", CultureInfo.InvariantCulture),
            (1000.0 / worstMean).ToString("0.0", CultureInfo.InvariantCulture),
            mean.ToString("0.00", CultureInfo.InvariantCulture),
            worstMean.ToString("0.00", CultureInfo.InvariantCulture),
            managedMb.ToString(CultureInfo.InvariantCulture),
            rssMb.ToString(CultureInfo.InvariantCulture),
        }));

        Mod.Logger.Notification(
            "Bench '{0}' at '{1}': {2:0.0} fps avg, {3:0.0} fps 1% low, {4} frames, {5} MB RSS",
            label, wp.Name, 1000.0 / mean, 1000.0 / worstMean, frameMs.Count, rssMb);
    }

    static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

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

        string csvPath = Path.Combine(outDir, $"{Sanitize(label)}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("label,waypoint,x,y,z,frames,fps_avg,fps_median,fps_1pct_low,frame_ms_avg,frame_ms_1pct_low,managed_mb,rss_mb");
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
