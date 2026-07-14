using System.Buffers;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Renders the LodGrid as colored heightmap terrain beyond the vanilla view distance.
/// Rendering techniques (render order/stage, ZFar extension, camera-relative model
/// matrices, fog + transition handling in the shaders) adapted from Farseer
/// (https://github.com/ViciousBadger/VSMod-Farseer, MIT, © Badgerson).
/// </summary>
public class LodTerrainRenderer : IRenderer
{
    public double RenderOrder => 0.36; // just before opaque terrain → occluded by real chunks
    public int RenderRange => 9999;

    const int MaxMeshRebuildsPerFrame = 2;

    readonly ICoreClientAPI capi;
    readonly LodGrid grid;
    readonly Dictionary<long, MeshRef> regionMeshes = new();
    readonly Matrixf modelMat = new();
    IShaderProgram? prog;
    bool shaderOk;

    public int FarViewDistance = 4096;
    public int MeshCount => regionMeshes.Count;

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

    /// <summary>Extend the camera far plane so LOD terrain isn't clipped. Call once the level is ready.</summary>
    public void ApplyZFar()
    {
        var clientMain = (ClientMain)capi.World;
        clientMain.MainCamera.ZFar = GameMath.Max(3000, FarViewDistance + 512);
        capi.Render.Reset3DProjection();
    }

    void RebuildDirtyMeshes()
    {
        if (grid.DirtyRegions.Count == 0) return;

        int budget = MaxMeshRebuildsPerFrame;
        List<long>? done = null;

        foreach (long rkey in grid.DirtyRegions)
        {
            RebuildRegionMesh(rkey);
            (done ??= new List<long>()).Add(rkey);
            if (--budget <= 0) break;
        }

        if (done != null) foreach (long rkey in done) grid.DirtyRegions.Remove(rkey);
    }

    void RebuildRegionMesh(long rkey)
    {
        if (!grid.Regions.TryGetValue(rkey, out LodRegion? region) || region.FilledSamples == 0)
        {
            if (regionMeshes.Remove(rkey, out MeshRef? stale)) stale.Dispose();
            return;
        }

        int rx = (int)(rkey & 0xFFFFFFFF);
        int rz = (int)(rkey >> 32);
        int gs = LodRegion.GridSize;
        int vertsPerEdge = gs + 1;
        int vertCount = vertsPerEdge * vertsPerEdge;

        float[] xyz = ArrayPool<float>.Shared.Rent(vertCount * 3);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(vertCount * 4);
        int[] indices = ArrayPool<int>.Shared.Rent(gs * gs * 6);
        bool[] valid = ArrayPool<bool>.Shared.Rent(vertCount);

        // Vertices on the global sample grid; the last row/column reads the east/south
        // neighbor region so meshes stitch seamlessly.
        for (int vz = 0; vz < vertsPerEdge; vz++)
        {
            for (int vx = 0; vx < vertsPerEdge; vx++)
            {
                int v = vz * vertsPerEdge + vx;
                valid[v] = grid.TryGetSample(rx * gs + vx, rz * gs + vz, out float height, out int color);

                xyz[v * 3 + 0] = vx * LodGrid.SampleStep + LodGrid.SampleStep / 2f;
                xyz[v * 3 + 1] = height;
                xyz[v * 3 + 2] = vz * LodGrid.SampleStep + LodGrid.SampleStep / 2f;

                rgba[v * 4 + 0] = (byte)(color & 0xFF);
                rgba[v * 4 + 1] = (byte)((color >> 8) & 0xFF);
                rgba[v * 4 + 2] = (byte)((color >> 16) & 0xFF);
                rgba[v * 4 + 3] = 255;
            }
        }

        // Only emit cells whose four corners all have data; holes fill in as chunks arrive.
        int indexCount = 0;
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

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (prog == null || !shaderOk || prog.LoadError) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        RebuildDirtyMeshes();
        if (regionMeshes.Count == 0) return;

        Vec3d camPos = capi.World.Player.Entity.CameraPos;
        float viewDistance = capi.World.Player.WorldData.DesiredViewDistance;

        prog.Use();

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
        prog.Uniform("farViewDistance", (float)FarViewDistance);

        float cullDistSq = (FarViewDistance + LodGrid.RegionBlocks) * (float)(FarViewDistance + LodGrid.RegionBlocks);

        foreach ((long rkey, MeshRef meshRef) in regionMeshes)
        {
            int rx = (int)(rkey & 0xFFFFFFFF);
            int rz = (int)(rkey >> 32);
            double originX = (double)rx * LodGrid.RegionBlocks;
            double originZ = (double)rz * LodGrid.RegionBlocks;

            double dx = originX + LodGrid.RegionBlocks / 2.0 - camPos.X;
            double dz = originZ + LodGrid.RegionBlocks / 2.0 - camPos.Z;
            if (dx * dx + dz * dz > cullDistSq) continue;

            modelMat.Identity().Translate(originX - camPos.X, -camPos.Y, originZ - camPos.Z);
            prog.UniformMatrix("modelMatrix", modelMat.Values);

            rapi.RenderMesh(meshRef);
        }

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
