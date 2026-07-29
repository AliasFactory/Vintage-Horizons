using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Walks an area of the world outward from spawn, asking the server to load each chunk
/// column so the capture pipeline sees it. Turns a freshly installed assist from "useful
/// once players have wandered" into "useful on the first join", which is the difference
/// between this competing with the server-side LOD mods and not.
///
/// This is the one place the mod generates terrain that nobody has visited, so it is
/// opt-in (<see cref="LodServerConfig.PregenRadiusChunks"/>, default 0), bounded, and
/// throttled: chunk columns are requested a few per second and left to unload naturally
/// rather than pinned, because a radius worth having is far more terrain than a server can
/// hold in memory at once.
///
/// Spiral order, not row-major: it is the order coverage is actually wanted in if the run
/// is interrupted - a partial spiral is a usable disc around spawn, a partial raster is a
/// band across the map.
/// </summary>
public class LodServerPregen
{
    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly int radiusChunks;
    readonly int perSecond;

    int index;
    int reported;
    long listenerId;

    public bool Done { get; private set; }
    public int Requested => index;
    public int Total => (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

    public LodServerPregen(ICoreServerAPI sapi, ILogger logger, int radiusChunks, int perSecond)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.radiusChunks = radiusChunks;
        this.perSecond = Math.Max(1, perSecond);
    }

    public void Start()
    {
        logger.Notification(
            "Pre-generating {0} chunk columns ({1} block radius around spawn) to build the LOD "
            + "cache. This generates terrain nobody has visited; set PregenRadiusChunks to 0 to "
            + "disable. Progress is logged every 10%.",
            Total, radiusChunks * GlobalConstants.ChunkSize);

        listenerId = sapi.Event.RegisterGameTickListener(_ => Step(), 1000);
    }

    void Step()
    {
        if (Done) return;

        int spawnCx = (int)sapi.World.DefaultSpawnPosition.X / GlobalConstants.ChunkSize;
        int spawnCz = (int)sapi.World.DefaultSpawnPosition.Z / GlobalConstants.ChunkSize;

        for (int n = 0; n < perSecond && index < Total; n++, index++)
        {
            (int dx, int dz) = SpiralAt(index);
            // Not KeepLoaded: the point is to have each column pass through the capture
            // pipeline once, not to hold a whole region resident.
            sapi.WorldManager.LoadChunkColumnPriority(spawnCx + dx, spawnCz + dz);
        }

        int percent = index * 100 / Total;
        if (percent >= reported + 10)
        {
            reported = percent - percent % 10;
            logger.Notification("LOD pre-generation {0}% ({1}/{2} columns requested)",
                reported, index, Total);
        }

        if (index < Total) return;

        Done = true;
        sapi.Event.UnregisterGameTickListener(listenerId);
        logger.Notification(
            "LOD pre-generation finished: {0} columns requested. Capture continues in the "
            + "background; the cache is complete once no columns remain queued.", Total);
    }

    /// <summary>
    /// Index -> offset on a square spiral centred on 0,0. Walks ring by ring, so any
    /// prefix of the sequence is a filled square around spawn.
    /// </summary>
    public static (int X, int Z) SpiralAt(int i)
    {
        if (i == 0) return (0, 0);

        // Which ring: the k-th ring ends at index (2k+1)^2 - 1.
        int ring = (int)Math.Ceiling((Math.Sqrt(i + 1) - 1) / 2);
        int ringStart = (2 * ring - 1) * (2 * ring - 1);
        int offset = i - ringStart;
        int side = 2 * ring;

        return (offset / side) switch
        {
            0 => (ring, -ring + 1 + offset % side),
            1 => (ring - 1 - offset % side, ring),
            2 => (-ring, ring - 1 - offset % side),
            _ => (-ring + 1 + offset % side, -ring),
        };
    }
}
