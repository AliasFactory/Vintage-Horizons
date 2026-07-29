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
    long tickListenerId;

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

        try
        {
            Config = api.LoadModConfig<LodServerConfig>(ConfigFile) ?? new LodServerConfig();
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Could not read {0}, using defaults: {1}", ConfigFile, e.Message);
            Config = new LodServerConfig();
        }
        Config.Sanitize();
        // Written back every start so a new option appears in the file rather than only in
        // the source, and so a sanitised value is visible as the one actually in force.
        api.StoreModConfig(Config, ConfigFile);

        if (!Config.EnableCapture)
        {
            Mod.Logger.Notification(
                "Server LOD capture disabled in {0}. Clients are unaffected and keep using "
                + "their own captures, exactly as on a server without this mod.", ConfigFile);
            return;
        }

        // Singleplayer (and LAN-hosted) worlds run client and integrated server in one
        // process. Ordinarily there is nothing for this side to do there: capture is driven
        // by chunks loading, and in one process the server loads exactly the chunks the
        // client is already shown, so running both sides doubles every capture and holds
        // two copies of the section pyramid for no gain. Observed live before this was
        // guarded: a manifest of 3851 keys the client already had, every one redundant.
        //
        // Sweeping is the exception, and the reason the guard is conditional rather than
        // absolute. A sweep deliberately loads columns the client will never be shown -
        // terrain generated in sessions before this mod was installed, or hundreds of
        // blocks from where the player is standing. That is the one thing this side can do
        // in singleplayer that the client cannot do for itself.
        //
        // The two caches stay separate regardless (the -server suffix below), so the
        // double-open that caused the original bug cannot recur.
        if (!api.Server.IsDedicated && !Config.SweepEnabled)
        {
            Mod.Logger.Notification(
                "Singleplayer or LAN-hosted world with sweeping off: skipping server LOD "
                + "capture. The client side already captures everything this process loads, "
                + "and running both would duplicate the cache, the work and the memory for "
                + "no gain. Set SweepSavegame to index terrain from earlier sessions.");
            return;
        }

        // A server has no texture atlas, so it cannot compute a palette colour at all
        // (Block.GetColorWithoutTint takes ICoreClientAPI). Sections are written
        // colour-unresolved and the receiving client fills colour in, which it can do
        // from the block code alone. Tint slots are likewise client-only and stay 0.
        pipeline = new LodPipeline(api, Mod.Logger, (_, _, _, _) => (0, 0));

        // Not at StartServerSide: the savegame identifier that names the cache file is
        // not known until the world is up.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, OnRunGame);
        api.Event.GameWorldSave += OnGameWorldSave;
    }

    void OnRunGame()
    {
        pipeline!.Open("ModData/vintagehorizons", "-server");

        sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        sapi.Event.DidBreakBlock += (_, _, blockSel) => QueueAt(blockSel?.Position);
        sapi.Event.DidPlaceBlock += (_, _, blockSel, _) => QueueAt(blockSel?.Position);

        tickListenerId = sapi.Event.RegisterGameTickListener(_ => pipeline!.Tick(), 50);

        Mod.Logger.Notification("Server LOD capture active ({0} sections from cache). {1}",
            pipeline.CachedSectionsLoaded, Config.Describe());

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
