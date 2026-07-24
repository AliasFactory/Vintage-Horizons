using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Renders the LodWorld section pyramid beyond the vanilla view distance. Meshes are
/// built off-thread by the LodWorker from section snapshots; this class schedules
/// mesh jobs (nearest-first), uploads finished vertex data on the render thread, and
/// walks the quadtree each frame picking detail by distance — a parent renders until
/// all four child slots are covered, so level swaps never open holes (DH's rule).
///
/// Rendering techniques (render order/stage, ZFar extension, camera-relative model
/// matrices, fog + transition handling in the shaders) adapted from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => 0.36; // just before opaque terrain → occluded by real chunks
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 4;
    const int MeshUploadsPerFrame = 4;
    const int MaxWorkerMeshBacklog = 12;

    readonly ICoreClientAPI capi;
    readonly LodWorld world;
    readonly LodWorker worker;
    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly HashSet<long> meshJobInFlight = new();
    readonly Dictionary<long, long> lastSelectedFrame = new();
    readonly List<long> evictBatch = new();
    long frameCounter;

    /// <summary>Meshes unselected for this many frames (~1 min) get evicted; the quadtree re-requests on demand.</summary>
    const int EvictAfterFrames = 3600;
    const int EvictSweepInterval = 300;

    public int EvictedTotal { get; private set; }
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();

    /// <summary>Dev/testing: keep the game unpaused even without window focus.</summary>
    public bool AutoUnpause;

    // Live seasonal state, refreshed periodically and fed to the shader as uniforms.
    Block? grassTintBlock;
    Block? foliageTintBlock;
    bool tintBlocksResolved;
    readonly Vec3f grassTint = new(1, 1, 1);
    readonly Vec3f foliageTint = new(1, 1, 1);
    float snowLineY = 99999;
    long lastSeasonRefreshFrame = -99999;
    readonly BlockPos climatePos = new(0, 0, 0);

    /// <summary>Optional hard cap in blocks; 0 = unlimited (render every cached section).</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;

    public int MeshCount => sectionMeshes.Count;
    public int LastDrawCount { get; private set; }

    /// <summary>Sections selected by the walk but skipped this frame as off-screen.</summary>
    public int LastCulledCount { get; private set; }

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;

    public string DescribeDrawnLevels()
    {
        var counts = new int[LodWorld.MaxLevel + 1];
        foreach (long key in drawList) counts[LodWorld.KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    public LodTerrainRenderer(ICoreClientAPI capi, LodWorld world, LodWorker worker)
    {
        this.capi = capi;
        this.world = world;
        this.worker = worker;

        capi.Event.ReloadShader += LoadShader;
        LoadShader();

        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "vintagehorizons-lod");
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();
        prog.AssetDomain = "vintagehorizons";
        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

        capi.Shader.RegisterFileShaderProgram("lodterrain", prog);

        shaderOk = prog.Compile();
        if (!shaderOk) capi.Logger.Error("[VintageHorizons] lodterrain shader failed to compile; LOD rendering disabled");
        return shaderOk;
    }

    public void ApplyZFar()
    {
        float needed = GameMath.Max(3000, EffectiveFarDistance + 512);
        var clientMain = (ClientMain)capi.World;

        if (clientMain.MainCamera.ZFar >= needed && appliedZFar == needed) return;

        clientMain.MainCamera.ZFar = needed;
        capi.Render.Reset3DProjection();
        appliedZFar = needed;
    }

    void UpdateEffectiveFarDistance(float vanillaViewDistance)
    {
        double maxDistSq = 0;
        foreach (long key in sectionMeshes.Keys)
        {
            int footprint = LodWorld.KeyFootprintBlocks(key);
            double dx = LodWorld.KeySx(key) * (double)footprint + footprint / 2.0 - camPos.X;
            double dz = LodWorld.KeySz(key) * (double)footprint + footprint / 2.0 - camPos.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > maxDistSq) maxDistSq = distSq;
        }

        float far = (float)Math.Sqrt(maxDistSq) + LodSection.SectionBlocks * 1.5f;
        if (FarViewDistanceCap > 0) far = Math.Min(far, FarViewDistanceCap);

        EffectiveFarDistance = GameMath.Max(far, vanillaViewDistance + 1536);
    }

    // ---- Detail selection (quadtree walk) ----

    double NearestDistanceTo(long key)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - camPos.X, camPos.X - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - camPos.Z, camPos.Z - (minZ + footprint)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    static int WantedLevel(double distance) => LodWorld.WantedLevelFor(distance);

    bool HasAnyMesh(long key) => sectionMeshes.ContainsKey(key) || waterMeshes.ContainsKey(key);

    bool AllChildrenCovered(long key)
    {
        bool covered = true;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodWorld.ChildKey(key, qx, qz);
                if (!world.HasDataSet.Contains(ck)) continue;

                if (HasAnyMesh(ck))
                {
                    // Gate meshes are load-bearing even when never drawn (the walk
                    // descends THROUGH them) — stamp so the evictor spares them.
                    lastSelectedFrame[ck] = frameCounter;
                }
                else
                {
                    // Missing gate: re-request (evicted, or never built) so descent
                    // can resume; the parent keeps covering meanwhile.
                    RequestMesh(ck);
                    covered = false;
                }
            }
        }
        return covered;
    }

    bool CollectDrawNodes(long key)
    {
        bool hasMesh = HasAnyMesh(key);
        int level = LodWorld.KeyLevel(key);
        int wanted = WantedLevel(NearestDistanceTo(key));

        // Demand-driven meshing: request ONLY at the level the walk actually wants
        // here. Descending through meshless parents must not request every leaf the
        // recursion happens to reach — coarser/finer nodes on the path stay unmeshed
        // until the wanted level for their own distance says otherwise.
        if (!hasMesh && level == wanted) RequestMesh(key);

        if (level > 0 && ((level > wanted && AllChildrenCovered(key)) || !hasMesh))
        {
            bool anyChildDrew = false;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    if (world.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                }
            }
            if (anyChildDrew || !hasMesh) return anyChildDrew;
        }

        if (hasMesh)
        {
            drawList.Add(key);
            lastSelectedFrame[key] = frameCounter;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Refresh the live seasonal tints and snow line (~every 4s). Tints come from
    /// applying the game's climate/season color maps to pure white at the player's
    /// position; the snow line extrapolates the local temperature lapse rate to the
    /// altitude where it hits freezing.
    /// </summary>
    void RefreshSeasonalState()
    {
        if (frameCounter - lastSeasonRefreshFrame < 240) return;
        lastSeasonRefreshFrame = frameCounter;

        if (!tintBlocksResolved)
        {
            tintBlocksResolved = true;
            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Code == null) continue;
                if (foliageTintBlock == null && block.SeasonColorMapResolved != null) foliageTintBlock = block;
                else if (grassTintBlock == null && block.ClimateColorMapResolved != null
                    && block.SeasonColorMapResolved == null) grassTintBlock = block;
                if (grassTintBlock != null && foliageTintBlock != null) break;
            }
        }

        int px = (int)camPos.X;
        int py = (int)camPos.Y;
        int pz = (int)camPos.Z;

        if (grassTintBlock != null)
        {
            UnpackTint(capi.World.ApplyColorMapOnRgba(
                grassTintBlock.ClimateColorMapResolved, grassTintBlock.SeasonColorMapResolved,
                unchecked((int)0xFFFFFFFF), px, py, pz), grassTint);
        }
        if (foliageTintBlock != null)
        {
            UnpackTint(capi.World.ApplyColorMapOnRgba(
                foliageTintBlock.ClimateColorMapResolved, foliageTintBlock.SeasonColorMapResolved,
                unchecked((int)0xFFFFFFFF), px, py, pz), foliageTint);
        }

        try
        {
            int seaLevel = capi.World.SeaLevel;
            climatePos.Set(px, seaLevel, pz);
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(climatePos);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(climatePos);

            if (low == null || high == null || low.Temperature <= high.Temperature)
            {
                snowLineY = 99999; // no usable lapse rate → snow line disabled
            }
            else
            {
                float lapsePerBlock = (low.Temperature - high.Temperature) / 150f;
                snowLineY = seaLevel + (low.Temperature - (-1f)) / lapsePerBlock;
                snowLineY = GameMath.Clamp(snowLineY, seaLevel - 64, 99999);
            }
        }
        catch
        {
            snowLineY = 99999;
        }
    }

    static void UnpackTint(int rgba, Vec3f into)
    {
        // ApplyColorMapOnRgba defaults to flipRb=true, so red arrives in the high byte.
        into.X = ((rgba >> 16) & 0xFF) / 255f;
        into.Y = ((rgba >> 8) & 0xFF) / 255f;
        into.Z = (rgba & 0xFF) / 255f;
    }

    /// <summary>Demand-driven (re)meshing: the selection walk is the load queue (Voxy's idea, CPU-side).</summary>
    void RequestMesh(long key)
    {
        if (meshJobInFlight.Contains(key)) return;

        // RAM-evicted sections still count: HasDataSet says whether the subtree has
        // data at all; the scheduler reloads the row from disk when it picks the job.
        if (world.Sections.TryGetValue(key, out LodSection? section))
        {
            if (section.CapturedColumns == 0) return;
        }
        else if (!world.HasDataSet.Contains(key))
        {
            return;
        }

        world.RenderDirty.Add(key);
    }

    void EvictStaleMeshes()
    {
        if (frameCounter % EvictSweepInterval != 0) return;

        evictBatch.Clear();
        foreach ((long key, MeshRef _) in sectionMeshes)
        {
            if (!lastSelectedFrame.TryGetValue(key, out long last) || frameCounter - last > EvictAfterFrames)
            {
                evictBatch.Add(key);
            }
        }
        foreach ((long key, MeshRef _) in waterMeshes)
        {
            if (!sectionMeshes.ContainsKey(key)
                && (!lastSelectedFrame.TryGetValue(key, out long last) || frameCounter - last > EvictAfterFrames))
            {
                evictBatch.Add(key);
            }
        }

        foreach (long key in evictBatch)
        {
            if (sectionMeshes.Remove(key, out MeshRef? mesh)) mesh.Dispose();
            if (waterMeshes.Remove(key, out MeshRef? water)) water.Dispose();
            lastSelectedFrame.Remove(key);
            EvictedTotal++;
        }
    }

    // ---- Mesh job scheduling + result upload ----

    readonly List<long> dirtyPrune = new();

    /// <summary>
    /// Drop meaningless render-dirty entries: no live mesh AND finer than the level
    /// the walk wants there — meshing those wastes work. Entries at wanted level or
    /// COARSER must survive: they are draw targets or gate meshes the walk descends
    /// through (pruning gates stalls descent and freezes approached terrain at the
    /// coarse level it was first meshed at). Runs every frame regardless of worker
    /// backlog — pruning must never starve.
    /// </summary>
    void PruneRenderDirty()
    {
        if (world.RenderDirty.Count == 0) return;

        dirtyPrune.Clear();
        foreach (long key in world.RenderDirty)
        {
            if (!HasAnyMesh(key) && LodWorld.KeyLevel(key) < WantedLevel(NearestDistanceTo(key)))
            {
                dirtyPrune.Add(key);
            }
        }
        foreach (long key in dirtyPrune) world.RenderDirty.Remove(key);
    }

    void ScheduleMeshJobs()
    {
        if (world.RenderDirty.Count == 0 || worker.PendingMeshes >= MaxWorkerMeshBacklog) return;

        int budget = MeshSchedulesPerFrame;
        while (budget-- > 0 && world.RenderDirty.Count > 0)
        {
            long best = 0;
            double bestDist = double.MaxValue;
            foreach (long key in world.RenderDirty)
            {
                if (meshJobInFlight.Contains(key)) continue;
                double d = NearestDistanceTo(key);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = key;
                }
            }
            if (bestDist == double.MaxValue) return; // everything dirty is in flight

            world.RenderDirty.Remove(best);

            if (!world.TryGetOrLoad(best, out LodSection section) || section.CapturedColumns == 0)
            {
                if (sectionMeshes.Remove(best, out MeshRef? stale)) stale.Dispose();
                if (waterMeshes.Remove(best, out MeshRef? staleWater)) staleWater.Dispose();
                continue;
            }

            var neighbors = new SectionSnapshot?[4];
            for (int d = 0; d < 4; d++)
            {
                long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
                if (world.Sections.TryGetValue(nk, out LodSection? nb)) neighbors[d] = SectionSnapshot.Of(nb);
            }

            meshJobInFlight.Add(best);
            worker.EnqueueMesh(new MeshJob
            {
                Key = best,
                Self = SectionSnapshot.Of(section),
                Neighbors = neighbors,
            });
        }
    }

    void UploadFinishedMeshes()
    {
        int budget = MeshUploadsPerFrame;
        while (budget-- > 0 && worker.MeshResults.TryDequeue(out MeshResult? result))
        {
            meshJobInFlight.Remove(result.Key);

            if (sectionMeshes.Remove(result.Key, out MeshRef? old)) old.Dispose();
            if (waterMeshes.Remove(result.Key, out MeshRef? oldWater)) oldWater.Dispose();

            if (result.IndexCount > 0)
            {
                sectionMeshes[result.Key] = Upload(result.Xyz, result.Rgba, result.Indices,
                    result.VertexCount, result.IndexCount);
            }

            if (result.WaterIndexCount > 0 && result.WaterXyz != null)
            {
                waterMeshes[result.Key] = Upload(result.WaterXyz, result.WaterRgba!, result.WaterIndices!,
                    result.WaterVertexCount, result.WaterIndexCount);
            }

            // Fresh uploads get a grace stamp so they aren't evicted before first selection.
            lastSelectedFrame[result.Key] = frameCounter;
        }
    }

    MeshRef Upload(float[] xyz, byte[] rgba, int[] indices, int vertCount, int indexCount)
    {
        var mesh = new MeshData(false);
        mesh.SetVerticesCount(vertCount);
        mesh.SetIndicesCount(indexCount);
        mesh.xyz = xyz;
        mesh.Rgba = rgba;
        mesh.Indices = indices;
        return capi.Render.UploadMesh(mesh);
    }

    // ---- Frame ----

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (AutoUnpause && capi.IsGamePaused) capi.PauseGame(false);

        if (prog == null || !shaderOk || prog.LoadError) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;
        frameCounter++;

        PruneRenderDirty();
        ScheduleMeshJobs();
        UploadFinishedMeshes();
        EvictStaleMeshes();
        RefreshSeasonalState();
        if (sectionMeshes.Count == 0 && waterMeshes.Count == 0) return;

        var playerData = capi.World.Player.WorldData;
        float viewDistance = playerData.DesiredViewDistance;
        if (playerData.LastApprovedViewDistance > 0)
        {
            viewDistance = Math.Min(viewDistance, playerData.LastApprovedViewDistance);
        }

        UpdateEffectiveFarDistance(viewDistance);
        ApplyZFar();

        drawList.Clear();
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        LastDrawCount = drawList.Count;
        if (drawList.Count == 0) return;

        prog.Use();
        rapi.GlDisableCullFace();

        prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);

        // Same matrices the shader gets, so the cull can never disagree with the draw.
        worldHeight = capi.World.BlockAccessor.MapSizeY;
        frustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        culledThisFrame = 0;

        prog.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
        prog.Uniform("sunColor", capi.World.Calendar.SunColor);
        prog.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));

        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);

        prog.Uniform("viewDistance", viewDistance);
        prog.Uniform("farViewDistance", EffectiveFarDistance);

        prog.Uniform("grassTint", grassTint);
        prog.Uniform("foliageTint", foliageTint);
        prog.Uniform("snowLineY", snowLineY);

        float cullDistSq = float.MaxValue;
        if (FarViewDistanceCap > 0)
        {
            float cull = FarViewDistanceCap + LodSection.SectionBlocks;
            cullDistSq = cull * cull;
        }

        // Pass 1: opaque terrain.
        foreach (long key in drawList)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 2: water, alpha-blended over the terrain.
        rapi.GlToggleBlend(true);
        foreach (long key in drawList)
        {
            if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }
        rapi.GlToggleBlend(false);

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    bool SetupSectionTransform(long key, float cullDistSq)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        double originX = LodWorld.KeySx(key) * (double)footprint;
        double originZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = originX + footprint / 2.0 - camPos.X;
        double dz = originZ + footprint / 2.0 - camPos.Z;
        if (dx * dx + dz * dz > cullDistSq) return false;

        // Camera-relative box, matching the model matrix below. Y spans the whole
        // world: sections don't track their vertical extent, and the wins that matter
        // (sections behind or beside the camera) come from the side planes anyway.
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;
        if (!frustum.BoxInView(relX, -camPos.Y, relZ, relX + footprint, worldHeight - camPos.Y, relZ + footprint))
        {
            culledThisFrame++;
            return false;
        }

        modelMat.Identity().Translate(relX, -camPos.Y, relZ);
        prog!.UniformMatrix("modelMatrix", modelMat.Values);
        return true;
    }

    int culledThisFrame;

    public void ClearMeshes()
    {
        foreach (MeshRef meshRef in sectionMeshes.Values) meshRef.Dispose();
        foreach (MeshRef meshRef in waterMeshes.Values) meshRef.Dispose();
        sectionMeshes.Clear();
        waterMeshes.Clear();
        meshJobInFlight.Clear();
        lastSelectedFrame.Clear();
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        ClearMeshes();
    }
}
