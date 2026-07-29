# Vintage Story Modding API Research - Client-Side Extended-Render-Distance LOD Mod

Research current as of 2026-07-14. Findings verified against the official API docs (apidocs.vintagestory.at), the [vsapi source](https://github.com/anegostudios/vsapi), the [Farseer mod source](https://github.com/ViciousBadger/VSMod-Farseer), and decompiled VintagestoryLib 1.20.7 sources mirrored at [Primer81/vintage-story-mods](https://github.com/Primer81/vintage-story-mods/tree/master/references/1.20.7/DecompiledSource/VintagestoryLib).

## 0. Game version and runtime (July 2026)

- **Latest stable: Vintage Story 1.22.3**, released May 30, 2026 ("[1.22.3 - Maintenance patch](https://www.vintagestory.at/blog.html/news/1223-maintenance-patch-r445/)", "a stable release"; also headline: first **native Apple Silicon macOS build**). The [ModDB game-versions API](https://mods.vintagestory.at/api/gameversions) lists 1.22.3 as the newest tag.
- **Runtime: .NET 10.** "You'll need to install the .NET 10 SDK which includes the runtime needed since version 1.22.0" ([wiki: Setting up your Development Environment](https://wiki.vintagestory.at/Modding:Setting_up_your_Development_Environment)). Farseer's csproj targets `net10.0`.
- **Graphics: "OpenGL 3.3 or newer" on all requirement tiers** ([system requirements](https://www.vintagestory.at/sysrequirements/)). More in §3.6.

## 1. Mod structure (client-side-only code mod)

### 1.1 modinfo.json

Documented on the [Modinfo wiki page](https://wiki.vintagestory.at/Modinfo) and in the [`ModInfo` class docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ModInfo.html):

- `type`: `"code"` | `"content"` | `"theme"` - a code mod ships compiled DLLs.
- `modid`: lowercase letters/digits only; `name`, `version` (SemVer), `authors`, `description`, `website`, `iconPath`, `dependencies` (e.g. `{ "game": "1.22.0" }`).
- **`side`**: doc string verbatim - *"Which side(s) this mod runs on. Can be 'Server', 'Client' or 'Universal'. (Optional. Universal (both server and client) by default.)"*
- **`requiredOnClient`** (default `true`): *"If set to false and the mod is universal, clients don't need the mod to join."* - i.e. it only matters for `Universal` mods.
- **`requiredOnServer`** (default `true`): *"If set to false and the mod is universal, the mod is not disabled if it's not present on the server."*
- **`networkVersion`**: *"Change this number when a user that has an older version of your mod should not be allowed to connect to a server with a newer version."*

For a pure client mod use `"side": "Client"` - then the requiredOn\* flags are irrelevant. Example skeleton:

```json
{
  "type": "code",
  "modid": "vintagehorizons",
  "name": "Vintage Horizons",
  "version": "0.1.0",
  "side": "Client",
  "dependencies": { "game": "1.22.0" }
}
```

(For contrast, [Farseer's modinfo.json](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/modinfo.json) is `"side": "Universal", "requiredOnClient": true, "requiredOnServer": true` because it needs its server half.)

### 1.2 ModSystem lifecycle

From [`ModSystem` source](https://github.com/anegostudios/vsapi/blob/master/Common/API/ModSystem.cs) / [API docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ModSystem.html), the overridable members in call order:

```csharp
public virtual bool ShouldLoad(ICoreAPI api)        // default: ShouldLoad(api.Side)
public virtual bool ShouldLoad(EnumAppSide forSide) // return forSide == EnumAppSide.Client for client-only systems
public virtual double ExecuteOrder()                // ordering among mod systems, default 0.1
public virtual void StartPre(ICoreAPI api)
public virtual void Start(ICoreAPI api)             // both sides, before assets load
public virtual void AssetsLoaded(ICoreAPI api)
public virtual void AssetsFinalize(ICoreAPI api)
public virtual void StartClientSide(ICoreClientAPI api)  // client only - register renderers/UI here
public virtual void StartServerSide(ICoreServerAPI api)  // server only
public virtual void Dispose()
```

Per the wiki tutorial docs, `Start()` runs on both sides before blocks/items/recipes load, and one ModSystem instance is created per server plus one per client, so side-specific work belongs in `StartClientSide`/`StartServerSide` ([wiki: Code Tutorial Essentials](https://wiki.vintagestory.at/index.php/Modding:Code_Tutorial_Essentials), [wiki: Server-Client Considerations](https://wiki.vintagestory.at/Modding:Server-Client_Considerations)).

### 1.3 Can a client-only mod join a vanilla server? Yes.

- Mod verification is one-directional: the server requires clients to have its `Universal`/`Server`-distributed mods (unless `requiredOnClient: false`), and auto-downloads them into a per-server `ModsByServer` folder ([forum: Client, Server and "Both" mods](https://www.vintagestory.at/forums/topic/16149-client-server-and-both-mods-manual-downloads/)). The vanilla server does **not** check or restrict what client-only mods a player runs.
- Direct evidence: the [ModIntegrity mod](https://github.com/chriswa/vsmod-ModIntegrity) exists precisely to add that missing restriction - *"Vintage Story mod which helps ensure that the client and server use the same mods, **including client-only mods**"*. That it must be installed to enforce this confirms the vanilla default is permissive.
- Practical corroboration: the huge catalog of client-side graphics mods on the ModDB (e.g. [Volumetric Shading Refreshed](https://mods.vintagestory.at/volumetricshadingrefreshed)) that are used on public servers.

**Design consequence:** a Distant-Horizons-style mod with `"side": "Client"` can join unmodded servers - but it can only see chunk data the server streams within the approved view distance, so it must build its own persistent LOD cache from chunks as they arrive (exactly like Distant Horizons on Minecraft servers).

## 2. Client chunk data access

### 2.1 Chunk arrival notification

Client event API surface: [`IClientEventAPI`](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs) (extends [`IEventAPI`](https://github.com/anegostudios/vsapi/blob/master/Common/API/IEventAPI.cs)), reachable via `capi.Event` ([docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IClientEventAPI.html)).

- **`event ChunkDirtyDelegate ChunkDirty`** with `public delegate void ChunkDirtyDelegate(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)` and `enum EnumChunkDirtyReason { NewlyCreated, NewlyLoaded, MarkedDirty }` ([IEventAPI.cs L31/L128](https://github.com/anegostudios/vsapi/blob/master/Common/API/IEventAPI.cs)). Doc: *"Called whenever a chunk was marked dirty (as in, its blocks or light values have been modified or it got newly loaded or newly created)."*
- **Confirmed client-side behavior** (decompiled 1.20.7 [`ClientWorldMap.loadChunkMT`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/NoObf/ClientWorldMap.cs)): when a `Packet_ServerChunk` arrives, the client stores the chunk then fires `game.api.eventapi.TriggerChunkDirty(vec, chunk, EnumChunkDirtyReason.NewlyLoaded)`. So **`capi.Event.ChunkDirty` with reason `NewlyLoaded` is the "chunk arrived" hook on the client.** Real mods use exactly this (e.g. [TrailMod's TrailChunkManager](https://github.com/Grifthegnome/OutlawMod/blob/master/mods-dll/trailmod/src/TrailChunkManager.cs)).
- Other useful client events (all in [IClientEventAPI.cs](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs)): `event Action BlockTexturesLoaded` (*"Fired when server assets were received and all texture atlases have been created"* - good init point), `event Action LevelFinalize` (client received level-finalize packet; world/player ready), `event ActionBoolReturn ReloadShader`, `event BlockChangedDelegate BlockChanged`, `event Action LeaveWorld / LeftWorld`, `MapRegionLoaded/MapRegionUnloaded`.
- **There is no client-side chunk-unloaded event.** `IClientEventAPI` has none, and the decompiled unload path ([`SystemUnloadChunks.HandleChunkUnload`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/NoObf/SystemUnloadChunks.cs)) shows unloading is driven by server packet id 11: the client calls `clientchunk.Dispose()`, removes it from `WorldMap.chunks`, and drops the column's `MapChunks` entry - firing only `BlockEntity.OnBlockUnloaded()`, no public event. Detect unloads by checking `IWorldChunk.Disposed` (*"Whether this chunk got unloaded"*, [IWorldChunk docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldChunk.html)) or by diffing `IWorldAccessor.LoadedChunkIndices` / `LoadedMapChunkIndices` ([IWorldAccessor.cs L65–70](https://github.com/anegostudios/vsapi/blob/master/Common/API/IWorldAccessor.cs)). **For an LOD mod this is a feature:** snapshot the chunk into your LOD store on `NewlyLoaded`/`MarkedDirty` and simply keep rendering after the real chunk unloads.

### 2.2 Reading block data client-side

- Get chunks via `capi.World.BlockAccessor`: `IWorldChunk GetChunk(int chunkX, int chunkY, int chunkZ)`, `GetChunk(long chunkIndex3D)`, `GetChunkAtBlockPos(BlockPos)`, `IMapChunk GetMapChunk(int chunkX, int chunkZ)` ([IBlockAccessor.cs L222–711](https://github.com/anegostudios/vsapi/blob/master/Common/API/IBlockAccessor.cs)) - or directly from the `ChunkDirty` callback argument.
- [`IWorldChunk`](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldChunk.html): `IChunkBlocks Data` (*"Holds all the blockids for each coordinate, access via index: (y * chunksize + z) * chunksize + x"*), `IChunkBlocks MaybeBlocks` (faster, non-blocking read-only), `void Unpack()` / `bool Unpack_ReadOnly()` (chunks are kept compressed in RAM; unpack before reading), `int UnpackAndReadBlock(int index, int layer)`, `Block GetLocalBlockAtBlockPos(IWorldAccessor, BlockPos)`, `Dictionary<BlockPos, BlockEntity> BlockEntities`, `bool Disposed`, `bool Empty`, `IMapChunk MapChunk`. Chunk edge length is **32** (`GlobalConstants.ChunkSize = 32`, [GlobalConstants.cs L39](https://github.com/anegostudios/vsapi/blob/master/Config/GlobalConstants.cs)).

### 2.3 Heightmaps on the client - yes, with caveats

[`IMapChunk`](https://github.com/anegostudios/vsapi/blob/master/Common/API/IMapChunk.cs) ([docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IMapChunk.html)) per chunk column:

- `ushort[] RainHeightMap` - *"The position of the last block that is not rain permeable before the first airblock"* (32×32 entries).
- `ushort[] WorldGenTerrainHeightMap` - *"The position of the last block before the first airblock before world gen pass Vegetation. For oceans/sealevel lakes this will be seafloor position."*
- `int[] TopRockIdMap`, `ushort YMax` (*"The highest position of any non-air block"*).

**Client availability confirmed** by decompiled [`ClientMapChunk`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/ClientMapChunk.cs): `UpdateFromPacket(Packet_ServerMapChunk)` deserializes `RainHeightMap`, `TerrainHeightMap` and `Ymax` - so **the client receives rain + worldgen-terrain heightmaps and YMax for every loaded column**. However, client-side `TopRockIdMap => null`, `SnowAccum => null`, `MapRegion => null`, and `Get/SetModdata` throw `NotImplementedException`. So surface *height* is free on the client; surface *color/material* must be derived by reading the actual surface blocks (`RainHeightMap[z*32+x]` gives you the Y to sample in the right chunk of the column - this is how you avoid scanning whole columns).

### 2.4 View distance and chunk streaming limits

- The client requests a view distance; the server approves it: `IWorldPlayerData.DesiredViewDistance` (*"The players desired viewing distance in blocks"*) and `LastApprovedViewDistance` (*"The players viewing distance in blocks that is allowed by the server"*) ([IWorldPlayerData.cs L26–35](https://github.com/anegostudios/vsapi/blob/master/Common/Entity/Player/IWorldPlayerData.cs)); accessed as `capi.World.Player.WorldData.DesiredViewDistance` (used by Farseer's renderer for the fade-in radius).
- Server cap: `/serverconfig maxchunkradius [int]` - *"the highest chunk radius a player may load"* ([wiki: serverconfig commands](https://wiki.vintagestory.at/List_of_server_commands/serverconfig)); hosting guides cite a default of 12 chunks = 384 blocks ([BisectHosting guide](https://help.bisecthosting.com/hc/en-us/articles/42433637094939-How-to-Change-the-Max-View-Distance-on-a-Vintage-Story-Server)).
- Client side, `.viewdistance [number]` *"sets the viewing distance, no limit (unlike the limit in graphics settings)"* ([wiki: client commands](https://wiki.vintagestory.at/List_of_client_commands)); in single player the integrated server honors large values, on multiplayer you're clamped by `maxchunkradius`.
- Unloading: server-driven (packet 11), as in §2.1; there is no client-side "keep chunks" option, which again motivates an own LOD cache (Farseer/ChunkLOD instead solve it server-side with a SQLite heightmap DB).

## 3. Custom rendering

### 3.1 IRenderer + RegisterRenderer

[`IRenderer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderer.html):

```csharp
double RenderOrder { get; }   // 0 = drawn first, 1 = last; terrain opaque ≈ 0.37, entities 0.4
int RenderRange { get; }      // "currently not used!"
void OnRenderFrame(float deltaTime, EnumRenderStage stage);
void Dispose();
```

Registration ([IClientEventAPI.cs L211, L223](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs)):

```csharp
void RegisterRenderer(IRenderer renderer, EnumRenderStage renderStage, string profilingName = null);
void RegisterRenderer(IRenderer renderer, EnumRenderStage renderStage, string profilingName,
                      double reservedFirstOrder, double reservedLastOrder, Type firstType);
```

Farseer registers at `EnumRenderStage.Opaque` with `RenderOrder => 0.36` so distant terrain draws just *before* real terrain and gets depth-occluded by it ([FarRegionRenderer.cs](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Client/FarRegionRenderer.cs)).

### 3.2 Render stages

[`EnumRenderStage`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.EnumRenderStage.html): `Before(0)` - *"Before any rendering has begun"*; `Opaque(1)` - *"Opaque/Alpha tested rendering"*; `OIT(2)` - *"Order independent transparency"*; `AfterOIT(3)`; `ShadowFar(4)/ShadowFarDone(5)/ShadowNear(6)/ShadowNearDone(7)` - shadow map passes; `AfterPostProcessing(8)`; `AfterBlit(9)`; `Ortho(10)` - 2D GUI; `AfterFinalComposition(11)`; `Done(12)`. High-level overview: [wiki: Rendering API](https://wiki.vintagestory.at/index.php/Modding:Rendering_API) (admits "a thorough tutorial is still missing" and links sample repos, incl. [vsmodexamples](https://github.com/anegostudios/vsmodexamples)).

### 3.3 Custom GLSL shaders

[`IShaderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IShaderAPI.html) (`capi.Shader`): `IShaderProgram NewShaderProgram()`, `IShader NewShader(EnumShaderType)`, `int RegisterFileShaderProgram(string name, IShaderProgram program)` (loads `name.vsh`/`name.fsh` from the mod's `assets/<domain>/shaders/` folder), `RegisterMemoryShaderProgram`, `GetProgramByName(string)`, `IsGLSLVersionSupported(string)`, `bool ReloadShaders()`.

[`IShaderProgram`](https://github.com/anegostudios/vsapi/blob/master/Client/Render/IShaderProgram.cs): `Use() / Stop() / bool Compile()`, `Uniform(string, float|int|Vec2f|Vec3f|Vec4f|…)`, `UniformMatrix(string, float[])`, `UniformMatrices`, `BindTexture2D(string samplerName, int textureId, int textureNumber)`, `HasUniform(string)`, `string PrefixCode`, `string AssetDomain`, `bool LoadError`.

The canonical hot-reload-friendly pattern (Farseer [FarRegionRenderer.LoadShader](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Client/FarRegionRenderer.cs)):

```csharp
capi.Event.ReloadShader += LoadShader;  // fired when graphics settings change
LoadShader();

public bool LoadShader() {
    prog = capi.Shader.NewShaderProgram();
    prog.AssetDomain = "farseer";
    prog.VertexShader   = capi.Shader.NewShader(EnumShaderType.VertexShader);
    prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
    capi.Shader.RegisterFileShaderProgram("region", prog);
    return prog.Compile();
}
```

Shaders are plain GLSL - Farseer's [region.vsh](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/assets/farseer/shaders/region.vsh) is `#version 330 core` and can `#include` the game's stock shader includes (`vertexflagbits.ash`, `colorutil.ash`, `shadowcoords.vsh`, `fogandlight.vsh` - giving `getFogLevel()`, `vertexwarp.vsh` - giving `applyGlobalWarping()`), which is how you match vanilla fog/lighting exactly.

### 3.4 Meshes

[`MeshData`](https://github.com/anegostudios/vsapi/blob/master/Client/Model/Mesh/MeshData.cs): raw arrays `float[] xyz`, `float[] Uv`, `byte[] Rgba`, `int[] Flags`, `int[] Indices`, plus `CustomMeshDataPartFloat CustomFloats` for custom vertex attributes; ctors `MeshData(bool initialiseArrays = true)` and `MeshData(int capacityVertices, int capacityIndices, bool withNormals = false, bool withUv = true, bool withRgba = true, bool withFlags = true)`; `SetVerticesCount(int)` / `SetIndicesCount(int)`.

[`IRenderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html) (`capi.Render`):

```csharp
MeshRef UploadMesh(MeshData data);        // "load into a VAO"
void UpdateMesh(MeshRef meshRef, MeshData updatedata);
void RenderMesh(MeshRef meshRef);
void RenderMesh(MeshRef meshRef, int[] indicesStarts, int[] indicesSizes, int groupCount);
void RenderMeshInstanced(MeshRef meshRef, int quantity = 1);
void DeleteMesh(MeshRef vao);
```

`MeshRef` is an opaque VAO handle, `Dispose()` frees GPU memory ([MeshRef.cs](https://github.com/anegostudios/vsapi/blob/master/Client/API/MeshRef.cs)). Farseer builds one heightmap grid mesh per region ((gridSize+1)² vertices, position-only, edge-stitched with neighbor regions), pools the CPU arrays with `ArrayPool<T>`, uploads once, then draws each region with a per-region model matrix.

### 3.5 Camera matrices, fog uniforms, projection

- Matrices on `IRenderAPI`: `float[] CurrentModelviewMatrix`, `float[] CurrentProjectionMatrix`, `double[] CameraMatrixOrigin` / `float[] CameraMatrixOriginf` (camera matrix with the translation at origin - for camera-relative rendering to avoid float precision loss), `StackMatrix4 MvMatrix / PMatrix` ([docs](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html)). Farseer does `modelMat.Identity().Translate(regionPos).Translate(-camPos)` with `Vec3d camPos = capi.World.Player.Entity.CameraPos` and passes `viewMatrix = rapi.CameraMatrixOriginf`, `projectionMatrix = rapi.CurrentProjectionMatrix`.
- Fog/ambient: `capi.Ambient` ([`IAmbientManager`](https://github.com/anegostudios/vsapi/blob/master/Client/API/IAmbientManager.cs)): `Vec4f BlendedFogColor`, `float BlendedFogDensity`, `float BlendedFogMin`, `float BlendedCloudDensity`; also `IRenderAPI.FogColor/FogDensity/FogMin` and the big `DefaultShaderUniforms ShaderUniforms` bag (`ZNear`, `ZFar`, `SunPosition3D`, `PointLights3`, `FogSpheres`, `FlatFogDensity`, `FlatFogStartYPos`, shadow matrices `ToShadowMapSpaceMatrixFar/Near`, …) ([DefaultShaderUniforms.cs](https://github.com/anegostudios/vsapi/blob/master/Client/Render/DefaultShaderUniforms.cs)). Sun/time: `capi.World.Calendar.SunPositionNormalized`, `.SunColor`, `.DayLightStrength`.
- Projection/zFar: `void Set3DProjection(float zfar, float fov)` and `void Reset3DProjection()` on IRenderAPI. **The default zFar clips distant LOD terrain**, so Farseer reaches into engine internals: `((ClientMain)capi.World).MainCamera.ZFar = max(3000, farViewDistance); capi.Render.Reset3DProjection();` - `ClientMain` lives in `Vintagestory.Client.NoObf` inside **VintagestoryLib.dll**, which mods may reference (Farseer's csproj does). This is unofficial API but standard practice.
- Vanilla near-terrain LOD: chunk tessellation supports per-mesh `lodLevel` 0–3 via `ITerrainMeshPool.AddMeshData`, culled by `FrustumCulling.InFrustumAndRange` ([wiki: Modding:Level of detail](https://wiki.vintagestory.at/index.php/Modding:Level_of_detail)) - that's model-detail LOD within normal view distance, not extended draw distance.

### 3.6 OpenGL version - compute shaders / MDI are NOT safe to require

- Official requirement is **OpenGL 3.3+** ([system requirements](https://www.vintagestory.at/sysrequirements/)); the engine *requests a 3.3 context by default* and throws "OpenGL version 330 is required" below that ([VintageStory-Issues #850](https://github.com/anegostudios/VintageStory-Issues/issues/850), [#643](https://github.com/anegostudios/VintageStory-Issues/issues/643)). Users/mods can set `glContextVersion` (e.g. `"4.2"`) in `clientsettings.json`; note some drivers hand back a 4.6 compatibility profile anyway ([#2454](https://github.com/anegostudios/VintageStory-Issues/issues/2454)).
- Game shaders are GLSL 330 (`#version 330 core` in stock includes / Farseer's shader). The renderer is built on **OpenTK** - decompiled VintagestoryLib imports `OpenTK.Graphics.OpenGL` throughout (e.g. [UBO.cs, ClientPlatformWindows.cs](https://github.com/Primer81/vintage-story-mods/tree/master/references/1.20.7/DecompiledSource/VintagestoryLib)) - and mods run in-process, so you *can* issue raw GL calls via the game's OpenTK assembly (e.g. for `glMultiDrawElementsIndirect`).
- **Practical ceiling:** compute shaders and MDI are GL 4.3. VS officially supports macOS (now Apple Silicon native as of [1.22.3](https://www.vintagestory.at/blog.html/news/1223-maintenance-patch-r445/)), and Apple caps OpenGL at 4.1 - so any GL 4.3 path must be optional with a 3.3 fallback (`capi.Shader.IsGLSLVersionSupported` + GL extension query). Design the core renderer for GL 3.3: static VAOs per LOD cell + one draw call each (Farseer's approach), or manual batching; instancing (`RenderMeshInstanced`, GL 3.3) is available.

## 4. Existing art

### 4.1 Farseer - open source, MIT - primary reference

- ModDB: [mods.vintagestory.at/show/mod/22371](https://mods.vintagestory.at/show/mod/22371) - v1.4.0 (Apr 2026, game 1.22.0), author badgerson. Source: [github.com/ViciousBadger/VSMod-Farseer](https://github.com/ViciousBadger/VSMod-Farseer), **MIT license** (LICENSE.md, © 2026 Badgerson) - client rendering code is directly reusable with attribution.
- Architecture (from source): **server side** (`Server/FarRegionGen.cs`, `FarRegionDB.cs`, `FarRegionProvider.cs`) builds one 2D heightmap per "far region" from map-chunk heightmap data, persists in SQLite, and streams `FarRegionData` messages over a named network channel (`api.Network.RegisterChannel("farseer").RegisterMessageType<...>()`, see [FarseerModSystem.cs](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/FarseerModSystem.cs); protocol in `Protocol.cs`, protobuf-net). **Client side** (`Client/FarRegionRenderer.cs`) is ~300 lines: heightmap grid mesh + neighbor-edge stitching + deferred dirty-region rebuilds, `UploadMesh`, one `RenderMesh` per region in `Opaque` stage, GLSL-330 shader that pushes far terrain down near the transition ring, applies a globe-curvature term, and uses vanilla fog/sun uniforms. Requires the mod on the server (~4000 blocks default; server caps clients via world config `capi.World.Config.GetInt("maxFarViewDistance")`).
- Its 1.4.0 release integrates "Algernon's Terrain Sampler Lib" for much faster server-side heightmap generation ([mod page](https://mods.vintagestory.at/show/mod/22371)).

### 4.2 ChunkLOD - closed source

- ModDB: [mods.vintagestory.at/chunklod](https://mods.vintagestory.at/chunklod), author BiasHyperion. **No public repo**: `sourcecodeurl` is empty in the [ModDB API record](https://mods.vintagestory.at/api/mod/chunklod), no license stated → treat as all-rights-reserved; study behavior, don't copy code.
- Status (ModDB API, July 2026): stable 1.1.0; test builds 1.2.0-dev.1..3 (latest July 11, 2026) for game 1.22.0–1.22.3; "major overhaul underway"; 1.21.6 backport at [mods.vintagestory.at/chunklodold](https://mods.vintagestory.at/chunklodold).
- Design (mod description): server-required; per-world SQLite DB of 1:1 chunk heightmaps + coloring data (~10 MB for 4k radius); client renders colored, lit heightmap terrain (base color from rock id, green shading by forestation, blue for water, seasonal tinting), multiple grid resolutions incl. distance-adaptive "Mixed" mode, optional face lighting and globe curvature, view distance to 16,384 (dev builds pushed to 65,000); shader is *"a heavily modified instance"* of Farseer's (credited). Known pain points its changelogs flag: z-fighting at LOD seams, fog propagation, water at sea level vs the fog system, microblocks/chiseled blocks, Qualcomm iGPU face rendering.

### 4.3 Vanilla engine

No vanilla distant-terrain LOD exists as of 1.22.x - nothing in the [1.21 release notes](https://www.vintagestory.at/blog.html/news/v1210-story-chapter-2-redux-stable-r420/) or the [1.22 feature page](https://info.vintagestory.at/v1dot22); community answers to "[Are there any plans for LOD?](https://www.vintagestory.at/forums/topic/11348-are-there-any-plans-for-level-of-detail-lod/)" report no committed plans. The only built-in LOD is the per-mesh `lodLevel` 0–3 system in chunk tessellation ([wiki](https://wiki.vintagestory.at/index.php/Modding:Level_of_detail)).

## 5. Dev setup

- **Template**: `dotnet new install VintageStory.Mod.Templates` then `dotnet new vsmod --AddSolutionFile -o mymod` ([wiki: Setting up your Development Environment](https://wiki.vintagestory.at/Modding:Setting_up_your_Development_Environment)); template source at [github.com/anegostudios/vsmodtemplate](https://github.com/anegostudios/vsmodtemplate).
- **`VINTAGE_STORY` env var** points at the game install; Linux: `export VINTAGE_STORY="$HOME/ApplicationData/vintagestory"` in `~/.bashrc` (wiki, same page). csproj pattern (from [Farseer.csproj](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Farseer.csproj)): `<TargetFramework>net10.0</TargetFramework>`, `<GamePath Condition="Exists('$(VINTAGE_STORY)')">$(VINTAGE_STORY)</GamePath>`, then `<Reference Include="VintagestoryAPI"><HintPath>$(GamePath)\VintagestoryAPI.dll</HintPath><Private>false</Private></Reference>` plus as needed `VintagestoryLib.dll` (for `Vintagestory.Client.NoObf` internals like `ClientMain.MainCamera`), `Mods/VSSurvivalMod.dll`, `Mods/VSEssentials.dll`, `Lib/protobuf-net.dll`, `Lib/0Harmony.dll` - all `Private=false` (never copy game DLLs into the mod zip).
- **Run/debug on Linux** (Farseer [launchSettings.json](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Properties/launchSettings.json) - works in Rider/VS Code):

```json
"Client": {
  "commandName": "Executable",
  "executablePath": "dotnet",
  "commandLineArgs": "\"$(VINTAGE_STORY)/Vintagestory.dll\" --tracelog --addModPath \"$(ProjectDir)/bin/$(Configuration)/Mods\" --addOrigin \"$(ProjectDir)/assets\"",
  "workingDirectory": "$(VINTAGE_STORY)"
}
```

  `--addModPath` loads your build output as a mod folder; `--addOrigin` mounts your assets dir so shader/lang edits hot-reload without repackaging; the vsmodtemplate README notes Linux/Mac must use the extension-less/dll launch form ([vsmodtemplate](https://github.com/anegostudios/vsmodtemplate)). Shaders can be hot-reloaded in-session (graphics-settings change fires `ReloadShader`; `IShaderAPI.ReloadShaders()` recompiles all).

## Key design takeaways for a client-only "Distant Horizons for VS"

1. `"side": "Client"` mod joins any vanilla server (§1.3); it must **self-harvest** terrain: subscribe `capi.Event.ChunkDirty` (`NewlyLoaded`/`MarkedDirty`), read `chunk.MapChunk.RainHeightMap`/`WorldGenTerrainHeightMap` (synced, §2.3) for height, sample surface blocks for color (TopRockIdMap is null client-side), and persist to a local per-world/per-server SQLite cache - this is the client-side analog of what Farseer/ChunkLOD do on the server, and it's why they chose server-side (instant full coverage vs. gradual exploration-based buildup like Distant Horizons).
2. Render via `IRenderer` at `EnumRenderStage.Opaque` with `RenderOrder ≈ 0.36`, camera-relative model matrices + `CameraMatrixOriginf`, custom GLSL-330 program including vanilla `fogandlight.vsh`/`vertexwarp.vsh`, uniforms from `capi.Ambient.Blended*` and `capi.World.Calendar` - Farseer's MIT renderer + shader is a drop-in starting skeleton (§3, §4.1).
3. Extend `ZFar` via `Vintagestory.Client.NoObf.ClientMain.MainCamera.ZFar` + `Reset3DProjection()` (VintagestoryLib reference required) (§3.5).
4. Budget for GL 3.3 core (macOS ceiling 4.1); treat compute/MDI as optional fast paths only (§3.6).
5. Target game 1.22.x / .NET 10; latest stable 1.22.3 (May 30, 2026) (§0).
