using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the LOD cache from terrain the world already has, by loading chunk columns that
/// were generated in some earlier session.
///
/// This is the cheap half of pre-generation and the one worth having on by default. A
/// savegame accumulates terrain for as long as anyone plays, while the LOD cache only ever
/// saw the fraction that happened to stream past a player running this mod - measured on a
/// test world at 12,632 generated columns against 620 captured sections, and a world played
/// for weeks skews far harder than that. All of it is already on disk, already paid for.
///
/// The distinction from pre-generation (<see cref="LodPlayerPregen"/>) is the whole
/// point: that creates terrain nobody has visited, and reveals places no player has
/// been. Sweeping creates nothing. It indexes what exists, so it is safe to default on
/// where pre-generation is not.
///
/// Keeping that promise takes more than checking the target column, which is what the first
/// version did and why this does two passes now. Loading a column whose surroundings are
/// absent makes the engine generate them, because worldgen runs in passes and finishing one
/// column requires its neighbours to have reached an earlier pass - which requires theirs.
/// A one-pass sweep of 8,464 existing columns added 1,460 brand new ones to the savegame.
/// Sweeping the same world with a radius that fell entirely inside generated terrain added
/// exactly zero, which is what identified the cause as the frontier rather than the loads.
///
/// So: probe everything first, then load only columns whose whole neighbourhood is already
/// on disk. The cost is a border of real terrain going uncaptured, which for any world worth
/// sweeping is a rounding error against not generating anything at all.
/// </summary>
public class LodSavegameSweep
{
    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly int radiusChunks;
    readonly int perSecond;

    /// <summary>
    /// Probes outstanding with the engine. Bounded because the spiral would otherwise queue
    /// every position on the first tick - 66k callbacks for the default radius, all landing
    /// before anything useful had happened.
    /// </summary>
    const int MaxProbesInFlight = 256;

    /// <summary>Which positions hold generated terrain, and the safety rule over them.</summary>
    readonly LodColumnMap exists = new();

    int probeIndex;
    int probesInFlight;
    int loadIndex;
    int reported;
    long listenerId;
    bool verifying;

    int spawnCx;
    int spawnCz;

    /// <summary>
    /// Probing reaches past the load area by the neighbourhood width, so a column on the
    /// edge of the sweep still has known neighbours rather than being skipped for want of
    /// information about positions nobody looked at.
    /// </summary>
    int ProbeRadius => radiusChunks + LodColumnMap.SafeNeighbourhood;
    int ProbeTotal => (2 * ProbeRadius + 1) * (2 * ProbeRadius + 1);
    int LoadTotal => (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

    public bool Probing { get; private set; } = true;
    public bool Done { get; private set; }

    /// <summary>Positions that held generated terrain. Not all of them get loaded.</summary>
    public int Found => exists.Count;

    /// <summary>Columns actually loaded - those with a complete neighbourhood.</summary>
    public int Loaded { get; private set; }

    /// <summary>Existing columns skipped because a neighbour was missing.</summary>
    public int SkippedEdge { get; private set; }

    public LodSavegameSweep(ICoreServerAPI sapi, ILogger logger, int radiusChunks, int perSecond)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.radiusChunks = radiusChunks;
        this.perSecond = Math.Max(1, perSecond);
    }

    public void Start()
    {
        spawnCx = (int)sapi.World.DefaultSpawnPosition.X / GlobalConstants.ChunkSize;
        spawnCz = (int)sapi.World.DefaultSpawnPosition.Z / GlobalConstants.ChunkSize;

        logger.Notification(
            "Sweeping the savegame for terrain that already exists, out to {0} blocks around "
            + "spawn ({1} positions to examine). Nothing is generated: columns that were never "
            + "visited are skipped, and so are columns on the edge of explored terrain, "
            + "because loading those would make the engine generate their missing neighbours. "
            + "Set SweepSavegame to false to disable. Progress every 10%.",
            radiusChunks * GlobalConstants.ChunkSize, ProbeTotal);

        listenerId = sapi.Event.RegisterGameTickListener(_ => Step(), 1000);
    }

    void Step()
    {
        if (Done || verifying) return;
        if (Probing) StepProbe();
        else StepLoad();
    }

    void StepProbe()
    {
        // Refill to a cap rather than issuing a fixed number per tick: the sweep then runs
        // at whatever rate the engine answers, without ever having more outstanding.
        while (probeIndex < ProbeTotal && probesInFlight < MaxProbesInFlight)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(probeIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            probesInFlight++;
            sapi.WorldManager.TestMapChunkExists(cx, cz, hit =>
            {
                // The callback need not be on the main thread, and HashSet is not safe.
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    probesInFlight--;
                    if (hit) exists.Add(cx, cz);
                }, "vh-sweep-probe");
            });
        }

        int percent = probeIndex * 100 / ProbeTotal;
        if (percent >= reported + 10)
        {
            reported = percent - percent % 10;
            logger.Notification("Savegame sweep: examined {0}% ({1}/{2}), {3} hold terrain",
                reported, probeIndex, ProbeTotal, exists.Count);
        }

        if (probeIndex < ProbeTotal || probesInFlight > 0) return;

        Probing = false;
        reported = 0;
        logger.Notification(
            "Savegame sweep: {0} of {1} positions hold generated terrain. Loading those with a "
            + "complete neighbourhood.", exists.Count, ProbeTotal);
    }

    void StepLoad()
    {
        int loaded = 0;
        while (loadIndex < LoadTotal && loaded < perSecond)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(loadIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            switch (exists.Classify(cx, cz))
            {
                case EnumColumnAction.Peek:
                    // Not in the savegame. The sweep indexes what exists and creates
                    // nothing, so there is no work here. Generation (/vhgen) is the
                    // feature that acts on this arm.
                    continue;

                case EnumColumnAction.SkipFrontier:
                    // On the frontier of explored terrain. A load here would generate
                    // whatever is missing beside it - the one thing this must not do.
                    SkippedEdge++;
                    continue;
            }

            // Not KeepLoaded: each column needs to pass through capture once, not stay
            // resident. A radius worth sweeping is far more terrain than fits in memory.
            sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
            Loaded++;
            loaded++;
        }

        int percent = loadIndex * 100 / LoadTotal;
        if (percent >= reported + 10)
        {
            reported = percent - percent % 10;
            logger.Notification("Savegame sweep: loaded {0}% ({1} columns, {2} skipped as edge)",
                reported, Loaded, SkippedEdge);
        }

        if (loadIndex < LoadTotal) return;

        // The promise gets measured before it gets announced: re-probe a sample of the
        // positions that did not exist, and report the result on the finish line.
        verifying = true;
        new LodAbsenceVerifier(sapi,
            exists.AbsentSample(spawnCx, spawnCz, ProbeRadius, LodAbsenceVerifier.MaxSample),
            Finish).Start();
    }

    void Finish(LodAbsenceVerifier verified)
    {
        Done = true;
        sapi.Event.UnregisterGameTickListener(listenerId);

        if (verified.Regrown > 0)
        {
            logger.Warning(
                "Savegame sweep: {0} of {1} sampled positions that did not exist before the "
                + "sweep now exist. The sweep must generate nothing, so worldgen on this "
                + "server reaches further than the measured safe neighbourhood - a worldgen "
                + "mod is the likely cause. Consider SweepSavegame: false here, and please "
                + "report this.", verified.Regrown, verified.Checked);
        }

        logger.Notification(
            "Savegame sweep finished: {0} columns loaded from terrain that already existed, "
            + "{1} skipped on the frontier, nothing generated. {2}. Capture continues in the "
            + "background; the cache is complete once no columns remain queued.",
            Loaded, SkippedEdge, verified.Describe());
    }

    /// <summary>One line for /vhserver; null when no sweep was configured.</summary>
    public string Status => Done
        ? $"savegame sweep complete ({Loaded} columns loaded, {SkippedEdge} skipped on the frontier)"
        : verifying
            ? $"savegame sweep verifying: re-probing {Loaded} loads' surroundings"
        : Probing
            ? $"sweeping savegame: examined {probeIndex}/{ProbeTotal}, {exists.Count} hold terrain"
            : $"sweeping savegame: loaded {Loaded}, {SkippedEdge} skipped on the frontier";
}
