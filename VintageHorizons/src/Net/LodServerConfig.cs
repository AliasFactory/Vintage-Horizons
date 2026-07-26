namespace VintageHorizons.Net;

/// <summary>
/// Admin knobs for the server side, in <c>ModConfig/vintagehorizons-server.json</c>
/// (DESIGN.md §10.6). Written out on first run so the options are discoverable without
/// reading the source.
///
/// Serving is on by default, because installing the mod on a server *is* the opt-in and a
/// mod that silently does nothing until a file is edited reads as broken. What is
/// deliberately conservative is the radius: an admin who wants no map sharing at all sets
/// <see cref="EnableServing"/> false, and an admin who wants some gets a bounded amount by
/// default rather than the whole world.
/// </summary>
public class LodServerConfig
{
    /// <summary>
    /// Build a server-side LOD cache at all. Off means the server keeps no cache and
    /// serves nothing, whatever the other settings say — clients still work, using their
    /// own captures exactly as on a vanilla server.
    /// </summary>
    public bool EnableCapture = true;

    /// <summary>Answer client requests. Off keeps the cache but shares none of it.</summary>
    public bool EnableServing = true;

    /// <summary>
    /// How far from a player the assist will serve, in blocks. 0 means unlimited.
    ///
    /// This is the map-revealing control. Sections come from wherever players have
    /// collectively been, so without a cap a new player could pull a survey of the whole
    /// explored world — coastlines, structures, other people's bases — without travelling.
    /// 8192 still gives an enormous horizon while keeping that a local advantage.
    /// </summary>
    public int ServeRadiusBlocks = 8192;

    /// <summary>Sections served per player per second. See LodAssist for the reasoning.</summary>
    public int MaxSectionsPerSecondPerPlayer = LodAssist.MaxSectionsPerSecondPerPlayer;

    /// <summary>
    /// Sections served per second across all players. The cap that bounds what the server
    /// pays: every section served is a main-thread blob read.
    /// </summary>
    public int MaxSectionsPerSecondTotal = LodAssist.MaxSectionsPerSecondTotal;

    /// <summary>Clamp to values that cannot wedge the server, whatever the file says.</summary>
    public void Sanitize()
    {
        if (ServeRadiusBlocks < 0) ServeRadiusBlocks = 0;
        MaxSectionsPerSecondPerPlayer = Math.Clamp(MaxSectionsPerSecondPerPlayer, 1, 256);
        MaxSectionsPerSecondTotal = Math.Clamp(MaxSectionsPerSecondTotal, 1, 1024);
    }

    public string Describe() =>
        $"capture {(EnableCapture ? "on" : "off")}, serving {(EnableServing ? "on" : "off")}, "
        + $"radius {(ServeRadiusBlocks > 0 ? ServeRadiusBlocks + " blocks" : "unlimited")}, "
        + $"{MaxSectionsPerSecondPerPlayer}/s per player, {MaxSectionsPerSecondTotal}/s total";
}
