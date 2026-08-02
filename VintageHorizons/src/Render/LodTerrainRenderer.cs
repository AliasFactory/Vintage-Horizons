using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Draws the section pyramid of the LodWorld, past the vanilla view distance.
///
/// The LodWorker builds the meshes on another thread, from section snapshots. This class
/// schedules the mesh jobs, with the nearest first. It uploads the finished vertex data on
/// the render thread. In each frame it walks the quadtree and selects the detail by the
/// distance.
///
/// A parent draws until all four child slots have cover. Thus a change of level never opens a
/// hole. This is the rule of Distant Horizons.
///
/// The rendering methods come from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson). Those are the render
/// order and stage, the ZFar extension, the camera-relative model matrices, and the fog and
/// transition in the shaders.
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => 0.36; // Immediately before the opaque terrain, thus the real chunks hide it.
    public int RenderRange => 9999;

    const int MeshSchedulesPerFrame = 4;
    const int MeshUploadsPerFrame = 4;
    /// <summary>
    /// The depth of the queue at the mesh workers. This value is for each thread, and it is
    /// not a total.
    ///
    /// A fixed value of 12 was correct for one builder. It leaves a pool of four threads idle
    /// for three quarters of the time.
    ///
    /// The depth is large enough that a thread which completes a job always has another job.
    /// It is small enough that the queue does not continue after the view that asked for
    /// it.
    /// </summary>
    const int MeshBacklogPerThread = 4;
    int maxWorkerMeshBacklog;

    /// <summary>The number of load requests in each frame. A request only puts a key into a
    /// queue. Thus this value can be much larger than the mesh budget.</summary>
    const int MeshLoadRequestsPerFrame = 32;

    readonly ICoreClientAPI capi;
    readonly LodWorld world;
    readonly LodWorker worker;
    readonly Dictionary<long, MeshRef> sectionMeshes = new();
    readonly Dictionary<long, MeshRef> waterMeshes = new();
    readonly HashSet<long> meshJobInFlight = new();
    readonly Dictionary<long, long> lastSelectedFrame = new();
    readonly List<long> evictBatch = new();
    long frameCounter;

    /// <summary>The mod removes a mesh that the walk did not select for this number of
    /// frames, which is approximately 1 minute. The quadtree asks for it again when it needs
    /// it.</summary>
    const int EvictAfterFrames = 3600;
    const int EvictSweepInterval = 300;

    public int EvictedTotal { get; private set; }
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();

    /// <summary>For development and testing. It keeps the game running when the window has
    /// no focus.</summary>
    public bool AutoUnpause;

    // The live state of the season. The mod refreshes it from time to time, and gives it to
    // the shader as uniforms.
    float snowLineY = 99999;
    long lastSeasonRefreshFrame = -99999;
    readonly BlockPos climatePos = new(0, 0, 0);

    /// <summary>An optional limit in blocks. A value of 0 is unlimited, and then the mod
    /// draws each cached section.</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>The current far edge in blocks. This is the most distant LOD data that the
    /// mod loaded. The vanilla view distance does not affect it.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;

    public int MeshCount => sectionMeshes.Count;
    public int LastDrawCount { get; private set; }

    /// <summary>The sections that the walk selected, and that the mod skipped in this frame
    /// because they are off the screen.</summary>
    public int LastCulledCount { get; private set; }

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;


    /// <summary>
    /// The reason why each coarse node in the current draw list does not descend.
    ///
    /// This exists for one failure: a node that the mod draws far below its wanted level,
    /// with an idle pipeline. Then no quantity of waiting changes the picture.
    ///
    /// This reports the real state of each child. It does not infer that state. Three wrong
    /// diagnoses in sequence are the cost of an inference.
    /// </summary>
    public string ExplainCoarseDraws(double px, double pz, int maxNodes = 6)
    {
        var sb = new System.Text.StringBuilder();
        int shown = 0;

        foreach (long key in drawList)
        {
            int level = LodWorld.KeyLevel(key);
            int wanted = WantedLevel(NearestDistanceTo(key));
            if (level <= wanted || shown >= maxNodes) continue;

            shown++;
            double dist = Math.Sqrt(LodWorld.NearestDistanceSqTo(key, px, pz));
            sb.Append($"\n  L{level} at {LodWorld.KeySx(key) * LodWorld.KeyFootprintBlocks(key)},")
              .Append($"{LodWorld.KeySz(key) * LodWorld.KeyFootprintBlocks(key)} dist {(int)dist} wants L{wanted}:");

            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    long ck = LodWorld.ChildKey(key, qx, qz);
                    string state;
                    if (!world.HasDataSet.Contains(ck)) state = "no-data";
                    else if (!world.Sections.TryGetValue(ck, out LodSection? cs))
                    {
                        state = world.LoadsInFlight.Contains(ck) ? "loading"
                            : world.LoadFailed.Contains(ck) ? "load-failed"
                            : "not-resident";
                    }
                    else if (cs.CapturedColumns == 0) state = "empty";
                    else if (!HasAnyMesh(ck)) state = world.RenderDirty.Contains(ck) ? "meshing" : "no-mesh!";
                    else state = "ok";
                    sb.Append(' ').Append(state);
                }
            }
        }

        return shown == 0 ? "no coarse draws: every drawn node is at or below its wanted level" : sb.ToString();
    }

    public string DescribeDrawnLevels()
    {
        var counts = new int[LodWorld.MaxLevel + 1];
        foreach (long key in drawList) counts[LodWorld.KeyLevel(key)]++;
        return string.Join(" ", counts.Select((c, i) => $"L{i}:{c}"));
    }

    readonly LodTintRegistry tints;
    int uploadedTintVersion = -1;

    public LodTerrainRenderer(ICoreClientAPI capi, LodWorld world, LodWorker worker, LodTintRegistry tints)
    {
        this.capi = capi;
        this.world = world;
        this.worker = worker;
        this.tints = tints;
        maxWorkerMeshBacklog = worker.MeshThreads * MeshBacklogPerThread;

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

        // Each shader carries its own `const int TINT_SLOTS`, because this version of the
        // game cannot inject a #define. A difference decodes water as opaque, and thin
        // plants as water, with no compile error.
        //
        // A guard here compared MaxSlots against a C# copy of the shader value, maintained
        // by hand. That is two constants in one file, thus it can never find an edit to a
        // shader. The compiler agreed, and the branch raised CS0162, unreachable code.
        //
        // The check that operates reads the shader files. It is in the fast tier of
        // check.sh.

        uploadedTintVersion = -1; // fresh program object: uniform state is gone
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

    // ---- Detail selection, which is the walk over the quadtree ----

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

                // A resident section with no captured column never gets a mesh, because
                // RequestMesh refuses it by design.
                //
                // A count of that section as "not covered" holds the parent at its own level
                // permanently. This gate exists for a short wait for a mesh, and not for
                // that.
                //
                // The symptom was a coarse plate with a hard edge, above ground whose finer
                // data was loaded already, with an idle pipeline. Treat such a section as an
                // absent child.
                if (world.Sections.TryGetValue(ck, out LodSection? child) && child.CapturedColumns == 0)
                {
                    continue;
                }

                if (HasAnyMesh(ck))
                {
                    // A gate mesh is necessary even when the mod never draws it, because
                    // the walk descends THROUGH it. Stamp it, thus the evictor keeps it.
                    lastSelectedFrame[ck] = frameCounter;
                }
                else
                {
                    // The gate is absent, because the mod evicted it or never built it. Ask
                    // for it again, thus the descent can continue. The parent gives the
                    // cover until then.
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

        // The meshing depends on demand. Ask ONLY at the level that the walk wants here.
        //
        // A descent through parents with no mesh must not ask for each leaf that the
        // recursion reaches. A coarser node or a finer node on that path stays without a
        // mesh, until the wanted level for its own distance changes.
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
    /// Refresh the live tint table and the snow line, approximately every 4 seconds.
    ///
    /// The mod samples each tint slot at two altitudes, and it interpolates for each vertex.
    /// The climate maps use the temperature as their key, and the temperature decreases with
    /// the height.
    ///
    /// The snow line uses the same lapse rate. It calculates the height at which the
    /// temperature reaches freezing.
    /// </summary>
    void RefreshSeasonalState()
    {
        if (frameCounter - lastSeasonRefreshFrame < 240) return;
        lastSeasonRefreshFrame = frameCounter;

        int px = (int)camPos.X;
        int pz = (int)camPos.Z;

        // Do each registered pair of color maps together. Leaves use a map for each
        // species, thus an oak changes color and a pine stays green. Water has its own map.
        // Thus one shared tint for foliage can never be correct.
        tints.Refresh(capi.World, px, pz);

        try
        {
            int seaLevel = capi.World.SeaLevel;
            climatePos.Set(px, seaLevel, pz);
            ClimateCondition? low = capi.World.BlockAccessor.GetClimateAt(climatePos);
            climatePos.Set(px, seaLevel + 150, pz);
            ClimateCondition? high = capi.World.BlockAccessor.GetClimateAt(climatePos);

            if (low == null || high == null || low.Temperature <= high.Temperature)
            {
                snowLineY = 99999; // There is no usable lapse rate, thus the snow line is off.
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


    /// <summary>Meshing that depends on demand. The selection walk is also the load queue.
    /// This idea comes from Voxy, and this code does it on the CPU.</summary>
    void RequestMesh(long key)
    {
        if (meshJobInFlight.Contains(key)) return;

        // A section that the mod evicted from RAM still counts. HasDataSet gives whether
        // the subtree holds any data. The scheduler loads the row from the disk when it
        // takes the job.
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

    // ---- Schedule the mesh jobs, and upload the results ----

    readonly List<long> dirtyPrune = new();

    /// <summary>
    /// Remove the render-dirty entries that have no use. Such an entry has no live mesh, and
    /// it is finer than the level that the walk wants there. A mesh for it is wasted work.
    ///
    /// An entry at the wanted level, or COARSER, must stay. It is a draw target, or it is a
    /// gate mesh that the walk descends through. Removal of a gate stops the descent. Then
    /// terrain that the player approaches stays at the coarse level of its first mesh.
    ///
    /// This runs in each frame, whatever the backlog of the workers. The removal must never
    /// stop.
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
        if (world.RenderDirty.Count == 0 || worker.PendingMeshes >= maxWorkerMeshBacklog) return;

        // There are two budgets. The start of a background load costs this thread almost
        // nothing, because it only puts a key into a queue. A mesh snapshot is real work.
        //
        // One budget for both slowed the fill-in at a join badly. Each section needed two
        // passes to appear, and the mod touched only four in each frame.
        int meshBudget = MeshSchedulesPerFrame;
        int loadBudget = MeshLoadRequestsPerFrame;

        // There is a firm limit on the iterations, and not on the two budgets only.
        //
        // Some paths remove a key and start no work. Those are a section with no data, and a
        // section whose load returned nothing already. They use neither budget.
        //
        // Without this limit, the loop runs until RenderDirty is empty. Each iteration also
        // scans the full set again for the nearest key. A few hundred such keys made one
        // frame do a six-figure scan.
        int steps = MeshSchedulesPerFrame + MeshLoadRequestsPerFrame;

        while (steps-- > 0 && meshBudget > 0 && loadBudget > 0 && world.RenderDirty.Count > 0)
        {
            long best = 0;
            double bestDist = double.MaxValue;
            foreach (long key in world.RenderDirty)
            {
                // Skip a section that a worker meshes now, or that the mod loads now. Thus
                // the budget of this frame goes to a section that can start work now.
                if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;
                double d = NearestDistanceTo(key);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = key;
                }
            }
            if (bestDist == double.MaxValue) return; // everything dirty is in flight

            world.RenderDirty.Remove(best);

            // This call does not block. An evicted section starts a background load. The
            // selection walk asks for it again after it arrives. Thus a decompress does not
            // delay this frame.
            if (!world.TryGetForRender(best, out LodSection section))
            {
                if (world.LoadsInFlight.Contains(best))
                {
                    loadBudget--; // a reload is now under way; the walk re-requests it
                }
                else
                {
                    if (sectionMeshes.Remove(best, out MeshRef? gone)) gone.Dispose();
                    if (waterMeshes.Remove(best, out MeshRef? goneWater)) goneWater.Dispose();
                }
                continue;
            }

            if (section.CapturedColumns == 0)
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

            meshBudget--;
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

            // A new upload gets a stamp. Thus the mod does not evict it before the walk
            // selects it one time.
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

        // These are the same matrices that the shader gets. Thus the cull can never
        // disagree with the draw.
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

        // The uniforms stay in the program between the calls to Use(). Thus upload them
        // again only when the table changed, which is approximately every 240 frames. Do not
        // upload them in each frame.
        if (uploadedTintVersion != tints.Version)
        {
            uploadedTintVersion = tints.Version;
            prog.Uniforms4("tintsLow", LodTintRegistry.MaxSlots, tints.TintsLow);
            prog.Uniforms4("tintsHigh", LodTintRegistry.MaxSlots, tints.TintsHigh);
            prog.Uniform("tintYLow", tints.SampleYLow);
            prog.Uniform("tintYHigh", tints.SampleYHigh);
        }
        prog.Uniform("snowLineY", snowLineY);

        float cullDistSq = float.MaxValue;
        if (FarViewDistanceCap > 0)
        {
            float cull = FarViewDistanceCap + LodSection.SectionBlocks;
            cullDistSq = cull * cull;
        }

        // Pass 1: the opaque terrain.
        foreach (long key in drawList)
        {
            if (!sectionMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullDistSq)) continue;
            capi.Render.RenderMesh(mesh);
        }

        LastCulledCount = culledThisFrame; // opaque pass only: water covers a subset

        // Pass 2: the water, alpha-blended over the terrain.
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

        // The box is relative to the camera, and it matches the model matrix below.
        //
        // Y covers the full world, because a section does not record its vertical extent.
        // The important gains come from the side planes anyway, and those are the sections
        // behind the camera and beside it.
        double relX = originX - camPos.X;
        double relZ = originZ - camPos.Z;
        if (!frustum.BoxInView(relX, -camPos.Y, relZ, relX + footprint, worldHeight - camPos.Y, relZ + footprint))
        {
            culledThisFrame++;
            return false;
        }

        modelMat.Identity().Translate(relX, -camPos.Y, relZ);
        prog!.UniformMatrix("modelMatrix", modelMat.Values);
        prog.Uniform("columnBlocks", (float)LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)));

        // The sides that touch an area that the mod never captured. Thus the shader can
        // fade them into the horizon, and it does not leave a cliff at the edge of the
        // explored area.
        prog.Uniform("sectionSize", (float)footprint);
        prog.Uniform("openEdges",
            HasNeighbourData(key, -1, 0) ? 0f : 1f,
            HasNeighbourData(key, 1, 0) ? 0f : 1f,
            HasNeighbourData(key, 0, -1) ? 0f : 1f,
            HasNeighbourData(key, 0, 1) ? 0f : 1f);
        return true;
    }

    int culledThisFrame;

    /// <summary>
    /// Whether the neighbour section holds data, or covers data.
    ///
    /// The test uses the level of the section that the mod draws. The neighbour of a coarse
    /// section is coarse also. Its presence in HasDataSet means that the mod captured
    /// something in that subtree.
    /// </summary>
    bool HasNeighbourData(long key, int dx, int dz) =>
        world.HasDataSet.Contains(LodWorld.NeighborKey(key, dx, dz));

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
