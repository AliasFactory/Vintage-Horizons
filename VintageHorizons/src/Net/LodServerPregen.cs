using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Walks an area of the world outward from the spawn point. It asks the server to load each
/// chunk column, thus the capture pipeline sees that column.
///
/// This changes a new installation of the assist. Without it, the assist is useful only
/// after the players travel. With it, the assist is useful at the first join. That
/// difference decides whether this mod competes with the server-side LOD mods.
///
/// This is the one place where the mod generates terrain that nobody visited. Thus an admin
/// must ask for it, through <see cref="LodServerConfig.PregenRadiusChunks"/>, which is 0 by
/// default. It also has a limit and a rate.
///
/// The mod requests a small number of chunk columns each second, and it lets them unload
/// normally. It does not hold them. A radius that is large enough to be useful holds much
/// more terrain than a server can hold in memory at one time.
///
/// The order is a spiral, and not row by row. A spiral gives the coverage in the order that
/// a user wants, if the run stops early. A partial spiral is a usable disc around the spawn
/// point. A partial raster is a band across the map.
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
            // Do not use KeepLoaded. Each column must go through the capture pipeline one
            // time. The mod must not hold a full region resident.
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
    /// Turns an index into an offset on a square spiral with its center at 0,0. It goes ring
    /// by ring. Thus any prefix of the sequence is a full square around the spawn point.
    /// </summary>
    public static (int X, int Z) SpiralAt(int i)
    {
        if (i == 0) return (0, 0);

        // Find the ring. Ring k ends at the index (2k+1)^2 - 1.
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
