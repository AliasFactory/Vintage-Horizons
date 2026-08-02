using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the server's own LOD cache (DESIGN.md §10, stage 2). Runs the same
/// <see cref="LodPipeline"/> the client does, driven by chunk columns the server loads or
/// generates rather than by ones a player receives, so the cache accumulates terrain from
/// everybody's travels instead of one player's.
///
/// Dedicated servers only. In singleplayer the client side of this same process already
/// captures every chunk that loads, so a second pipeline would duplicate the cache file,
/// the work and the memory for nothing - see StartServerSide.
///
/// Deliberately not merged into <see cref="LodAssistServerSystem"/>: the handshake has to
/// keep answering, and answer honestly, even when capture is off or skipped.
/// </summary>
public class LodServerCaptureSystem : ModSystem
{
    const string ConfigFile = "vintagehorizons-server.json";

    ICoreServerAPI sapi = null!;
    LodPipeline? pipeline;
    LodServerPregen? pregen;
    LodSavegameSweep? sweep;
    LodPlayerPregen? generate;
    long tickListenerId;

    /// <summary>
    /// Whether chunk loads drive capture on this server. Dedicated: yes, every load.
    /// Singleplayer: only when a sweep needs it - the client side already captures
    /// every column this process shows the player, and capturing them again here
    /// doubles the work and the memory for no gain. Generation never needs this flag:
    /// it feeds the pipeline directly, so the guard has nothing to predict.
    /// </summary>
    bool captureDrivenByLoads;

    /// <summary>Set at RunGame. /vhgen cannot open a cache before the world has a name.</summary>
    bool worldReady;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Set once the cache is open; the assist handshake reports it.</summary>
    public bool Capturing => pipeline?.Active == true;

    /// <summary>
    /// Keys the server can offer. HasDataSet, not Sections: a section evicted from RAM is
    /// still on disk and still servable, and the count a client is told has to match what
    /// the manifest will actually contain.
    /// </summary>
    public int SectionCount => pipeline?.World.HasDataSet.Count ?? 0;

    public int ColumnsCaptured => pipeline?.ColumnsCaptured ?? 0;

    /// <summary>Progress line for /vhserver, or null when no pre-generation is running.</summary>
    public string? PregenStatus => pregen == null ? null
        : pregen.Done ? $"pre-generation complete ({pregen.Total} columns)"
        : $"pre-generating {pregen.Requested}/{pregen.Total} columns";

    /// <summary>Progress line for /vhserver, or null when no sweep is running.</summary>
    public string? SweepStatus => sweep?.Status;

    /// <summary>Progress line for /vhserver, or null when /vhgen never ran.</summary>
    public string? GenerateStatus => generate?.Status;

    /// <summary>Main thread only - the capture pipeline mutates this set every tick.</summary>
    public long[] SnapshotKeys() =>
        pipeline == null ? Array.Empty<long>() : pipeline.World.HasDataSet.ToArray();

    /// <summary>
    /// The stored blob for a key, for serving over the network. Main thread only: it
    /// shares the store connection with the capture that writes it.
    /// </summary>
    public byte[]? LoadBlob(long key) => pipeline?.LoadBlob(key);

    /// <summary>Admin settings, loaded once; both server systems read this copy.</summary>
    public LodServerConfig Config { get; private set; } = new();

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        bool configReadable = true;
        try
        {
            Config = api.LoadModConfig<LodServerConfig>(ConfigFile) ?? new LodServerConfig();
        }
        catch (Exception e)
        {
            // Defaults in memory, and the file stays exactly as the admin wrote it.
            // Writing defaults back here would destroy every hand-edited setting over
            // one bad comma.
            configReadable = false;
            Mod.Logger.Warning(
                "Could not parse {0}: {1}. Using defaults for this session. The file was "
                + "left untouched so you can fix it.", ConfigFile, e.Message);
            Config = new LodServerConfig();
        }
        Config.Sanitize();
        // Written back after every clean load so a new option appears in the file rather
        // than only in the source, and so a sanitised value is visible as the one in force.
        if (configReadable) api.StoreModConfig(Config, ConfigFile);

        // Registered whatever the config says: a command that refuses with the reason
        // beats "no such command", which reads as a broken mod.
        RegisterGenerateCommand();

        if (!Config.EnableCapture)
        {
            Mod.Logger.Notification(
                "Server LOD capture disabled in {0}. Clients are unaffected and keep using "
                + "their own captures, exactly as on a server without this mod.", ConfigFile);
            return;
        }

        // Singleplayer (and LAN-hosted) worlds run client and integrated server in one
        // process. Capture from chunk loads has nothing to do there: the server loads
        // exactly the chunks the client is shown, and the client side already captures
        // those. Observed live before this was guarded: a manifest of 3851 keys the
        // client already had, every one redundant. A sweep is the exception - it loads
        // columns the client is never shown, and this event is the only way to see them.
        //
        // Generation is deliberately NOT an exception here. It hands peeked columns to
        // the pipeline itself and routes its loads through an OnLoaded callback, so it
        // needs the cache open but not this event - which is what lets /vhgen work in
        // singleplayer hours after startup without re-running this decision.
        captureDrivenByLoads = api.Server.IsDedicated || Config.SweepEnabled;

        // Not at StartServerSide: the savegame identifier that names the cache file is
        // not known until the world is up.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, OnRunGame);
        api.Event.GameWorldSave += OnGameWorldSave;
    }

    void OnRunGame()
    {
        worldReady = true;

        if (!captureDrivenByLoads)
        {
            Mod.Logger.Notification(
                "Singleplayer or LAN-hosted world with sweeping off: the server side stays "
                + "idle. The client side already captures everything this process loads. "
                + "Set SweepSavegame to index terrain from earlier sessions; /vhgen opens a "
                + "server-side cache on demand for terrain the client cannot reach itself.");
            return;
        }

        OpenPipeline();

        sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        sapi.Event.DidBreakBlock += (_, _, blockSel) => QueueAt(blockSel?.Position);
        sapi.Event.DidPlaceBlock += (_, _, blockSel, _) => QueueAt(blockSel?.Position);

        // Sweep before pregen, and not only because it is cheaper. Sweeping loads terrain
        // that already exists; pregen makes more. Doing the free work first means an
        // interrupted startup has still indexed everything real before spending a second on
        // inventing anything.
        if (Config.SweepEnabled)
        {
            sweep = new LodSavegameSweep(sapi, Mod.Logger,
                Config.SweepRadiusChunks, Config.SweepColumnsPerSecond);
            sweep.Start();
        }

        if (Config.PregenRadiusChunks > 0)
        {
            pregen = new LodServerPregen(sapi, Mod.Logger,
                Config.PregenRadiusChunks, Config.PregenColumnsPerSecond);
            pregen.Start();
        }
    }

    /// <summary>
    /// Open the cache and start ticking the pipeline. Idempotent; main thread only.
    ///
    /// Split out of OnRunGame because /vhgen can need it at any moment - hours into a
    /// singleplayer session where this side never opened anything - and because opening
    /// a second SQLite file in every singleplayer world, in case someone might type a
    /// command one day, is exactly the cost the startup guard exists to avoid.
    /// </summary>
    LodPipeline OpenPipeline()
    {
        if (pipeline != null) return pipeline;

        // A server has no texture atlas, so it cannot compute a palette colour at all
        // (Block.GetColorWithoutTint takes ICoreClientAPI). Sections are written
        // colour-unresolved and the receiving client fills colour in, which it can do
        // from the block code alone. Tint slots are likewise client-only and stay 0.
        pipeline = new LodPipeline(sapi, Mod.Logger, (_, _, _, _) => (0, 0));
        pipeline.Open("ModData/vintagehorizons", "-server");

        tickListenerId = sapi.Event.RegisterGameTickListener(_ => pipeline!.Tick(), 50);

        Mod.Logger.Notification("Server LOD capture active ({0} sections from cache). {1}",
            pipeline.CachedSectionsLoaded, Config.Describe());
        return pipeline;
    }

    // ---- /vhgen: build the cache around a player, generating what nobody visited ----

    void RegisterGenerateCommand()
    {
        var parsers = sapi.ChatCommands.Parsers;
        sapi.ChatCommands.Create("vhgen")
            .WithDescription(
                "Build the VintageHorizons LOD cache around you. Terrain nobody has visited "
                + "is generated from the world seed and captured, and nothing is written to "
                + "the savegame.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ => TextCommandResult.Success(GenerateStatusLine()))
            .BeginSubCommand("start")
                .WithDescription("Start a run centred on you, or on x z when given")
                .WithArgs(
                    parsers.OptionalIntRange("radius",
                        1, Math.Max(1, Config.GenerateMaxRadiusChunks), Config.GenerateDefaultRadiusChunks),
                    parsers.OptionalInt("x", -1),
                    parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateStart)
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Cancel the run in progress")
                .HandleWith(OnGenerateStop)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Progress of the current or last run")
                .HandleWith(_ => TextCommandResult.Success(GenerateStatusLine()))
            .EndSubCommand()
            .BeginSubCommand("diff")
                .WithDescription("Measure what a peek produces against a full generation")
                .WithArgs(parsers.OptionalInt("x", -1), parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateDiff)
            .EndSubCommand();

        // Places a block, so it exists only where someone asked for developer tools.
        // DESIGN.md 14 states the mod writes nothing to the world; that stays literally
        // true on every server that does not set this.
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEVTOOLS") != "1") return;

        sapi.ChatCommands.Get("vhgen")
            .BeginSubCommand("edittest")
                .WithDescription("Dev tool: place a marker, then show a peek cannot see it")
                .WithArgs(parsers.OptionalInt("x", -1), parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateEditTest)
            .EndSubCommand();

        Mod.Logger.Notification(
            "Developer tools on (VINTAGEHORIZONS_DEVTOOLS): /vhgen edittest is registered and "
            + "PLACES BLOCKS in the world when run. Do not set this on a server you care about.");
    }

    /// <summary>Centre for a diagnostic run: explicit x z, else the caller, else spawn.</summary>
    (int Cx, int Cz) DiagnosticCentre(TextCommandCallingArgs args)
    {
        int argX = (int)args.Parsers[0].GetValue();
        int argZ = (int)args.Parsers[1].GetValue();
        bool aimed = argX >= 0 && argZ >= 0;
        var pos = args.Caller.Pos;
        double bx = aimed ? argX : pos?.X ?? sapi.World.DefaultSpawnPosition.X;
        double bz = aimed ? argZ : pos?.Z ?? sapi.World.DefaultSpawnPosition.Z;
        return ((int)bx / GlobalConstants.ChunkSize, (int)bz / GlobalConstants.ChunkSize);
    }

    TextCommandResult OnGenerateDiff(TextCommandCallingArgs args)
    {
        if (!worldReady) return TextCommandResult.Error("[VintageHorizons] The world is still starting.");

        (int cx, int cz) = DiagnosticCentre(args);
        new LodPeekDiff(sapi, Mod.Logger).RunDiff(cx, cz,
            line => Mod.Logger.Notification("Peek diff result: {0}", line));

        return TextCommandResult.Success(
            $"[VintageHorizons] Comparing a peek against a full generation at chunk {cx},{cz}. "
            + "This GENERATES terrain for the reference, so do not run it where that matters. "
            + "The report lands in the server log shortly.");
    }

    TextCommandResult OnGenerateEditTest(TextCommandCallingArgs args)
    {
        if (!worldReady) return TextCommandResult.Error("[VintageHorizons] The world is still starting.");

        (int cx, int cz) = DiagnosticCentre(args);
        new LodPeekDiff(sapi, Mod.Logger).RunEditTest(cx, cz,
            line => Mod.Logger.Notification("Edit test result: {0}", line));

        return TextCommandResult.Success(
            $"[VintageHorizons] Placing a marker at chunk {cx},{cz} and peeking the same "
            + "coordinate. This WRITES BLOCKS to your world. The report lands in the server log.");
    }

    string GenerateStatusLine() => generate != null
        ? "[VintageHorizons] " + generate.Status
        : "[VintageHorizons] No generation running. /vhgen start [radius] - default "
          + $"{Config.GenerateDefaultRadiusChunks} chunks, max {Config.GenerateMaxRadiusChunks}.";

    TextCommandResult OnGenerateStart(TextCommandCallingArgs args)
    {
        if (!Config.EnableCapture)
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Server LOD capture is switched off (EnableCapture in "
                + $"ModConfig/{ConfigFile}), so there is nothing to generate into.");
        }
        if (!Config.GenerateEnabled)
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Generation is switched off on this server "
                + $"(EnableGenerateCommand or GenerateMaxRadiusChunks in ModConfig/{ConfigFile}).");
        }
        if (!worldReady)
        {
            return TextCommandResult.Error("[VintageHorizons] The world is still starting; try again shortly.");
        }
        if (generate is { Done: false })
        {
            return TextCommandResult.Error(
                $"[VintageHorizons] A run started by {generate.StartedBy} is already going: "
                + generate.Status + ". Use /vhgen stop first.");
        }

        int radius = (int)args.Parsers[0].GetValue();
        int argX = (int)args.Parsers[1].GetValue();
        int argZ = (int)args.Parsers[2].GetValue();

        // Centre precedence: explicit x z beats the caller's position beats spawn.
        // World coordinates are never negative, so -1 means "not given". The console
        // has no position, so spawn is its only honest fallback, and the reply says
        // which centre was used.
        var pos = args.Caller.Pos;
        bool aimed = argX >= 0 && argZ >= 0;
        bool fromSpawn = !aimed && pos == null;
        double bx = aimed ? argX : pos?.X ?? sapi.World.DefaultSpawnPosition.X;
        double bz = aimed ? argZ : pos?.Z ?? sapi.World.DefaultSpawnPosition.Z;
        int cs = GlobalConstants.ChunkSize;
        int centreCx = (int)bx / cs;
        int centreCz = (int)bz / cs;
        string startedBy = args.Caller.Player?.PlayerName ?? "the console";

        generate = new LodPlayerPregen(sapi, Mod.Logger, OpenPipeline(),
            centreCx, centreCz, radius, Config, startedBy);
        generate.Start();

        int columns = LodPlayerPregen.ColumnsFor(radius);
        int seconds = LodPlayerPregen.EstimateSeconds(columns, Config.GenerateColumnsPerSecond);
        string time = seconds >= 120 ? seconds / 60 + " minutes" : seconds + " seconds";
        return TextCommandResult.Success(
            $"[VintageHorizons] Generating LOD around {(fromSpawn ? "spawn " : "")}{(int)bx},{(int)bz} "
            + $"out to {radius} chunks ({radius * cs} blocks): {columns} columns, roughly {time} if "
            + "the server keeps up. Terrain nobody has visited is generated from the seed and "
            + "thrown away - nothing is written to the savegame, and no chunk is loaded for any "
            + "player. /vhgen status shows progress; /vhgen stop cancels.");
    }

    TextCommandResult OnGenerateStop(TextCommandCallingArgs args)
    {
        if (generate is not { Done: false })
        {
            return TextCommandResult.Success("[VintageHorizons] No generation is running.");
        }
        generate.Cancel();
        return TextCommandResult.Success(
            "[VintageHorizons] Generation stopping. Peeks already issued still get captured - "
            + "that worldgen time is spent either way. The finish line in the server log "
            + "reports what was done and verifies the savegame gained nothing.");
    }

    void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        pipeline!.QueueColumn(chunkCoord.X, chunkCoord.Y); // Vec2i.Y is the Z chunk coord
    }

    /// <summary>
    /// Player edits: the column has to be re-captured or the cache keeps showing terrain
    /// that is no longer there. ChunkColumnLoaded does not fire again for a live column.
    /// </summary>
    void QueueAt(BlockPos? pos)
    {
        if (pos == null) return;
        pipeline!.QueueColumn(pos.X / GlobalConstants.ChunkSize, pos.Z / GlobalConstants.ChunkSize);
    }

    /// <summary>
    /// Flush with the world save rather than only at shutdown: a server that is killed
    /// rather than stopped cleanly would otherwise lose everything since it started.
    /// </summary>
    void OnGameWorldSave()
    {
        if (pipeline?.Active == true) pipeline.Tick();
    }

    public override void Dispose()
    {
        // Before the pipeline closes, or a peek landing during shutdown would enqueue a
        // capture into a closed pipeline. CaptureColumn also refuses once Active drops,
        // as the second layer of the same guard.
        generate?.Cancel();

        if (sapi != null)
        {
            sapi.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
            sapi.Event.UnregisterGameTickListener(tickListenerId);
        }
        pipeline?.Close();
        pipeline?.Dispose();
        pipeline = null;
    }
}
