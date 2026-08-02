using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the LOD cache around a player on request (/vhgen), including terrain that
/// nobody has generated.
///
/// Three phases. PROBE asks the engine which columns exist, exactly as the sweep does.
/// WORK walks the same spiral and acts on <see cref="LodColumnMap.Classify"/>: columns
/// that exist load through the normal capture path, and columns that do not exist are
/// PEEKED - PeekChunkColumn runs real worldgen from the seed and hands the column back
/// without writing the savegame or the loaded chunk list. VERIFY then re-probes a
/// sample of positions that were absent, because "a peek persists nothing" is a promise
/// this mod makes to server admins, and a promise gets measured, not trusted.
///
/// Peeked columns reach capture through <see cref="LodPipeline.CaptureColumn"/>. The
/// BlockAccessor cannot see a peeked column, so the ChunkColumnLoaded path that serves
/// the sweep never fires for them.
///
/// Measured constants come from TopoHorizon (MIT, (c) 2026 Jack Brown), confirmed on
/// this engine build where noted: a Terrain-pass peek returns a 1x1 area with the rain
/// height map populated (confirmed), the callback lands on the main thread (confirmed),
/// serial peeks cost ~250ms issue-to-callback here and saturate near 23/s. The
/// stuck-peek timeout is TopoHorizon's 300s - their 60s value produced 60-70% false
/// timeouts under load.
///
/// The Vegetation pass is deliberately not offered. It throws NullReferenceException
/// inside vanilla worldgen (ForestFloorSystem.CreateForestFloor and
/// BlockFruitTreeBranch.TryPlaceBlockForWorldGen) unless a Harmony finalizer swallows
/// it, and this mod ships no Harmony patches. Generated terrain therefore has no
/// trees. Real capture overwrites it whenever a player actually visits.
/// </summary>
public class LodPlayerPregen
{
    /// <summary>Worldgen pass each peek runs to. A constant, not a setting - see above.</summary>
    public const EnumWorldGenPass Pass = EnumWorldGenPass.Terrain;

    const int MaxProbesInFlight = 256;    // as LodSavegameSweep
    const int StuckPeekTimeoutMs = 300_000;

    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly LodPipeline pipeline;
    readonly int centreCx, centreCz, radiusChunks, perSecond, maxInFlight;

    /// <summary>Which positions hold generated terrain, and the safety rule over them.</summary>
    readonly LodColumnMap exists = new();

    /// <summary>Peeks outstanding, key to issue time. Expired entries count as TimedOut.</summary>
    readonly Dictionary<long, long> peeksInFlight = new();
    readonly List<long> stale = new();

    int probeIndex;
    int probesInFlight;
    int workIndex;
    int reported;
    long listenerId;
    LodAbsenceVerifier? verifier;

    public string StartedBy { get; }
    public bool Probing { get; private set; } = true;
    public bool Verifying { get; private set; }
    public bool Done { get; private set; }
    public bool Cancelled { get; private set; }

    /// <summary>Peeked columns that produced a capture.</summary>
    public int Generated { get; private set; }

    /// <summary>Existing columns loaded from the savegame, as the sweep would.</summary>
    public int Indexed { get; private set; }

    /// <summary>Existing columns skipped because a neighbour was missing.</summary>
    public int SkippedFrontier { get; private set; }

    /// <summary>Peeked columns that came back without a usable rain height map.</summary>
    public int NoHeightMap { get; private set; }

    /// <summary>Peeks whose callback never fired within the timeout. Not retried.</summary>
    public int TimedOut { get; private set; }

    int ProbeRadius => radiusChunks + LodColumnMap.SafeNeighbourhood;
    int ProbeTotal => (2 * ProbeRadius + 1) * (2 * ProbeRadius + 1);
    public int WorkTotal => ColumnsFor(radiusChunks);
    public int CentreBlockX => centreCx * GlobalConstants.ChunkSize;
    public int CentreBlockZ => centreCz * GlobalConstants.ChunkSize;

    /// <summary>Columns in a square of this radius. Printed to the person who typed the command.</summary>
    public static int ColumnsFor(int radiusChunks) => (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

    /// <summary>A round lower bound: the engine saturates near 23 columns/s regardless of the rate.</summary>
    public static int EstimateSeconds(int columns, int perSecond) =>
        columns / Math.Max(1, Math.Min(perSecond, 23));

    public LodPlayerPregen(ICoreServerAPI sapi, ILogger logger, LodPipeline pipeline,
        int centreCx, int centreCz, int radiusChunks, LodServerConfig config, string startedBy)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.pipeline = pipeline;
        this.centreCx = centreCx;
        this.centreCz = centreCz;
        this.radiusChunks = radiusChunks;
        perSecond = Math.Max(1, config.GenerateColumnsPerSecond);
        maxInFlight = Math.Max(1, config.GenerateMaxInFlight);
        StartedBy = startedBy;
    }

    public void Start()
    {
        logger.Notification(
            "Generation started by {0} around block {1},{2}: {3} columns out to {4} chunks. "
            + "Columns nobody generated are peeked from the seed and captured; nothing is "
            + "written to the savegame and no chunk is loaded for any player. Progress every 10%.",
            StartedBy, CentreBlockX, CentreBlockZ, WorkTotal, radiusChunks);

        listenerId = sapi.Event.RegisterGameTickListener(_ => Step(), 1000);
    }

    /// <summary>
    /// Stop issuing new work. Peeks already in flight still get captured - that worldgen
    /// time is spent either way, and throwing the result away helps nobody. The verify
    /// phase still runs, so a stopped run still reports whether the promise held.
    /// </summary>
    public void Cancel() => Cancelled = true;

    void Step()
    {
        if (Done || Verifying) return;
        ExpireStuckPeeks();
        if (Probing && !Cancelled) StepProbe();
        else StepWork();
    }

    void StepProbe()
    {
        // Refill to a cap rather than issuing a fixed number per tick, as the sweep does.
        while (probeIndex < ProbeTotal && probesInFlight < MaxProbesInFlight)
        {
            (int dx, int dz) = LodServerPregen.SpiralAt(probeIndex++);
            int cx = centreCx + dx;
            int cz = centreCz + dz;

            probesInFlight++;
            sapi.WorldManager.TestMapChunkExists(cx, cz, hit =>
            {
                // The callback need not be on the main thread, and the map is not safe.
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    probesInFlight--;
                    if (hit) exists.Add(cx, cz);
                }, "vh-generate-probe");
            });
        }

        Report(probeIndex, ProbeTotal,
            $"Generation: examined {{0}}% ({probeIndex}/{ProbeTotal} positions), {exists.Count} hold terrain");

        if (probeIndex < ProbeTotal || probesInFlight > 0) return;

        Probing = false;
        reported = 0;
        logger.Notification(
            "Generation: {0} of {1} positions hold terrain already. Existing columns load "
            + "from the savegame; the rest are generated transiently.", exists.Count, ProbeTotal);
    }

    void StepWork()
    {
        int started = 0;
        bool gated = false;
        while (!Cancelled && !gated && workIndex < WorkTotal && started < perSecond)
        {
            (int dx, int dz) = LodServerPregen.SpiralAt(workIndex);
            int cx = centreCx + dx;
            int cz = centreCz + dz;

            switch (exists.Classify(cx, cz))
            {
                case EnumColumnAction.Peek:
                    // Both gates bound this run's memory, not only its CPU: a peek that
                    // lands becomes a whole chunk column held until capture drains it.
                    // Stop the tick without advancing - the position retries next tick.
                    if (peeksInFlight.Count >= maxInFlight || pipeline.CaptureBacklogFull)
                    {
                        gated = true;
                        continue;
                    }
                    workIndex++; started++;
                    IssuePeek(cx, cz);
                    continue;

                case EnumColumnAction.Load:
                    // Loading is safe here for the same reason it is in the sweep: the
                    // whole neighbourhood exists, so the engine has nothing to
                    // complete. Capture goes through OnLoaded -> QueueColumn rather
                    // than the ChunkColumnLoaded event, because in singleplayer with
                    // sweeping off nothing subscribes that event. QueueColumn dedups,
                    // so on a dedicated server, where the event fires too, the column
                    // still captures once.
                    workIndex++; started++;
                    sapi.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
                    {
                        OnLoaded = () => pipeline.QueueColumn(cx, cz),
                    });
                    Indexed++;
                    continue;

                default:
                    // On the frontier. A load would generate the missing neighbours,
                    // and a peek would erase edits. Nothing safe exists to do.
                    workIndex++;
                    SkippedFrontier++;
                    continue;
            }
        }

        Report(workIndex, WorkTotal,
            $"Generation: {{0}}% ({workIndex}/{WorkTotal} columns) - {Generated} generated, "
            + $"{Indexed} loaded, {SkippedFrontier} skipped on the frontier");

        bool workOver = Cancelled || workIndex >= WorkTotal;
        if (workOver && peeksInFlight.Count == 0) BeginVerify();
    }

    void IssuePeek(int cx, int cz)
    {
        peeksInFlight[LodColumnMap.Key(cx, cz)] = sapi.World.ElapsedMilliseconds;
        sapi.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
        {
            UntilPass = Pass,
            OnGenerated = columns => OnPeeked(cx, cz, columns),
        });
    }

    void OnPeeked(int cx, int cz, Dictionary<Vec2i, IServerChunk[]>? columns)
    {
        // Capture first, on whatever thread this is: CaptureColumn is safe from any
        // thread and copies what it needs. Only the bookkeeping must reach the main
        // thread. The callback arrives on the main thread on the tested build, but the
        // API does not promise that, so nothing here relies on it.
        bool captured = false;
        if (columns != null
            && columns.TryGetValue(new Vec2i(cx, cz), out IServerChunk[]? column)
            && column is { Length: > 0 })
        {
            // A peeked column is not in the loaded list, so its map chunk has to come
            // off the column itself rather than through the BlockAccessor.
            ushort[]? rain = column[0].MapChunk?.RainHeightMap;
            if (rain != null) captured = pipeline.CaptureColumn(cx, cz, column, rain);
        }

        sapi.Event.EnqueueMainThreadTask(() =>
        {
            // Ignore a callback that fires after its timeout already counted it.
            if (!peeksInFlight.Remove(LodColumnMap.Key(cx, cz))) return;
            if (captured)
            {
                Generated++;
            }
            else if (++NoHeightMap == 1)
            {
                // The first failure is the loud one. Every column failing this way
                // means generation captures nothing at all, and a silent no-op is the
                // worst failure this feature can have.
                logger.Warning(
                    "Peeked column {0},{1} had no usable rain height map, so nothing was "
                    + "captured from it. If every column reports this, generation is doing "
                    + "nothing and the worldgen pass is the thing to investigate.", cx, cz);
            }
        }, "vh-generate-peeked");
    }

    void ExpireStuckPeeks()
    {
        if (peeksInFlight.Count == 0) return;
        long cutoff = sapi.World.ElapsedMilliseconds - StuckPeekTimeoutMs;
        stale.Clear();
        foreach (KeyValuePair<long, long> entry in peeksInFlight)
        {
            if (entry.Value < cutoff) stale.Add(entry.Key);
        }
        if (stale.Count == 0) return;

        foreach (long key in stale) peeksInFlight.Remove(key);
        TimedOut += stale.Count;

        // No retry, deliberately. A command-driven run must terminate, and a coordinate
        // that sticks once tends to stick again. The timeout also covers a worldgen
        // handler that threw inside the engine's own thread, where this mod has no call
        // site to catch anything - the only symptom is a callback that never comes.
        logger.Warning(
            "Generation: {0} peeks never called back and were given up on ({1} total). "
            + "Those columns are missing from the cache; run /vhgen again to retry them.",
            stale.Count, TimedOut);
    }

    void BeginVerify()
    {
        Verifying = true;
        verifier = new LodAbsenceVerifier(sapi,
            exists.AbsentSample(centreCx, centreCz, radiusChunks, LodAbsenceVerifier.MaxSample),
            Finish);
        verifier.Start();
    }

    void Finish(LodAbsenceVerifier verified)
    {
        Done = true;
        sapi.Event.UnregisterGameTickListener(listenerId);

        string counters =
            $"{Generated} generated, {Indexed} loaded from the savegame, {SkippedFrontier} "
            + $"skipped on the frontier, {NoHeightMap} without a height map, {TimedOut} timed out";

        if (verified.Regrown > 0)
        {
            logger.Warning(
                "Generation: {0} of {1} sampled positions that did not exist before the run "
                + "now exist in the savegame. A peek must persist nothing, so something on "
                + "this server generated terrain during the run - a worldgen mod is the "
                + "likely cause. Treat /vhgen as unsafe here, and please report this.",
                verified.Regrown, verified.Checked);
        }

        if (Cancelled)
        {
            logger.Notification(
                "Generation stopped by request after {0}/{1} columns: {2}. {3}.",
                workIndex, WorkTotal, counters, verified.Describe());
            return;
        }

        logger.Notification(
            "Generation finished around block {0},{1}: {2} columns - {3}. {4}. Capture "
            + "continues in the background; the cache is complete once no columns remain queued.",
            CentreBlockX, CentreBlockZ, WorkTotal, counters, verified.Describe());
    }

    void Report(int index, int total, string format)
    {
        int percent = index * 100 / Math.Max(1, total);
        if (percent < reported + 10) return;
        reported = percent - percent % 10;
        logger.Notification(string.Format(format, reported));
    }

    /// <summary>One line for /vhgen status and /vhserver.</summary>
    public string Status =>
        Done
            ? $"generation {(Cancelled ? "stopped" : "finished")}: {WorkTotal} columns around "
              + $"{CentreBlockX},{CentreBlockZ} - {Generated} generated, {Indexed} loaded, "
              + $"{SkippedFrontier} skipped on the frontier, {TimedOut} timed out"
        : Verifying
            ? $"generation verifying: re-probing sampled positions around {CentreBlockX},{CentreBlockZ}"
        : Probing
            ? $"generation probing around {CentreBlockX},{CentreBlockZ}: examined "
              + $"{probeIndex}/{ProbeTotal} positions, {exists.Count} hold terrain"
            : $"generating around {CentreBlockX},{CentreBlockZ} (started by {StartedBy}): "
              + $"{workIndex}/{WorkTotal} columns - {Generated} generated, {Indexed} loaded, "
              + $"{SkippedFrontier} skipped, {NoHeightMap} without height maps, {TimedOut} timed "
              + $"out, {peeksInFlight.Count} peeks in flight";
}
