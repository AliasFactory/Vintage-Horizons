using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The server-side admin knobs. Sanitize is the boundary between a config file an admin
/// typed and the values that reach the serve loop, and its ceilings come from measurement
/// rather than taste - a served section costs about 0.9ms of main-thread blob read, so the
/// total cap is what decides how much of a core an admin can hand over by editing a file.
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

        // Installing the mod on a server IS the opt-in; a mod that silently does nothing
        // until a file is edited reads as broken.
        c.True(config.EnableCapture, "capture is on by default");
        c.True(config.EnableServing, "serving is on by default");

        // But the radius is deliberately bounded rather than unlimited: sections come from
        // wherever players have collectively been, so an uncapped default would let a new
        // player pull a survey of the whole explored world without travelling.
        c.Eq(8192, config.ServeRadiusBlocks, "the default radius is bounded, not unlimited");

        // Generating terrain nobody has visited is opt-in.
        c.Eq(0, config.PregenRadiusChunks, "pre-generation is off by default");

        // Sweeping is not, and the asymmetry is the point: it loads terrain that already
        // exists, so it costs no worldgen and reveals nowhere a player has not been.
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

        // Generation is on by default where pregen is not: it runs only when a person
        // with the controlserver privilege asks, and peeks write nothing to the savegame.
        c.True(config.EnableGenerateCommand, "the generate command is enabled by default");
        c.True(config.GenerateEnabled, "the defaults leave generation available");
        c.Eq(128, config.GenerateMaxRadiusChunks, "the generate ceiling is 128 chunks");
        c.Eq(32, config.GenerateDefaultRadiusChunks, "the no-argument radius is 32 chunks");
        c.Eq(16, config.GenerateColumnsPerSecond, "the generate rate default is 16 columns/s");
        c.Eq(64, config.GenerateMaxInFlight, "the in-flight cap default is TopoHorizon's measured 64");
        c.False(new LodServerConfig { EnableGenerateCommand = false }.GenerateEnabled,
            "clearing the flag disables generation");
        c.False(new LodServerConfig { GenerateMaxRadiusChunks = 0 }.GenerateEnabled,
            "a zero ceiling disables generation as surely as the flag does");

        var untouched = new LodServerConfig();
        untouched.Sanitize();
        c.Eq(8192, untouched.ServeRadiusBlocks, "sanitizing the defaults changes nothing");
        c.Eq(LodAssist.MaxSectionsPerSecondTotal, untouched.MaxSectionsPerSecondTotal,
            "sanitizing leaves in-range values alone");
    }

    static void Clamps(Check c)
    {
        // The measured ceilings. 128/s is roughly 115ms per second of blob reads, about 11%
        // of a core. An earlier 1024 would have been ~920ms per second: a server wedged by
        // its own config file.
        c.Eq(128, Sanitized(cfg => cfg.MaxSectionsPerSecondTotal = 100000).MaxSectionsPerSecondTotal,
            "the total rate is capped at the measured ceiling");
        c.Eq(64, Sanitized(cfg => cfg.MaxSectionsPerSecondPerPlayer = 100000).MaxSectionsPerSecondPerPlayer,
            "the per-player rate is capped");

        // Zero would stall the serve loop outright rather than slow it.
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

        // A wider sweep ceiling than pregen's, because the cost tracks terrain that exists
        // rather than the radius: examining a position that was never generated is an index
        // lookup, so a large radius over a small world is nearly free.
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

        c.Eq(256, Sanitized(cfg => cfg.GenerateMaxRadiusChunks = 99999).GenerateMaxRadiusChunks,
            "the generate ceiling is capped at 256 chunks");
        c.Eq(0, Sanitized(cfg => cfg.GenerateMaxRadiusChunks = -1).GenerateMaxRadiusChunks,
            "a negative generate ceiling means off");
        c.Eq(64, Sanitized(cfg => cfg.GenerateColumnsPerSecond = 1000).GenerateColumnsPerSecond,
            "generate rate is capped");
        c.Eq(1, Sanitized(cfg => cfg.GenerateColumnsPerSecond = 0).GenerateColumnsPerSecond,
            "a zero generate rate becomes one, not a stall");
        c.Eq(256, Sanitized(cfg => cfg.GenerateMaxInFlight = 99999).GenerateMaxInFlight,
            "the in-flight cap is bounded");
        c.Eq(1, Sanitized(cfg => cfg.GenerateMaxInFlight = 0).GenerateMaxInFlight,
            "a zero in-flight cap becomes one");

        // The cross-field clamp. A lowered ceiling must never leave a default the
        // command itself then refuses as out of range.
        c.Eq(8, Sanitized(cfg => cfg.GenerateMaxRadiusChunks = 8).GenerateDefaultRadiusChunks,
            "the default radius clamps down to a lowered ceiling");
        c.Eq(0, Sanitized(cfg => cfg.GenerateMaxRadiusChunks = 0).GenerateDefaultRadiusChunks,
            "a zero ceiling zeroes the default radius too");
        c.Eq(32, Sanitized(cfg => cfg.GenerateDefaultRadiusChunks = 32).GenerateDefaultRadiusChunks,
            "an in-range default radius is preserved exactly");

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

        c.True(text.Contains("generate on request up to 128 chunks"),
            "the description reports the generate ceiling");
        c.True(new LodServerConfig { EnableGenerateCommand = false }.Describe().Contains("generate off"),
            "disabled generation is described as off");
    }

    static LodServerConfig Sanitized(Action<LodServerConfig> setup)
    {
        var config = new LodServerConfig();
        setup(config);
        config.Sanitize();
        return config;
    }
}
