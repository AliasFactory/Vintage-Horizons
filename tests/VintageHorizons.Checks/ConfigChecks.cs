using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The settings for an admin on the server side.
///
/// Sanitize is the boundary between a config file that an admin wrote and the values that
/// reach the serve loop. Its limits come from measurement, and not from opinion.
///
/// One section that the server gives costs approximately 0.9 ms of blob read on the main
/// thread. Thus the total limit decides how much of one core an admin can give to this, with
/// an edit of a file.
/// </summary>
public static class ConfigChecks
{
    public static void Run(Check c)
    {
        Defaults(c);
        Clamps(c);
        Description(c);
    }

    static void Defaults(Check c)
    {
        var config = new LodServerConfig();

        // The installation of the mod on a server IS the decision to opt in. A mod that does
        // nothing until a person edits a file appears to be broken.
        c.True(config.EnableCapture, "capture is on by default");
        c.True(config.EnableServing, "serving is on by default");

        // But the radius has a limit on purpose, and it is not unlimited. Sections come from
        // wherever the players went together. Thus a default with no limit lets a new player
        // take a survey of the full explored world, with no travel.
        c.Eq(8192, config.ServeRadiusBlocks, "the default radius is bounded, not unlimited");

        // An admin must ask for the generation of terrain that nobody visited.
        c.Eq(0, config.PregenRadiusChunks, "pre-generation is off by default");

        // A sweep is on by default, and that difference is the point. A sweep loads terrain
        // that exists already. Thus it costs no worldgen time, and it reveals no place where
        // a player did not go.
        c.True(config.SweepSavegame, "savegame sweeping is on by default");
        c.True(config.SweepRadiusChunks > 0, "the default sweep radius actually sweeps something");
        c.True(config.SweepEnabled, "the defaults leave sweeping enabled");
        c.False(new LodServerConfig { SweepSavegame = false }.SweepEnabled,
            "clearing the flag disables sweeping");
        c.False(new LodServerConfig { SweepRadiusChunks = 0 }.SweepEnabled,
            "a zero radius disables sweeping as surely as the flag does");

        c.Eq(LodAssist.MaxSectionsPerSecondPerPlayer, config.MaxSectionsPerSecondPerPlayer,
            "the per-player default tracks the protocol constant");
        c.Eq(LodAssist.MaxSectionsPerSecondTotal, config.MaxSectionsPerSecondTotal,
            "the total default tracks the protocol constant");

        var untouched = new LodServerConfig();
        untouched.Sanitize();
        c.Eq(8192, untouched.ServeRadiusBlocks, "sanitizing the defaults changes nothing");
        c.Eq(LodAssist.MaxSectionsPerSecondTotal, untouched.MaxSectionsPerSecondTotal,
            "sanitizing leaves in-range values alone");
    }

    static void Clamps(Check c)
    {
        // The limits from the measurement. 128 each second is approximately 115 ms each
        // second of blob reads, which is approximately 11% of one core. An earlier value of
        // 1024 is approximately 920 ms each second, which stops the server through its own
        // config file.
        c.Eq(128, Sanitized(cfg => cfg.MaxSectionsPerSecondTotal = 100000).MaxSectionsPerSecondTotal,
            "the total rate is capped at the measured ceiling");
        c.Eq(64, Sanitized(cfg => cfg.MaxSectionsPerSecondPerPlayer = 100000).MaxSectionsPerSecondPerPlayer,
            "the per-player rate is capped");

        // Zero stops the serve loop fully. It does not make the loop slower.
        c.Eq(1, Sanitized(cfg => cfg.MaxSectionsPerSecondTotal = 0).MaxSectionsPerSecondTotal,
            "a zero total rate becomes one, not a stall");
        c.Eq(1, Sanitized(cfg => cfg.MaxSectionsPerSecondPerPlayer = -5).MaxSectionsPerSecondPerPlayer,
            "a negative per-player rate becomes one");

        c.Eq(256, Sanitized(cfg => cfg.PregenRadiusChunks = 99999).PregenRadiusChunks,
            "pre-generation radius is capped at 256 chunks");
        c.Eq(0, Sanitized(cfg => cfg.PregenRadiusChunks = -1).PregenRadiusChunks,
            "a negative pre-generation radius means off");
        c.Eq(64, Sanitized(cfg => cfg.PregenColumnsPerSecond = 1000).PregenColumnsPerSecond,
            "pre-generation rate is capped");
        c.Eq(1, Sanitized(cfg => cfg.PregenColumnsPerSecond = 0).PregenColumnsPerSecond,
            "a zero pre-generation rate becomes one");

        // The sweep limit is wider than the pre-generation limit. The cost follows the
        // terrain that exists, and not the radius. An examination of a position that nothing
        // generated is an index lookup. Thus a large radius over a small world is almost
        // free.
        c.Eq(512, Sanitized(cfg => cfg.SweepRadiusChunks = 99999).SweepRadiusChunks,
            "sweep radius is capped at 512 chunks");
        c.Eq(0, Sanitized(cfg => cfg.SweepRadiusChunks = -1).SweepRadiusChunks,
            "a negative sweep radius means off, never negative");
        c.Eq(64, Sanitized(cfg => cfg.SweepColumnsPerSecond = 1000).SweepColumnsPerSecond,
            "sweep rate is capped");
        c.Eq(1, Sanitized(cfg => cfg.SweepColumnsPerSecond = 0).SweepColumnsPerSecond,
            "a zero sweep rate becomes one, not a stall");
        c.Eq(48, Sanitized(cfg => cfg.SweepRadiusChunks = 48).SweepRadiusChunks,
            "an in-range sweep radius is preserved exactly");

        // The invariant that matters downstream: WithinServeRadius squares this value and
        // compares it against a squared distance, so a negative would compare as positive
        // and quietly serve a radius the admin never asked for.
        c.True(Sanitized(cfg => cfg.ServeRadiusBlocks = -1).ServeRadiusBlocks >= 0,
            "the serve radius is never left negative");
        c.Eq(512, Sanitized(cfg => cfg.ServeRadiusBlocks = 512).ServeRadiusBlocks,
            "an in-range serve radius is preserved exactly");
        c.Eq(0, Sanitized(cfg => cfg.ServeRadiusBlocks = 0).ServeRadiusBlocks,
            "zero is preserved, and means unlimited");

        // Sanitize must be idempotent: it runs on load and the result is written back to
        // disk, so a second run on its own output has to be a no-op or the file drifts
        // every restart.
        var once = Sanitized(cfg => { cfg.ServeRadiusBlocks = -7; cfg.MaxSectionsPerSecondTotal = 9999; });
        var twice = Sanitized(cfg =>
        {
            cfg.ServeRadiusBlocks = once.ServeRadiusBlocks;
            cfg.MaxSectionsPerSecondTotal = once.MaxSectionsPerSecondTotal;
        });
        c.Eq(once.ServeRadiusBlocks, twice.ServeRadiusBlocks, "sanitize is idempotent for the radius");
        c.Eq(once.MaxSectionsPerSecondTotal, twice.MaxSectionsPerSecondTotal,
            "sanitize is idempotent for the rate");
    }

    /// <summary>Describe() is what /vhserver prints, so admins read these words to check their config took.</summary>
    static void Description(Check c)
    {
        string text = new LodServerConfig().Describe();
        c.True(text.Contains("capture on"), "the description reports capture state");
        c.True(text.Contains("serving on"), "the description reports serving state");
        c.True(text.Contains("8192 blocks"), "the description reports the radius in blocks");

        var unlimited = new LodServerConfig { ServeRadiusBlocks = 0 };
        c.True(unlimited.Describe().Contains("unlimited"), "a zero radius is described as unlimited");

        var off = new LodServerConfig { EnableCapture = false, EnableServing = false };
        c.True(off.Describe().Contains("capture off"), "capture off is described");
        c.True(off.Describe().Contains("serving off"), "serving off is described");

        c.True(text.Contains("sweep 128 chunks"), "the description reports the sweep radius");
        c.True(new LodServerConfig { SweepSavegame = false }.Describe().Contains("sweep off"),
            "a disabled sweep is described as off");
    }

    static LodServerConfig Sanitized(Action<LodServerConfig> setup)
    {
        var config = new LodServerConfig();
        setup(config);
        config.Sanitize();
        return config;
    }
}
