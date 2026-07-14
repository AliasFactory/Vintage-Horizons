using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// Entry point. Client-only: builds LODs purely from chunk data the client receives,
/// so it works on any server. See DESIGN.md at the repo root.
/// </summary>
public class VintageHorizonsModSystem : ModSystem
{
    const int ColumnsPerTick = 8;

    ICoreClientAPI capi = null!;
    LodGrid grid = null!;
    LodTerrainRenderer renderer = null!;
    long tickListenerId;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        grid = new LodGrid(capi);
        renderer = new LodTerrainRenderer(capi, grid);

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(OnGameTick, 50);

        RegisterCommands();

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        // Any reason warrants a resample: new terrain or changed terrain.
        grid.EnqueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    void OnGameTick(float dt)
    {
        grid.ProcessPending(ColumnsPerTick);
    }

    void OnLevelFinalize()
    {
        renderer.ApplyZFar();
        Mod.Logger.Notification("Level finalized. LOD capture active (far view distance: {0} blocks).",
            renderer.FarViewDistance);

        capi.Event.RegisterCallback(_ => Mod.Logger.Notification(
            "Stats after 30s: {0} regions, {1} meshes, {2} columns processed, {3} pending",
            grid.Regions.Count, renderer.MeshCount, grid.ColumnsProcessed, grid.PendingColumns), 30000);
    }

    void OnLeaveWorld()
    {
        grid.Clear();
        renderer.ClearMeshes();
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("vhinfo")
            .WithDescription("VintageHorizons status")
            .HandleWith(_ => TextCommandResult.Success(
                $"[VintageHorizons] regions: {grid.Regions.Count}, meshes: {renderer.MeshCount}, " +
                $"columns processed: {grid.ColumnsProcessed}, pending: {grid.PendingColumns}, " +
                $"dirty: {grid.DirtyRegions.Count}, far view distance: {renderer.FarViewDistance}"));

        capi.ChatCommands.Create("vhfar")
            .WithDescription("Set VintageHorizons far view distance in blocks")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                renderer.FarViewDistance = GameMath.Clamp((int)args[0], 1024, 65536);
                renderer.ApplyZFar();
                return TextCommandResult.Success($"[VintageHorizons] far view distance set to {renderer.FarViewDistance}");
            });
    }

    public override void Dispose()
    {
        if (capi != null)
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LeaveWorld -= OnLeaveWorld;
            capi.Event.UnregisterGameTickListener(tickListenerId);
            renderer?.Dispose();
        }
    }
}
