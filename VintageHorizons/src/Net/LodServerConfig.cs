namespace VintageHorizons.Net;

/// <summary>
/// The settings for an admin on the server side, in
/// <c>ModConfig/vintagehorizons-server.json</c> (DESIGN.md section 10.6). The mod writes
/// this file at the first start. Thus an admin finds the options without a read of the
/// source.
///
/// Serving is on by default. The installation of the mod on a server is the decision to
/// opt in. A mod that does nothing until a person edits a file appears to be broken.
///
/// The radius is the conservative part. An admin who wants no map sharing sets
/// <see cref="EnableServing"/> to false. An admin who wants some sharing gets a limited
/// quantity by default, and not the full world.
/// </summary>
public class LodServerConfig
{
    /// <summary>
    /// Build an LOD cache on the server. When this is off, the server keeps no cache and
    /// gives nothing, and the other settings have no effect. Each client continues to
    /// operate with its own captures, exactly as on a vanilla server.
    /// </summary>
    public bool EnableCapture = true;

    /// <summary>Answer the requests of a client. When off, the cache stays, but the
    /// server gives none of it.</summary>
    public bool EnableServing = true;

    /// <summary>
    /// The distance from a player at which the assist gives data, in blocks. 0 is
    /// unlimited.
    ///
    /// This is the control for the map-revealing problem. Sections come from wherever the
    /// players went together. Thus without a limit, a new player can take a survey of the
    /// full explored world without travel. That survey shows the coastlines, the
    /// structures and the bases of other players. A value of 8192 still gives a very large
    /// horizon, and it keeps that knowledge local.
    /// </summary>
    public int ServeRadiusBlocks = 8192;

    /// <summary>Sections given to each player each second. LodAssist gives the
    /// reasons.</summary>
    public int MaxSectionsPerSecondPerPlayer = LodAssist.MaxSectionsPerSecondPerPlayer;

    /// <summary>
    /// Sections given each second, across all players. This limit bounds the cost to the
    /// server, because each section that it gives is a blob read on the main thread.
    /// </summary>
    public int MaxSectionsPerSecondTotal = LodAssist.MaxSectionsPerSecondTotal;

    /// <summary>
    /// Build the cache from the terrain that the world holds already. The sweep loads each
    /// chunk column around the spawn point that an earlier session generated.
    ///
    /// This is on by default. <see cref="PregenRadiusChunks"/> is deliberately off,
    /// because the two settings do different work.
    ///
    /// A sweep generates nothing. It indexes terrain that exists already. It costs no
    /// worldgen time. It adds no disk space except the LOD cache itself. It reveals no
    /// place where a player did not go.
    ///
    /// A savegame collects terrain for as long as anyone plays. But the LOD cache saw only
    /// the part that streamed past a player who runs this mod.
    /// </summary>
    public bool SweepSavegame = true;

    /// <summary>
    /// The distance to examine, in chunks. The sweep examines each position inside that
    /// distance. It skips a position that has no generated terrain. Thus a small world
    /// costs little, whatever this value is. A value of 0 stops the sweep, exactly as
    /// SweepSavegame false does.
    /// </summary>
    public int SweepRadiusChunks = 128;

    /// <summary>
    /// Generated columns to load each second. This is higher than the pre-generation rate,
    /// because the work is a deserialize and not a worldgen pass. At startup the sweep
    /// also competes with nothing on a server that is otherwise idle.
    /// </summary>
    public int SweepColumnsPerSecond = 16;

    /// <summary>
    /// Build the cache in advance. The mod generates a square of chunk columns around the
    /// spawn point, and this value is the radius in chunks. The default of 0 means never,
    /// and then the cache fills only as the players travel.
    ///
    /// This is the one setting that makes the mod generate terrain that nobody visited.
    /// Thus it is off until an admin asks for it.
    ///
    /// It is worth the request. It is the difference between a horizon at the first join
    /// and a horizon that appears after weeks of play. The cost is worldgen time and disk
    /// space. At the measured mean of 45.9 KB for a section, radius 64, which is a square
    /// of 4096 blocks, costs approximately a few hundred MB.
    /// </summary>
    public int PregenRadiusChunks;

    /// <summary>Chunk columns to request each second during pre-generation. Keep this
    /// value small.</summary>
    public int PregenColumnsPerSecond = 8;

    /// <summary>Clamp each value to a range that cannot stop the server, whatever the file
    /// holds.</summary>
    public void Sanitize()
    {
        if (ServeRadiusBlocks < 0) ServeRadiusBlocks = 0;
        // 256 chunks is a radius of 4096 blocks. Above that value, an admin cannot accept
        // the disk cost by accident.
        PregenRadiusChunks = Math.Clamp(PregenRadiusChunks, 0, 256);
        PregenColumnsPerSecond = Math.Clamp(PregenColumnsPerSecond, 1, 64);
        // This limit is wider than the pre-generation limit. The cost increases with the
        // terrain that exists, and not with the radius. An examination of a position that
        // nothing generated is an index lookup. Thus a large radius over a small world is
        // almost free.
        SweepRadiusChunks = Math.Clamp(SweepRadiusChunks, 0, 512);
        SweepColumnsPerSecond = Math.Clamp(SweepColumnsPerSecond, 1, 64);
        // These limits come from measurement, not from opinion. One section that the
        // server gives costs approximately 0.9 ms of SQLite blob read on the main thread.
        // The measurement was 415 sections in 348 ms, on a warm cache.
        //
        // Thus 128 each second is approximately 115 ms each second, which is approximately
        // 11% of one core. That is the maximum that an admin can give to this by an edit
        // of a file. The original value of 1024 is approximately 920 ms each second, which
        // stops the server through its own settings.
        MaxSectionsPerSecondPerPlayer = Math.Clamp(MaxSectionsPerSecondPerPlayer, 1, 64);
        MaxSectionsPerSecondTotal = Math.Clamp(MaxSectionsPerSecondTotal, 1, 128);
    }

    /// <summary>True when the settings make the sweep do work.</summary>
    public bool SweepEnabled => SweepSavegame && SweepRadiusChunks > 0;

    public string Describe() =>
        $"capture {(EnableCapture ? "on" : "off")}, serving {(EnableServing ? "on" : "off")}, "
        + $"radius {(ServeRadiusBlocks > 0 ? ServeRadiusBlocks + " blocks" : "unlimited")}, "
        + $"{MaxSectionsPerSecondPerPlayer}/s per player, {MaxSectionsPerSecondTotal}/s total, "
        + $"sweep {(SweepEnabled ? SweepRadiusChunks + " chunks" : "off")}";
}
