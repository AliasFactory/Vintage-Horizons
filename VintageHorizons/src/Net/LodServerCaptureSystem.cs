using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the LOD cache of the server (DESIGN.md section 10, stage 2). It runs the same
/// <see cref="LodPipeline"/> that the client runs.
///
/// The chunk columns that the server loads or generates drive it. The columns that one
/// player receives do not. Thus the cache collects the terrain from the travels of all
/// players, and not from the travels of one player.
///
/// This class is for a dedicated server only. In singleplayer, the client side of this same
/// process captures each chunk that loads. Thus a second pipeline duplicates the cache file,
/// the work and the memory, for no gain. Read StartServerSide.
///
/// This class is deliberately separate from <see cref="LodAssistServerSystem"/>. The
/// handshake must continue to answer, and to answer correctly, even when the capture is off
/// or skipped.
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

    /// <summary>The mod sets this after it opens the cache. The assist handshake reports
    /// it.</summary>
    public bool Capturing => pipeline?.Active == true;

    /// <summary>
    /// The keys that the server can offer.
    ///
    /// This uses HasDataSet, and not Sections. A section that the mod evicted from RAM is
    /// still on the disk, and the server can still give it. The count that a client receives
    /// must also match the content of the manifest.
    /// </summary>
    public int SectionCount => pipeline?.World.HasDataSet.Count ?? 0;

    public int ColumnsCaptured => pipeline?.ColumnsCaptured ?? 0;

    /// <summary>A progress line for /vhserver. This is null when no pre-generation
    /// runs.</summary>
    public string? PregenStatus => pregen == null ? null
        : pregen.Done ? $"pre-generation complete ({pregen.Total} columns)"
        : $"pre-generating {pregen.Requested}/{pregen.Total} columns";

    /// <summary>A progress line for /vhserver. This is null when no sweep runs.</summary>
    public string? SweepStatus => sweep?.Status;

    /// <summary>Use this on the main thread only. The capture pipeline changes this set in
    /// each tick.</summary>
    public long[] SnapshotKeys() =>
        pipeline == null ? Array.Empty<long>() : pipeline.World.HasDataSet.ToArray();

    /// <summary>
    /// The stored blob for a key, to give over the network.
    ///
    /// Use this on the main thread only. It shares the store connection with the capture that
    /// writes to it.
    /// </summary>
    public byte[]? LoadBlob(long key) => pipeline?.LoadBlob(key);

    /// <summary>The settings of the admin, which the mod loads one time. Both server systems
    /// read this copy.</summary>
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
        // The mod writes the file again at each start. Thus a new option appears in the
        // file, and not in the source only. A value that the mod clamped is also visible as
        // the value in force.
        api.StoreModConfig(Config, ConfigFile);

        if (!Config.EnableCapture)
        {
            Mod.Logger.Notification(
                "Server LOD capture disabled in {0}. This does not affect any client. Each "
                + "client continues to use its own captures, exactly as on a server without "
                + "this mod.", ConfigFile);
            return;
        }

        // A singleplayer world, and a world that a player hosts on a LAN, run the client
        // and the integrated server in one process. Normally this side has nothing to do
        // there.
        //
        // The chunks that load drive the capture. In one process, the server loads exactly
        // the chunks that the client shows already. Thus two sides double each capture, and
        // they hold two copies of the section pyramid, for no gain.
        //
        // A live session showed this before the guard existed. A manifest held 3851 keys that
        // the client held already, and each one was redundant.
        //
        // A sweep is the exception, and it is the reason why the guard has a condition. A
        // sweep deliberately loads columns that the client never shows. Those columns are
        // terrain that a session generated before the installation of this mod, or terrain
        // hundreds of blocks from the player. That is the one thing that this side can do in
        // singleplayer, and the client cannot do it for itself.
        //
        // The two caches stay separate in each case, through the -server suffix below. Thus
        // the double open that caused the original defect cannot occur again.
        if (!api.Server.IsDedicated && !Config.SweepEnabled)
        {
            Mod.Logger.Notification(
                "This is a singleplayer world, or a world hosted on a LAN, and sweeping is "
                + "off. Thus the server LOD capture does not start. The client side already "
                + "captures each chunk that this process loads. Two sides would duplicate the "
                + "cache, the work and the memory, for no gain. To index the terrain from "
                + "earlier sessions, set SweepSavegame to true.");
            return;
        }

        // A server has no texture atlas. Thus it cannot calculate a palette color at all,
        // because Block.GetColorWithoutTint takes an ICoreClientAPI. The mod writes a
        // section with no color. The client that receives it adds the color, which it can do
        // from the block code alone. A tint slot is also for the client only, and it stays
        // at 0.
        pipeline = new LodPipeline(api, Mod.Logger, (_, _, _, _) => (0, 0));

        // Do not do this at StartServerSide. The savegame identifier gives the name of the
        // cache file, and it is not known until the world starts.
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

        // Sweep before pre-generation. The lower cost is not the only reason. A sweep loads
        // terrain that exists already, and pre-generation makes more terrain. The free work
        // comes first. Thus a startup that stops early has still indexed each real column,
        // before it spent one second to create new terrain.
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
    /// A player edit. The mod must capture the column again, or the cache continues to show
    /// terrain that is gone. ChunkColumnLoaded does not occur again for a live column.
    /// </summary>
    void QueueAt(BlockPos? pos)
    {
        if (pos == null) return;
        pipeline!.QueueColumn(pos.X / GlobalConstants.ChunkSize, pos.Z / GlobalConstants.ChunkSize);
    }

    /// <summary>
    /// Write the data with the world save, and not at the shutdown only. A server can stop
    /// without a clean shutdown. Then it loses each change since its start.
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
