using System.Text.RegularExpressions;
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
    const int PropagationsPerTick = 4;
    const int RegionSavesPerTick = 2;

    ICoreClientAPI capi = null!;
    LodGrid grid = null!;
    LodTerrainRenderer renderer = null!;
    LodStore? store;
    long tickListenerId;
    int cachedRegionsLoaded;

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
        grid.ProcessPropagation(PropagationsPerTick);
        SaveSomeDirtyRegions(RegionSavesPerTick);
    }

    void SaveSomeDirtyRegions(int budget)
    {
        if (store == null || grid.SaveDirtyRegions.Count == 0) return;

        List<long>? saved = null;
        foreach (long rkey in grid.SaveDirtyRegions)
        {
            if (grid.Regions.TryGetValue(rkey, out LodRegion? region))
            {
                // Persisting the mip-dirty flag makes pyramid propagation crash-safe:
                // on load, flagged rows re-enter the propagation queue.
                store.SaveRegion(LodGrid.KeyLevel(rkey), LodGrid.KeyRx(rkey), LodGrid.KeyRz(rkey),
                    region, grid.MipDirty.Contains(rkey));
            }
            (saved ??= new List<long>()).Add(rkey);
            if (--budget <= 0) break;
        }
        if (saved != null) foreach (long rkey in saved) grid.SaveDirtyRegions.Remove(rkey);
    }

    void OnLevelFinalize()
    {
        renderer.ApplyZFar();
        OpenLodCache();

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} regions from cache).",
            cachedRegionsLoaded);

        capi.Event.RegisterCallback(_ => Mod.Logger.Notification(
            "Stats after 30s: {0} regions [{1}] ({2} from cache), {3} meshes, {4} drawn, {5} columns processed, {6} pending, {7} awaiting mip",
            grid.Regions.Count, grid.DescribeLevels(), cachedRegionsLoaded, renderer.MeshCount,
            renderer.LastDrawCount, grid.ColumnsProcessed, grid.PendingColumns, grid.MipDirty.Count), 30000);
    }

    void OpenLodCache()
    {
        string worldKey = capi.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + capi.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = capi.GetOrCreateDataPath("ModData/vintagehorizons");
        string dbPath = Path.Combine(dir, worldKey + ".db");

        var newStore = new LodStore(Mod.Logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            return; // no persistence this session; everything else still works
        }

        store = newStore;
        cachedRegionsLoaded = store.LoadAllRegions(grid.InstallLoadedRegion);
        Mod.Logger.Notification("LOD cache: {0}", dbPath);
    }

    void OnLeaveWorld()
    {
        if (store != null)
        {
            SaveSomeDirtyRegions(int.MaxValue);
            store.Close();
            store.Dispose();
            store = null;
        }
        cachedRegionsLoaded = 0;
        grid.Clear();
        renderer.ClearMeshes();
    }

    void RegisterCommands()
    {
        capi.ChatCommands.Create("vhinfo")
            .WithDescription("VintageHorizons status")
            .HandleWith(_ => TextCommandResult.Success(
                $"[VintageHorizons] regions: {grid.Regions.Count} [{grid.DescribeLevels()}] ({cachedRegionsLoaded} from cache), " +
                $"meshes: {renderer.MeshCount}, drawn: {renderer.LastDrawCount}, " +
                $"columns processed: {grid.ColumnsProcessed}, pending: {grid.PendingColumns}, " +
                $"awaiting mip: {grid.MipDirty.Count}, unsaved: {grid.SaveDirtyRegions.Count}, " +
                $"persistence: {(store != null ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}"));

        capi.ChatCommands.Create("vhfar")
            .WithDescription("Cap VintageHorizons render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[VintageHorizons] render distance capped at {renderer.FarViewDistanceCap}"
                    : "[VintageHorizons] render distance unlimited");
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
            store?.Dispose();
            renderer?.Dispose();
        }
    }
}
