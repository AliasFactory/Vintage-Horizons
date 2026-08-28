using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Renders the LodWorld section pyramid beyond the vanilla view distance. Meshes are
/// built off-thread by the LodWorker from section snapshots; this class schedules
/// mesh jobs (nearest-first), uploads finished vertex data on the render thread, and
/// walks the quadtree each frame picking detail by distance - a parent renders until
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
    /// <summary>
    /// Queue depth allowed at the mesh workers. Per thread, not absolute: a fixed 12 was
    /// sized for one builder and would leave a four-thread pool idling three quarters of
    /// the time. Deep enough that a thread finishing a job always has another waiting,
    /// shallow enough that the queue does not outlive the view that asked for it.
    /// </summary>
    const int MeshBacklogPerThread = 4;
    int maxWorkerMeshBacklog;

    /// <summary>Reload requests per frame; only enqueues a key, so it can far exceed the mesh budget.</summary>
    const int MeshLoadRequestsPerFrame = 32;

    readonly ICoreClientAPI capi;
    readonly LodWorld world;
    readonly LodWorker worker;
    /// <summary>
    /// What the renderer's own frame costs, by phase, since the last stats report. Each
    /// of these is tens of microseconds against a ten-millisecond frame, which is well
    /// under what any frame-rate comparison can resolve, so they are timed rather than
    /// inferred. Reported by .vhinfo and by the periodic stats line.
    /// </summary>
    public LodPhaseCost PruneCost, ScheduleCost, FarDistanceCost, WalkCost, DrawCost;

    public void ResetPhaseCosts()
    {
        PruneCost.Reset();
        ScheduleCost.Reset();
        FarDistanceCost.Reset();
        WalkCost.Reset();
        DrawCost.Reset();
    }

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
    float snowLineY = 99999;
    long lastSeasonRefreshFrame = -99999;
    readonly BlockPos climatePos = new(0, 0, 0);

    /// <summary>Optional hard cap in blocks; 0 = unlimited (render every cached section).</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;

    /// <summary>Bumped whenever the resident mesh geometry changes, so the far-edge scan
    /// can be skipped on frames that add or evict nothing.</summary>
    long meshGeomVersion;

    /// <summary>Quantized far-plane state: the applied camera ZFar only steps in
    /// StableProjectionStep blocks, growing immediately and shrinking after a hold, so
    /// ordinary movement does not reset the projection every frame.</summary>
    const int StableProjectionStep = 512;
    const int StableProjectionHoldMs = 5000;
    float stableProjectionZFar = 3000 + StableProjectionStep;
    int stableProjectionHoldMsLeft;
    long lastStableProjectionClock;

    public int MeshCount => sectionMeshes.Count;
    public int LastDrawCount { get; private set; }

    /// <summary>Sections selected by the walk but skipped this frame as off-screen.</summary>
    public int LastCulledCount { get; private set; }

    /// <summary>Draw opaque sections nearest first so the depth buffer has nearer depth
    /// before far sections shade. Water keeps traversal order.</summary>
    public bool OpaqueNearToFar = true;

    /// <summary>Reusable nearest-first ordering for the opaque draw pass.</summary>
    readonly List<long> opaqueOrder = new();

    readonly LodFrustum frustum = new();
    int worldHeight = 1024;


    /// <summary>
    /// Why each coarse node in the current draw list is not descending. Written for one
    /// specific failure: a node drawn far below its wanted level, with the pipeline idle, so
    /// no amount of waiting changes it. Reports each child's actual state rather than an
    /// inference - which is what three wrong diagnoses in a row cost.
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

        // The shaders carry their own `const int TINT_SLOTS`, because this game version
        // exposes no way to inject a #define, and a mismatch decodes water as opaque and
        // thin plants as water with no compile error. That used to be guarded here by
        // comparing MaxSlots against a hand-maintained C# mirror of the shader's value -
        // which is two constants in one file, and so could never notice a shader being
        // edited. The compiler agreed: the branch raised CS0162, unreachable code.
        // The check that works reads the shader files, in the fast tier of check.sh.

        uploadedTintVersion = -1; // fresh program object: uniform state is gone
        shaderOk = prog.Compile();
        if (!shaderOk) capi.Logger.Error("[VintageHorizons] lodterrain shader failed to compile; LOD rendering disabled");
        return shaderOk;
    }

    public void ApplyZFar()
    {
        // The camera far plane is quantized: it only steps in StableProjectionStep
        // blocks, growing immediately and shrinking after a hold, so ordinary movement
        // does not reset the projection every frame. The continuous shader far edge
        // (EffectiveFarDistance) stays separate and is what the shader reads.
        float needed = GameMath.Max(3000, EffectiveFarDistance + 512);
        var clientMain = (ClientMain)capi.World;

        // Grow immediately to the step that covers the needed distance. The hold is
        // armed on grow too: with it at zero, the very next recessive frame would step
        // straight back down, oscillating between adjacent steps while the camera
        // straddles the far edge - the exact pull-back the hold exists to prevent.
        int neededStep = (int)Math.Ceiling(needed / StableProjectionStep) * StableProjectionStep;
        if (neededStep > stableProjectionZFar)
        {
            stableProjectionZFar = neededStep;
            stableProjectionHoldMsLeft = StableProjectionHoldMs;
            lastStableProjectionClock = capi.World.ElapsedMilliseconds;
        }
        else if (neededStep < stableProjectionZFar)
        {
            // Recessive: only step down after the hold has elapsed at the closer
            // distance, so a camera that briefly exposes more far terrain does not
            // pull the plane back and forth.
            long now = capi.World.ElapsedMilliseconds;
            // Clamp the delta: this value resets on world load while the renderer (and
            // its clock field) outlive worlds, so an unclamped subtraction could
            // inflate the hold by the previous session's elapsed time.
            long delta = lastStableProjectionClock == 0 ? 0
                : Math.Max(0, now - lastStableProjectionClock);
            stableProjectionHoldMsLeft -= (int)delta;
            lastStableProjectionClock = now;
            if (stableProjectionHoldMsLeft <= 0)
            {
                stableProjectionZFar = Math.Max(3000, stableProjectionZFar - StableProjectionStep);
                stableProjectionHoldMsLeft = StableProjectionHoldMs;
            }
        }
        else
        {
            lastStableProjectionClock = capi.World.ElapsedMilliseconds;
        }

        if (clientMain.MainCamera.ZFar >= stableProjectionZFar && appliedZFar == stableProjectionZFar) return;

        clientMain.MainCamera.ZFar = stableProjectionZFar;
        capi.Render.Reset3DProjection();
        appliedZFar = stableProjectionZFar;
    }

    void UpdateEffectiveFarDistance(float vanillaViewDistance)
    {
        // The far edge only changes when the resident mesh set changes (a mesh landed
        // or was evicted) or the camera moved far enough to matter. Skip the O(n) scan
        // over every mesh on frames where neither happened.
        if (meshGeomVersion == lastFarScanVersion
            && Math.Abs(camPos.X - lastFarScanCamX) < 8
            && Math.Abs(camPos.Z - lastFarScanCamZ) < 8)
        {
            return;
        }
        lastFarScanVersion = meshGeomVersion;
        lastFarScanCamX = camPos.X;
        lastFarScanCamZ = camPos.Z;

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

    long lastFarScanVersion = -1;
    double lastFarScanCamX, lastFarScanCamZ;

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

    /// <summary>
    /// Squared distance to the section's nearest edge. The hot paths compare and pick a
    /// level, and both work on the square, so neither needs the root.
    /// </summary>
    double NearestDistanceSqTo(long key) => LodWorld.NearestDistanceSqTo(key, camPos.X, camPos.Z);

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

                // A resident section with nothing captured will never get a mesh --
                // RequestMesh refuses it, by design. Counting it as uncovered pins the
                // parent at its own level permanently, which is not the transient
                // wait-for-mesh this gate exists for: it showed up as hard-edged coarse
                // plates over ground whose finer data was already loaded, with the whole
                // pipeline idle. Treat it like an absent child instead.
                if (world.Sections.TryGetValue(ck, out LodSection? child) && child.CapturedColumns == 0)
                {
                    continue;
                }

                if (HasAnyMesh(ck))
                {
                    // Gate meshes are load-bearing even when never drawn (the walk
                    // descends THROUGH them) - stamp so the evictor spares them.
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
        int wanted = LodWorld.WantedLevelForSq(NearestDistanceSqTo(key));

        // Demand-driven meshing: request ONLY at the level the walk actually wants
        // here. Descending through meshless parents must not request every leaf the
        // recursion happens to reach - coarser/finer nodes on the path stay unmeshed
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
    /// Refresh the live tint table and snow line (~every 4s). Each tint slot is sampled
    /// at two altitudes and interpolated per vertex, because the climate maps are keyed
    /// by temperature and temperature falls with height; the snow line extrapolates the
    /// same lapse rate to where it hits freezing.
    /// </summary>
    void RefreshSeasonalState()
    {
        if (frameCounter - lastSeasonRefreshFrame < 240) return;
        lastSeasonRefreshFrame = frameCounter;

        int px = (int)camPos.X;
        int pz = (int)camPos.Z;

        // Every registered colour-map pair at once: leaves are per species (oak turns
        // while pine stays green) and water has its own map, so one shared foliage tint
        // was never going to be right.
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
            meshGeomVersion++;
        }
    }

    // ---- Mesh job scheduling + result upload ----

    readonly List<long> dirtyPrune = new();

    /// <summary>
    /// Drop meaningless render-dirty entries: no live mesh AND finer than the level
    /// the walk wants there - meshing those wastes work. Entries at wanted level or
    /// COARSER must survive: they are draw targets or gate meshes the walk descends
    /// through (pruning gates stalls descent and freezes approached terrain at the
    /// coarse level it was first meshed at). Runs every frame regardless of worker
    /// backlog - pruning must never starve.
    /// </summary>
    void PruneRenderDirty()
    {
        if (world.RenderDirty.Count == 0) return;

        dirtyPrune.Clear();
        foreach (long key in world.RenderDirty)
        {
            if (!HasAnyMesh(key) && LodWorld.KeyLevel(key) < LodWorld.WantedLevelForSq(NearestDistanceSqTo(key)))
            {
                dirtyPrune.Add(key);
            }
        }
        foreach (long key in dirtyPrune) world.RenderDirty.Remove(key);
    }

    readonly long[] scheduleCandidates = new long[MeshSchedulesPerFrame + MeshLoadRequestsPerFrame];
    readonly double[] scheduleCandidateDistSq = new double[MeshSchedulesPerFrame + MeshLoadRequestsPerFrame];

    /// <summary>
    /// The nearest dirty keys that can start work now, nearest first, at most as many as
    /// one frame could possibly use. Returns how many were found.
    ///
    /// A fixed insertion buffer rather than a sort: the buffer holds 36 and the dirty set
    /// can hold thousands, so sorting the set to take its head would be the same mistake
    /// in a different shape. Squared distances, because the order is all that is wanted
    /// from them and the square root does not change it.
    /// </summary>
    int SelectNearestDirty()
    {
        int count = 0;
        int capacity = scheduleCandidates.Length;

        foreach (long key in world.RenderDirty)
        {
            // Skip anything already being meshed or reloaded, so the per-frame budget
            // goes to sections that can actually start work now.
            if (meshJobInFlight.Contains(key) || world.LoadsInFlight.Contains(key)) continue;

            double distSq = NearestDistanceSqTo(key);
            if (count == capacity && distSq >= scheduleCandidateDistSq[count - 1]) continue;

            int at = count < capacity ? count++ : capacity - 1;
            while (at > 0 && scheduleCandidateDistSq[at - 1] > distSq)
            {
                scheduleCandidateDistSq[at] = scheduleCandidateDistSq[at - 1];
                scheduleCandidates[at] = scheduleCandidates[at - 1];
                at--;
            }
            scheduleCandidateDistSq[at] = distSq;
            scheduleCandidates[at] = key;
        }

        return count;
    }

    void ScheduleMeshJobs()
    {
        if (world.RenderDirty.Count == 0 || worker.PendingMeshes >= maxWorkerMeshBacklog) return;

        // Two budgets. Starting a background reload costs this thread almost nothing
        // (enqueue a key), whereas building a mesh snapshot is real work, so charging a
        // reload against the mesh budget throttled join fill-in badly: every section
        // needed two passes to appear and only four could be touched per frame.
        int meshBudget = MeshSchedulesPerFrame;
        int loadBudget = MeshLoadRequestsPerFrame;

        // ONE pass over the dirty set, keeping the nearest few, rather than a fresh scan
        // of the whole set for every key scheduled.
        //
        // The iteration cap below has been here a while, and the reason it was added is
        // still true: the paths that drop a key without starting work charge neither
        // budget, so without a cap the loop runs until RenderDirty drains. But capping
        // the number of iterations left each iteration scanning everything, so the work
        // stayed proportional to cap times dirty, with a square root per element.
        // Measured at 2.6ms inside a single frame during fill-in, which is a visible
        // stutter exactly when the player is exploring.
        int candidates = SelectNearestDirty();

        for (int i = 0; i < candidates && meshBudget > 0 && loadBudget > 0; i++)
        {
            long best = scheduleCandidates[i];

            // It was dirty and not in flight a moment ago, and nothing below touches any
            // key but the one it is working on. Remove says so anyway for the price of
            // the probe the old code was making regardless.
            if (!world.RenderDirty.Remove(best)) continue;

            // Non-blocking: an evicted section starts a background reload and is
            // re-requested by the selection walk once it lands, rather than stalling
            // this frame on a decompress.
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
                    if (gone != null || goneWater != null) meshGeomVersion++;
                }
                continue;
            }

            if (section.CapturedColumns == 0)
            {
                if (sectionMeshes.Remove(best, out MeshRef? stale)) stale.Dispose();
                if (waterMeshes.Remove(best, out MeshRef? staleWater)) staleWater.Dispose();
                if (stale != null || staleWater != null) meshGeomVersion++;
                continue;
            }

            var neighbors = new SectionSnapshot?[4];
            for (int d = 0; d < 4; d++)
            {
                long nk = LodWorld.NeighborKey(best, d == 0 ? -1 : d == 1 ? 1 : 0, d == 2 ? -1 : d == 3 ? 1 : 0);
                if (world.Sections.TryGetValue(nk, out LodSection? nb)) neighbors[d] = SectionSnapshot.Of(nb);
                else if (world.HasDataSet.Contains(nk))
                {
                    // G51: this neighbour has data on disk but is not resident, so the
                    // mesher will treat the shared edge as the open frontier and wall it.
                    // Record the edge so the wall is repaired when the neighbour loads.
                    if (!world.PhantomFrontierEdges.TryGetValue(nk, out HashSet<long>? waiting))
                        world.PhantomFrontierEdges[nk] = waiting = new HashSet<long>();
                    waiting.Add(best);
                }
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

            // Fresh uploads get a grace stamp so they aren't evicted before first selection.
            lastSelectedFrame[result.Key] = frameCounter;
            meshGeomVersion++;
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

    /// <summary>
    /// Fill <see cref="opaqueOrder"/> with the current draw list ordered nearest first.
    /// Water keeps traversal order and is untouched.
    /// </summary>
    void OrderOpaqueNearToFar()
    {
        opaqueOrder.Clear();
        opaqueOrder.AddRange(drawList);
        opaqueOrder.Sort((a, b) =>
            LodWorld.NearestDistanceSqTo(a, camPos.X, camPos.Z)
                .CompareTo(LodWorld.NearestDistanceSqTo(b, camPos.X, camPos.Z)));
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

        // Timed apart, not together. Lumped into one counter they cannot be told apart,
        // and they are different shapes: pruning walks the whole dirty set once a frame,
        // while scheduling picks a bounded number of jobs out of it. A spike in the pair
        // was being read as a spike in scheduling.
        long phaseStart = LodPhaseCost.Start();
        PruneRenderDirty();
        PruneCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start();
        ScheduleMeshJobs();
        ScheduleCost.Add(phaseStart);

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

        phaseStart = LodPhaseCost.Start();
        UpdateEffectiveFarDistance(viewDistance);
        FarDistanceCost.Add(phaseStart);
        ApplyZFar();

        phaseStart = LodPhaseCost.Start();
        drawList.Clear();
        foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);
        WalkCost.Add(phaseStart);
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

        // Uniforms persist in the program between Use() calls, so re-upload only when
        // the table actually changed (every ~240 frames) rather than every frame.
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

        phaseStart = LodPhaseCost.Start();

        // Pass 1: opaque terrain. Nearest-first when the toggle is on, so the depth
        // buffer has nearer depth before far sections shade. Water keeps traversal
        // order in pass 2.
        //
        // With one or fewer sections there is nothing to order, and reusing the
        // previous frame's list would draw a stale section (the walk collapsed after
        // that list was built). Refresh whenever there is at least one section.
        if (OpaqueNearToFar && drawList.Count > 0)
        {
            if (drawList.Count > 1) OrderOpaqueNearToFar();
            else { opaqueOrder.Clear(); opaqueOrder.Add(drawList[0]); }
        }
        List<long> opaqueSource = OpaqueNearToFar && drawList.Count > 0 ? opaqueOrder : drawList;
        foreach (long key in opaqueSource)
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

        // Submission only. RenderMesh queues work for the GPU and returns, so this
        // measures the CPU cost of the draw loop -- the uniform uploads, the culling and
        // the dictionary probes -- and not what the GPU then does with it.
        DrawCost.Add(phaseStart);

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
        prog.Uniform("columnBlocks", (float)LodWorld.ColumnStepBlocks(LodWorld.KeyLevel(key)));

        // Sides that border on never-captured area, so the shader can dissolve them
        // into the horizon instead of leaving a cliff at the edge of what we've seen.
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
    /// Whether the neighbouring section holds (or covers) data. Checked at the drawn
    /// section's own level: a coarse section's neighbour is coarse too, and its
    /// presence in HasDataSet means something in that subtree was captured.
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
        // Our own resources first. UnregisterRenderer refuses to run off the main thread,
        // and the game's shutdown crash path disposes mods from another one, so putting
        // the engine call first meant a crashing client freed none of its GPU meshes.
        ClearMeshes();
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}
