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
    /// serves nothing, whatever the other settings say - clients still work, using their
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
    /// explored world - coastlines, structures, other people's bases - without travelling.
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

    /// <summary>
    /// Build the cache from terrain the world already has, by loading every chunk column
    /// around spawn that was generated in some earlier session.
    ///
    /// On by default, which <see cref="PregenRadiusChunks"/> deliberately is not, because
    /// the two do different things. Sweeping generates nothing - it indexes terrain that
    /// already exists, costs no worldgen, adds no disk beyond the LOD cache itself, and
    /// reveals nowhere a player has not already been. A savegame accumulates terrain for
    /// as long as anyone plays, while the LOD cache only ever saw the fraction that
    /// streamed past someone running this mod.
    /// </summary>
    public bool SweepSavegame = true;

    /// <summary>
    /// How far out to look, in chunks. Every position inside it is examined; positions with
    /// no generated terrain are skipped, so a small world costs little regardless of this.
    /// 0 disables the sweep as surely as SweepSavegame false does.
    /// </summary>
    public int SweepRadiusChunks = 128;

    /// <summary>
    /// Generated columns loaded per second. Higher than the pre-generation rate because the
    /// work is a deserialize rather than a worldgen pass, and the sweep is competing with
    /// nothing on a server that is otherwise idle at startup.
    /// </summary>
    public int SweepColumnsPerSecond = 16;

    /// <summary>
    /// Let an admin build the LOD cache around themselves with /vhgen. Columns nobody
    /// has generated are peeked: real worldgen runs from the seed, capture reads the
    /// result, and nothing is written to the savegame. Columns that exist are loaded
    /// under the same neighbourhood rule the sweep uses, so player edits stay correct.
    ///
    /// On by default, unlike <see cref="PregenRadiusChunks"/>, and the difference is
    /// the same one that puts sweeping on by default: this runs only when a person
    /// with the controlserver privilege asks, and the reply states the cost first.
    /// Off means the command still exists but refuses, with the reason.
    /// </summary>
    public bool EnableGenerateCommand = true;

    /// <summary>
    /// The largest radius, in chunks, that /vhgen accepts. 0 disables the command as
    /// surely as the flag does. 128 is a 4096-block radius: 66,049 columns, roughly 48
    /// minutes at the engine's measured ~23 columns per second of worldgen.
    /// </summary>
    public int GenerateMaxRadiusChunks = 128;

    /// <summary>
    /// The radius used when /vhgen start gets no argument. Small on purpose: a person
    /// typing a command wants a result. 32 chunks is 4,225 columns, roughly 3 minutes.
    /// </summary>
    public int GenerateDefaultRadiusChunks = 32;

    /// <summary>
    /// Columns started per second. The engine saturates near 23 per second whatever
    /// this says. A higher value only means the in-flight cap binds first. A lower one
    /// is a real throttle for a server with players on it.
    /// </summary>
    public int GenerateColumnsPerSecond = 16;

    /// <summary>
    /// Peeks outstanding at once. A memory ceiling as much as a contention one: each
    /// peek that lands hands a whole chunk column to the capture thread, which unpacks
    /// it to read it - 1-2 MB per column, held until capture drains it. 64 is
    /// TopoHorizon's measured value; unbounded reached ~520 in flight and every peek
    /// slowed under the contention.
    /// </summary>
    public int GenerateMaxInFlight = 64;

    /// <summary>
    /// Pre-build the cache by generating a square of chunk columns around spawn, in chunks
    /// of radius. 0 (default) means never; the cache then fills only as players travel.
    ///
    /// This is the one setting that makes the mod generate terrain nobody has visited, so
    /// it is off unless an admin asks for it. Worth asking for: it is the difference
    /// between a horizon on the first join and one that appears over weeks of play. Cost is
    /// worldgen time and disk - at the measured mean 45.9 KB a section, radius 64 (a 4096
    /// block square) is on the order of a few hundred MB.
    /// </summary>
    public int PregenRadiusChunks;

    /// <summary>Chunk columns requested per second while pre-generating. Keep it modest.</summary>
    public int PregenColumnsPerSecond = 8;

    /// <summary>Clamp to values that cannot wedge the server, whatever the file says.</summary>
    public void Sanitize()
    {
        if (ServeRadiusBlocks < 0) ServeRadiusBlocks = 0;
        // 256 chunks is a 4096-block radius. Past that the disk cost stops being something
        // an admin can absorb by accident.
        PregenRadiusChunks = Math.Clamp(PregenRadiusChunks, 0, 256);
        PregenColumnsPerSecond = Math.Clamp(PregenColumnsPerSecond, 1, 64);
        // A wider ceiling than pregen's, because the cost scales with terrain that exists
        // rather than with the radius: examining a position that was never generated is an
        // index lookup, so a large radius over a small world is nearly free.
        SweepRadiusChunks = Math.Clamp(SweepRadiusChunks, 0, 512);
        SweepColumnsPerSecond = Math.Clamp(SweepColumnsPerSecond, 1, 64);
        // Same ceiling as pregen: past 256 chunks the time cost stops being something an
        // admin grasps from a chat reply. The default clamps against the ceiling, not
        // against 256 - a lowered ceiling must not leave a default the command refuses.
        GenerateMaxRadiusChunks = Math.Clamp(GenerateMaxRadiusChunks, 0, 256);
        GenerateDefaultRadiusChunks = GenerateMaxRadiusChunks == 0
            ? 0 : Math.Clamp(GenerateDefaultRadiusChunks, 1, GenerateMaxRadiusChunks);
        GenerateColumnsPerSecond = Math.Clamp(GenerateColumnsPerSecond, 1, 64);
        GenerateMaxInFlight = Math.Clamp(GenerateMaxInFlight, 1, 256);
        // Ceilings derived from measurement, not taste: a served section costs ~0.9ms of
        // main-thread SQLite blob read (415 sections, 348ms, on a warm cache). So 128/s is
        // ~115ms per second, around 11% of a core, which is the most an admin should be
        // able to hand to this by editing a file. The original 1024 would have been ~920ms
        // per second - a server wedged by its own config.
        MaxSectionsPerSecondPerPlayer = Math.Clamp(MaxSectionsPerSecondPerPlayer, 1, 64);
        MaxSectionsPerSecondTotal = Math.Clamp(MaxSectionsPerSecondTotal, 1, 128);
    }

    /// <summary>True when the sweep is configured to actually do something.</summary>
    public bool SweepEnabled => SweepSavegame && SweepRadiusChunks > 0;

    /// <summary>True when /vhgen is configured to accept a start.</summary>
    public bool GenerateEnabled => EnableGenerateCommand && GenerateMaxRadiusChunks > 0;

    public string Describe() =>
        $"capture {(EnableCapture ? "on" : "off")}, serving {(EnableServing ? "on" : "off")}, "
        + $"radius {(ServeRadiusBlocks > 0 ? ServeRadiusBlocks + " blocks" : "unlimited")}, "
        + $"{MaxSectionsPerSecondPerPlayer}/s per player, {MaxSectionsPerSecondTotal}/s total, "
        + $"sweep {(SweepEnabled ? SweepRadiusChunks + " chunks" : "off")}, "
        + $"generate {(GenerateEnabled ? "on request up to " + GenerateMaxRadiusChunks + " chunks" : "off")}";
}
