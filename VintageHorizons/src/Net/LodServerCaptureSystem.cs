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
/// Nothing reads this yet — section transfer is a later stage. Landing it first means the
/// database exists before there is a protocol to serve it over, which is the order the
/// dependency actually runs in: a key manifest cannot list what has never been captured.
///
/// Deliberately not merged into <see cref="LodAssistServerSystem"/>: capture is useful on
/// its own (a singleplayer world builds one with no networking involved at all), and the
/// handshake has to keep answering even when capture is switched off.
/// </summary>
public class LodServerCaptureSystem : ModSystem
{
    ICoreServerAPI sapi = null!;
    LodPipeline? pipeline;
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

    /// <summary>Main thread only — the capture pipeline mutates this set every tick.</summary>
    public long[] SnapshotKeys() =>
        pipeline == null ? Array.Empty<long>() : pipeline.World.HasDataSet.ToArray();

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

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
        pipeline!.Open("ModData/vintagehorizons");

        sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        sapi.Event.DidBreakBlock += (_, _, blockSel) => QueueAt(blockSel?.Position);
        sapi.Event.DidPlaceBlock += (_, _, blockSel, _) => QueueAt(blockSel?.Position);

        tickListenerId = sapi.Event.RegisterGameTickListener(_ => pipeline!.Tick(), 50);

        Mod.Logger.Notification(
            "Server LOD capture active ({0} sections from cache). Nothing is served to clients yet.",
            pipeline.CachedSectionsLoaded);
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
