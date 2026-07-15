using System.Buffers;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Renders the LodGrid pyramid as colored heightmap terrain beyond the vanilla view
/// distance. Each frame a quadtree walk over the top-level regions picks the detail
/// level by distance; a parent keeps rendering until all four child slots are covered
/// (mesh uploaded or provably empty), so level transitions never open holes (DH's
/// swap rule). Skirt geometry hides T-junction cracks at level boundaries.
///
/// Rendering techniques (render order/stage, ZFar extension, camera-relative model
/// matrices, fog + transition handling in the shaders) adapted from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => 0.36; // just before opaque terrain → occluded by real chunks
    public int RenderRange => 9999;

    const int MaxMeshRebuildsPerFrame = 2;
    const int BacklogMeshRebuildsPerFrame = 8;

    /// <summary>Level 0 renders out to twice this distance; each level doubles the band.</summary>
    const double DetailDistance = 1024;

    readonly ICoreClientAPI capi;
    readonly LodGrid grid;
    readonly Dictionary<long, MeshRef> regionMeshes = new();
    readonly Matrixf modelMat = new();
    readonly List<long> drawList = new();
    IShaderProgram? prog;
    bool shaderOk;
    float appliedZFar;
    Vec3d camPos = new();

    /// <summary>Optional hard cap in blocks; 0 = unlimited (render every cached region).</summary>
    public int FarViewDistanceCap = 0;

    /// <summary>Current far edge in blocks: the farthest loaded LOD data, independent of the vanilla view distance.</summary>
    public float EffectiveFarDistance { get; private set; } = 3000;

    public int MeshCount => regionMeshes.Count;
    public int LastDrawCount { get; private set; }

    public LodTerrainRenderer(ICoreClientAPI capi, LodGrid grid)
    {
        this.capi = capi;
        this.grid = grid;

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

    /// <summary>
    /// Extend the camera far plane to cover the farthest loaded LOD data. Cheap when
    /// nothing changed; re-applies automatically if the game reset the projection
    /// (e.g. the player changed the vanilla view distance in settings).
    /// </summary>
    public void ApplyZFar()
    {
        float needed = GameMath.Max(3000, EffectiveFarDistance + 512);
        var clientMain = (ClientMain)capi.World;

        if (clientMain.MainCamera.ZFar >= needed && appliedZFar == needed) return;

        clientMain.MainCamera.ZFar = needed;
        capi.Render.Reset3DProjection();
        appliedZFar = needed;
    }

    /// <summary>Farthest region corner from the camera, clamped to the cap (if any) and floored above the vanilla ring.</summary>
    void UpdateEffectiveFarDistance(float vanillaViewDistance)
    {
        double maxDistSq = 0;
        foreach (long rkey in regionMeshes.Keys)
        {
            int footprint = LodGrid.KeyFootprintBlocks(rkey);
            double dx = LodGrid.KeyRx(rkey) * (double)footprint + footprint / 2.0 - camPos.X;
            double dz = LodGrid.KeyRz(rkey) * (double)footprint + footprint / 2.0 - camPos.Z;
            double distSq = dx * dx + dz * dz;
            if (distSq > maxDistSq) maxDistSq = distSq;
        }

        float far = (float)Math.Sqrt(maxDistSq) + LodGrid.RegionBlocks * 1.5f;
        if (FarViewDistanceCap > 0) far = Math.Min(far, FarViewDistanceCap);

        // Keep the shader's transition band well-formed even with little data:
        // the far edge must stay beyond the inner (vanilla) transition ring.
        EffectiveFarDistance = GameMath.Max(far, vanillaViewDistance + 1536);
    }

    // ---- Detail selection (quadtree walk) ----

    /// <summary>2D distance from the camera to the nearest point of the region's footprint.</summary>
    double NearestDistanceTo(long rkey)
    {
        int footprint = LodGrid.KeyFootprintBlocks(rkey);
        double minX = LodGrid.KeyRx(rkey) * (double)footprint;
        double minZ = LodGrid.KeyRz(rkey) * (double)footprint;
        double dx = Math.Max(0, Math.Max(minX - camPos.X, camPos.X - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - camPos.Z, camPos.Z - (minZ + footprint)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    int WantedLevel(double distance) =>
        GameMath.Clamp((int)Math.Log2(Math.Max(1.0, distance / DetailDistance)), 0, LodGrid.MaxLevel);

    bool AllChildrenCovered(long rkey)
    {
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long ck = LodGrid.ChildKey(rkey, qx, qz);
                if (grid.HasDataSet.Contains(ck) && !regionMeshes.ContainsKey(ck)) return false;
            }
        }
        return true;
    }

    /// <summary>Collects the keys to draw this frame. Returns true if this subtree drew anything.</summary>
    bool CollectDrawNodes(long rkey)
    {
        bool hasMesh = regionMeshes.ContainsKey(rkey);
        int level = LodGrid.KeyLevel(rkey);

        if (level > 0)
        {
            bool wantFiner = level > WantedLevel(NearestDistanceTo(rkey));

            if ((wantFiner && AllChildrenCovered(rkey)) || !hasMesh)
            {
                bool anyChildDrew = false;
                for (int qz = 0; qz < 2; qz++)
                {
                    for (int qx = 0; qx < 2; qx++)
                    {
                        long ck = LodGrid.ChildKey(rkey, qx, qz);
                        if (grid.HasDataSet.Contains(ck)) anyChildDrew |= CollectDrawNodes(ck);
                    }
                }
                if (anyChildDrew || !hasMesh) return anyChildDrew;
            }
        }

        if (hasMesh)
        {
            drawList.Add(rkey);
            return true;
        }
        return false;
    }

    // ---- Mesh building ----

    void RebuildDirtyMeshes()
    {
        if (grid.DirtyRegions.Count == 0) return;

        int budget = grid.DirtyRegions.Count > 32 ? BacklogMeshRebuildsPerFrame : MaxMeshRebuildsPerFrame;

        // Nearest-first: pick the closest dirty region each iteration.
        while (budget-- > 0 && grid.DirtyRegions.Count > 0)
        {
            long best = 0;
            double bestDist = double.MaxValue;
            foreach (long rkey in grid.DirtyRegions)
            {
                double d = NearestDistanceTo(rkey);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = rkey;
                }
            }
            grid.DirtyRegions.Remove(best);
            RebuildRegionMesh(best);
        }
    }

    void RebuildRegionMesh(long rkey)
    {
        if (!grid.Regions.TryGetValue(rkey, out LodRegion? region) || region.FilledSamples == 0)
        {
            if (regionMeshes.Remove(rkey, out MeshRef? stale)) stale.Dispose();
            return;
        }

        int level = LodGrid.KeyLevel(rkey);
        int rx = LodGrid.KeyRx(rkey);
        int rz = LodGrid.KeyRz(rkey);
        int step = LodGrid.SampleStep << level;
        int gs = LodRegion.GridSize;
        int vertsPerEdge = gs + 1;
        int gridVertCount = vertsPerEdge * vertsPerEdge;
        int skirtVertCount = vertsPerEdge * 4;
        int vertCount = gridVertCount + skirtVertCount;
        float skirtDepth = 32 << level;

        float[] xyz = ArrayPool<float>.Shared.Rent(vertCount * 3);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(vertCount * 4);
        int[] indices = ArrayPool<int>.Shared.Rent((gs * gs + gs * 4) * 6);
        bool[] valid = ArrayPool<bool>.Shared.Rent(vertCount);

        // Grid vertices on the global sample grid of this level; the last row/column
        // reads the east/south neighbor region so same-level meshes stitch seamlessly.
        for (int vz = 0; vz < vertsPerEdge; vz++)
        {
            for (int vx = 0; vx < vertsPerEdge; vx++)
            {
                int v = vz * vertsPerEdge + vx;
                valid[v] = grid.TryGetSample(level, rx * gs + vx, rz * gs + vz, out float height, out int color);

                xyz[v * 3 + 0] = vx * step + step / 2f;
                xyz[v * 3 + 1] = height;
                xyz[v * 3 + 2] = vz * step + step / 2f;

                rgba[v * 4 + 0] = (byte)(color & 0xFF);
                rgba[v * 4 + 1] = (byte)((color >> 8) & 0xFF);
                rgba[v * 4 + 2] = (byte)((color >> 16) & 0xFF);
                rgba[v * 4 + 3] = 255;
            }
        }

        // Skirt vertices: perimeter verts extruded downward; hides cracks where
        // neighboring terrain renders at a different level (T-junctions).
        for (int i = 0; i < vertsPerEdge; i++)
        {
            int[] edgeGridVert =
            {
                i,                                    // north edge (vz = 0)
                (vertsPerEdge - 1) * vertsPerEdge + i, // south edge
                i * vertsPerEdge,                      // west edge
                i * vertsPerEdge + (vertsPerEdge - 1), // east edge
            };

            for (int e = 0; e < 4; e++)
            {
                int src = edgeGridVert[e];
                int v = gridVertCount + e * vertsPerEdge + i;
                valid[v] = valid[src];
                xyz[v * 3 + 0] = xyz[src * 3 + 0];
                xyz[v * 3 + 1] = xyz[src * 3 + 1] - skirtDepth;
                xyz[v * 3 + 2] = xyz[src * 3 + 2];
                rgba[v * 4 + 0] = rgba[src * 4 + 0];
                rgba[v * 4 + 1] = rgba[src * 4 + 1];
                rgba[v * 4 + 2] = rgba[src * 4 + 2];
                rgba[v * 4 + 3] = 255;
            }
        }

        int indexCount = 0;

        // Terrain cells: only where all four corners have data; holes fill in as chunks arrive.
        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int tl = cz * vertsPerEdge + cx;
                int bl = (cz + 1) * vertsPerEdge + cx;
                if (!valid[tl] || !valid[tl + 1] || !valid[bl] || !valid[bl + 1]) continue;

                indices[indexCount++] = tl;
                indices[indexCount++] = bl;
                indices[indexCount++] = tl + 1;
                indices[indexCount++] = tl + 1;
                indices[indexCount++] = bl;
                indices[indexCount++] = bl + 1;
            }
        }

        // Skirt cells (two triangles between adjacent perimeter verts and their extrusions).
        for (int e = 0; e < 4; e++)
        {
            for (int i = 0; i < gs; i++)
            {
                int topA, topB;
                switch (e)
                {
                    case 0: topA = i; topB = i + 1; break;
                    case 1: topA = (vertsPerEdge - 1) * vertsPerEdge + i; topB = topA + 1; break;
                    case 2: topA = i * vertsPerEdge; topB = (i + 1) * vertsPerEdge; break;
                    default: topA = i * vertsPerEdge + (vertsPerEdge - 1); topB = (i + 1) * vertsPerEdge + (vertsPerEdge - 1); break;
                }

                int botA = gridVertCount + e * vertsPerEdge + i;
                int botB = botA + 1;
                if (!valid[topA] || !valid[topB]) continue;

                indices[indexCount++] = topA;
                indices[indexCount++] = botA;
                indices[indexCount++] = topB;
                indices[indexCount++] = topB;
                indices[indexCount++] = botA;
                indices[indexCount++] = botB;
            }
        }

        if (regionMeshes.Remove(rkey, out MeshRef? old)) old.Dispose();

        if (indexCount > 0)
        {
            var mesh = new MeshData(false);
            mesh.SetVerticesCount(vertCount);
            mesh.SetIndicesCount(indexCount);
            mesh.xyz = xyz;
            mesh.Rgba = rgba;
            mesh.Indices = indices;

            regionMeshes[rkey] = capi.Render.UploadMesh(mesh);
        }

        ArrayPool<float>.Shared.Return(xyz);
        ArrayPool<byte>.Shared.Return(rgba);
        ArrayPool<int>.Shared.Return(indices);
        ArrayPool<bool>.Shared.Return(valid);
    }

    // ---- Frame ----

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (prog == null || !shaderOk || prog.LoadError) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        camPos = capi.World.Player.Entity.CameraPos;

        RebuildDirtyMeshes();
        if (regionMeshes.Count == 0) return;

        // Inner transition ring = where real terrain actually ends. On servers the
        // approved distance can be far below the client's desired setting.
        var playerData = capi.World.Player.WorldData;
        float viewDistance = playerData.DesiredViewDistance;
        if (playerData.LastApprovedViewDistance > 0)
        {
            viewDistance = Math.Min(viewDistance, playerData.LastApprovedViewDistance);
        }

        UpdateEffectiveFarDistance(viewDistance);
        ApplyZFar();

        drawList.Clear();
        foreach (long top in grid.TopLevelKeys) CollectDrawNodes(top);
        LastDrawCount = drawList.Count;
        if (drawList.Count == 0) return;

        prog.Use();

        // Skirt quads face outward on two edges and inward on the other two;
        // terrain is heightmap-like anyway, so double-sided rendering is harmless.
        rapi.GlDisableCullFace();

        prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);

        prog.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
        prog.Uniform("sunColor", capi.World.Calendar.SunColor);
        prog.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));

        prog.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        prog.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
        prog.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        prog.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);

        prog.Uniform("viewDistance", viewDistance);
        prog.Uniform("farViewDistance", EffectiveFarDistance);

        float cullDistSq = float.MaxValue;
        if (FarViewDistanceCap > 0)
        {
            float cull = FarViewDistanceCap + LodGrid.RegionBlocks;
            cullDistSq = cull * cull;
        }

        foreach (long rkey in drawList)
        {
            int footprint = LodGrid.KeyFootprintBlocks(rkey);
            double originX = LodGrid.KeyRx(rkey) * (double)footprint;
            double originZ = LodGrid.KeyRz(rkey) * (double)footprint;

            double dx = originX + footprint / 2.0 - camPos.X;
            double dz = originZ + footprint / 2.0 - camPos.Z;
            if (dx * dx + dz * dz > cullDistSq) continue;

            modelMat.Identity().Translate(originX - camPos.X, -camPos.Y, originZ - camPos.Z);
            prog.UniformMatrix("modelMatrix", modelMat.Values);

            capi.Render.RenderMesh(regionMeshes[rkey]);
        }

        rapi.GlEnableCullFace();
        prog.Stop();
    }

    public void ClearMeshes()
    {
        foreach (MeshRef meshRef in regionMeshes.Values) meshRef.Dispose();
        regionMeshes.Clear();
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        ClearMeshes();
    }
}
