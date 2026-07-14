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
    ICoreClientAPI capi = null!;
    int chunksSeen;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        Mod.Logger.Notification("VintageHorizons {0} loaded (client-only)", Mod.Info.Version);
    }

    void OnLevelFinalize()
    {
        Mod.Logger.Notification("Level finalized. World seed: {0}, map size: {1}. LOD capture active.",
            capi.World.Seed, capi.World.BlockAccessor.MapSize);
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        // M1 will snapshot the chunk here and hand it to the ingest queue.
        chunksSeen++;
        if (chunksSeen % 512 == 0)
        {
            Mod.Logger.VerboseDebug("ChunkDirty events so far: {0} (last: {1} at {2})",
                chunksSeen, reason, chunkCoord);
        }
    }

    void OnLeaveWorld()
    {
        chunksSeen = 0;
    }

    public override void Dispose()
    {
        if (capi != null)
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LeaveWorld -= OnLeaveWorld;
        }
    }
}
