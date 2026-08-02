using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the LOD cache from the terrain that the world holds already. It loads the chunk
/// columns that an earlier session generated.
///
/// This is the cheap half of pre-generation, and it is the half that is correct to have on
/// by default. A savegame collects terrain for as long as anyone plays. But the LOD cache
/// saw only the part that streamed past a player who runs this mod. On a test world that was
/// 12,632 generated columns against 620 captured sections. A world that people played for
/// weeks gives a larger difference. All of that data is on the disk already, and somebody
/// paid for it already.
///
/// The difference from <see cref="LodServerPregen"/> is the point of this class.
/// Pre-generation *creates* terrain that nobody visited. It costs worldgen time and disk
/// space, and it reveals places where no player went. A sweep creates nothing. It indexes
/// what exists. Thus it is safe to have on by default, and pre-generation is not.
///
/// To keep that promise, a check of the target column alone is not sufficient. The first
/// version did only that check, and this version has two passes for that reason.
///
/// A load of a column whose surround is absent makes the engine generate that surround.
/// Worldgen runs in passes, and one column completes only after its neighbours reach an
/// earlier pass. That needs *their* neighbours also.
///
/// A one-pass sweep of 8,464 existing columns added 1,460 new columns to the savegame. A
/// sweep of the same world, with a radius that was fully inside the generated terrain, added
/// exactly zero. That measurement identified the frontier as the cause, and not the loads.
///
/// Thus this class probes each position first. Then it loads only a column whose full
/// neighbourhood is on the disk already. The cost is a border of real terrain that the mod
/// does not capture. For any world that is large enough to sweep, that border is very small
/// against the promise to generate nothing.
/// </summary>
public class LodSavegameSweep
{
    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly int radiusChunks;
    readonly int perSecond;

    /// <summary>
    /// The probes that wait for an answer from the engine. This value has a limit. Without
    /// the limit, the spiral puts each position into the queue on the first tick. For the
    /// default radius that is 66k callbacks, and they all arrive before anything useful
    /// occurs.
    /// </summary>
    const int MaxProbesInFlight = 256;

    /// <summary>The positions that hold generated terrain, packed as cz&lt;&lt;32 |
    /// cx.</summary>
    readonly HashSet<long> exists = new();

    int probeIndex;
    int probesInFlight;
    int loadIndex;
    int reported;
    long listenerId;

    int spawnCx;
    int spawnCz;

    /// <summary>
    /// The probe reaches past the load area by the width of the neighbourhood. Thus a column
    /// at the edge of the sweep still has known neighbours. Without this, the mod skips that
    /// column because it has no information about positions that nobody examined.
    /// </summary>
    int ProbeRadius => radiusChunks + SafeNeighbourhood;
    int ProbeTotal => (2 * ProbeRadius + 1) * (2 * ProbeRadius + 1);
    int LoadTotal => (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

    public bool Probing { get; private set; } = true;
    public bool Done { get; private set; }

    /// <summary>The positions that held generated terrain. The sweep does not load all of
    /// them.</summary>
    public int Found => exists.Count;

    /// <summary>The columns that the sweep loaded. Each one has a complete
    /// neighbourhood.</summary>
    public int Loaded { get; private set; }

    /// <summary>The columns that exist, but that the sweep skipped because a neighbour was
    /// absent.</summary>
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
            "Sweeping the savegame for terrain that exists already, out to {0} blocks around "
            + "spawn. There are {1} positions to examine. This generates nothing. The sweep "
            + "skips each position that nobody visited. It also skips a column at the edge of "
            + "the explored terrain, because a load of that column would make the engine "
            + "generate its absent neighbours. To stop the sweep, set SweepSavegame to false. "
            + "Progress follows every 10%.",
            radiusChunks * GlobalConstants.ChunkSize, ProbeTotal);

        listenerId = sapi.Event.RegisterGameTickListener(_ => Step(), 1000);
    }

    static long Key(int cx, int cz) => ((long)cz << 32) | (uint)cx;

    void Step()
    {
        if (Done) return;
        if (Probing) StepProbe();
        else StepLoad();
    }

    void StepProbe()
    {
        // Fill up to a limit. Do not send a fixed number in each tick. Thus the sweep runs
        // at the rate at which the engine answers, and it never has more probes open than
        // the limit.
        while (probeIndex < ProbeTotal && probesInFlight < MaxProbesInFlight)
        {
            (int dx, int dz) = LodServerPregen.SpiralAt(probeIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            probesInFlight++;
            sapi.WorldManager.TestMapChunkExists(cx, cz, hit =>
            {
                // The callback can occur on a thread other than the main thread, and a
                // HashSet is not safe for more than one thread.
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    probesInFlight--;
                    if (hit) exists.Add(Key(cx, cz));
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
            "Savegame sweep: {0} of {1} positions hold generated terrain. The sweep now loads "
            + "each of those that has a complete neighbourhood.", exists.Count, ProbeTotal);
    }

    void StepLoad()
    {
        int loaded = 0;
        while (loadIndex < LoadTotal && loaded < perSecond)
        {
            (int dx, int dz) = LodServerPregen.SpiralAt(loadIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            if (!exists.Contains(Key(cx, cz))) continue;

            if (!NeighbourhoodComplete(cx, cz))
            {
                // This column is on the frontier of the explored terrain. A load of it
                // generates what is absent beside it. That is the one thing that this class
                // must not do.
                SkippedEdge++;
                continue;
            }

            // Do not use KeepLoaded. Each column must go through the capture one time, and
            // it must not stay resident. A radius that is large enough to sweep holds much
            // more terrain than the memory can hold.
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

        Done = true;
        sapi.Event.UnregisterGameTickListener(listenerId);
        logger.Notification(
            "Savegame sweep finished: {0} columns loaded from terrain that existed already, "
            + "{1} skipped on the frontier, nothing generated. The capture continues in the "
            + "background. The cache is complete when no column remains in the queue.",
            Loaded, SkippedEdge);
    }

    /// <summary>
    /// The distance over which the neighbourhood must be intact, before a load of a column
    /// is safe.
    ///
    /// The value is four. Measurement gave this value, and reasoning did not. The dependency
    /// reaches much further than one ring.
    ///
    /// One world was swept at each setting. The table gives the chunk columns that the
    /// savegame gained:
    ///
    ///   no check   1460 generated
    ///   radius 1    714 generated
    ///   radius 2    509 generated
    ///   radius 4      0 generated
    ///
    /// Radius 3 was not tested. Thus this value can be one step wider than necessary. A
    /// value that is too wide is the correct direction for an error. A value that is too
    /// narrow breaks the one promise that this function makes, and it gives no message. A
    /// value that is too wide costs a slightly thicker border of terrain that the mod does
    /// not capture.
    /// </summary>
    const int SafeNeighbourhood = 4;

    /// <summary>
    /// True when each position within <see cref="SafeNeighbourhood"/> of this column is on
    /// the disk also. Then a load of this column cannot make the engine generate anything to
    /// complete it.
    /// </summary>
    bool NeighbourhoodComplete(int cx, int cz)
    {
        for (int dz = -SafeNeighbourhood; dz <= SafeNeighbourhood; dz++)
        {
            for (int dx = -SafeNeighbourhood; dx <= SafeNeighbourhood; dx++)
            {
                if (!exists.Contains(Key(cx + dx, cz + dz))) return false;
            }
        }
        return true;
    }

    /// <summary>One line for /vhserver. This is null when the settings configured no
    /// sweep.</summary>
    public string Status => Done
        ? $"savegame sweep complete ({Loaded} columns loaded, {SkippedEdge} skipped on the frontier)"
        : Probing
            ? $"sweeping savegame: examined {probeIndex}/{ProbeTotal}, {exists.Count} hold terrain"
            : $"sweeping savegame: loaded {Loaded}, {SkippedEdge} skipped on the frontier";
}
