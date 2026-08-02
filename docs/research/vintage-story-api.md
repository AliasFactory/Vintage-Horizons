# Vintage Story modding API research

This research is for a client-side LOD mod that increases the render distance. It is
current as of 2026-07-14.

The findings were examined against the official API documentation (apidocs.vintagestory.at),
the [vsapi source](https://github.com/anegostudios/vsapi), the [Farseer mod
source](https://github.com/ViciousBadger/VSMod-Farseer), and the decompiled VintagestoryLib
1.20.7 sources at
[Primer81/vintage-story-mods](https://github.com/Primer81/vintage-story-mods/tree/master/references/1.20.7/DecompiledSource/VintagestoryLib).

## 0. Game version and runtime (July 2026)

The most recent stable version is **Vintage Story 1.22.3**, released on May 30, 2026. The
release notes call it "[1.22.3 - Maintenance patch](https://www.vintagestory.at/blog.html/news/1223-maintenance-patch-r445/)"
and "a stable release". The headline is the first **native Apple Silicon macOS build**. The
[ModDB game-versions API](https://mods.vintagestory.at/api/gameversions) gives 1.22.3 as the
newest tag.

The runtime is **.NET 10**. The wiki says: "You'll need to install the .NET 10 SDK which
includes the runtime needed since version 1.22.0"
([wiki: Setting up your Development Environment](https://wiki.vintagestory.at/Modding:Setting_up_your_Development_Environment)).
The csproj of Farseer targets `net10.0`.

The graphics requirement is "OpenGL 3.3 or newer" at all requirement levels
([system requirements](https://www.vintagestory.at/sysrequirements/)). Section 3.6 gives
more.

## 1. Mod structure for a client-side-only code mod

### 1.1 modinfo.json

The [Modinfo wiki page](https://wiki.vintagestory.at/Modinfo) and the
[`ModInfo` class documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ModInfo.html)
give these fields.

- `type` is `"code"`, `"content"` or `"theme"`. A code mod ships compiled DLLs.
- `modid` holds lowercase letters and digits only. The other fields are `name`, `version`
  (SemVer), `authors`, `description`, `website`, `iconPath` and `dependencies`. One example
  of `dependencies` is `{ "game": "1.22.0" }`.
- **`side`** has this documentation: *"Which side(s) this mod runs on. Can be 'Server',
  'Client' or 'Universal'. (Optional. Universal (both server and client) by default.)"*
- **`requiredOnClient`** has the default `true`. Its documentation is: *"If set to false and
  the mod is universal, clients don't need the mod to join."* Thus it applies to a
  `Universal` mod only.
- **`requiredOnServer`** has the default `true`. Its documentation is: *"If set to false and
  the mod is universal, the mod is not disabled if it's not present on the server."*
- **`networkVersion`** has this documentation: *"Change this number when a user that has an
  older version of your mod should not be allowed to connect to a server with a newer
  version."*

For a client-only mod, use `"side": "Client"`. Then the two `requiredOn` flags have no
effect. This is an example:

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

For a comparison, the [modinfo.json of Farseer](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/modinfo.json)
has `"side": "Universal", "requiredOnClient": true, "requiredOnServer": true`, because
Farseer needs its server half.

### 1.2 ModSystem lifecycle

The [`ModSystem` source](https://github.com/anegostudios/vsapi/blob/master/Common/API/ModSystem.cs)
and the [API documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.ModSystem.html)
give these members. They are in the order of the calls.

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

The wiki tutorials say that `Start()` runs on both sides, before the blocks, items and
recipes load. The game makes one ModSystem instance for each server and one for each client.
Thus work for one side belongs in `StartClientSide` or `StartServerSide`. Read
[wiki: Code Tutorial Essentials](https://wiki.vintagestory.at/index.php/Modding:Code_Tutorial_Essentials)
and [wiki: Server-Client Considerations](https://wiki.vintagestory.at/Modding:Server-Client_Considerations).

### 1.3 A client-only mod can join a vanilla server

Mod verification goes in one direction. The server requires each client to have its
`Universal` and `Server` mods, unless `requiredOnClient` is false. It downloads them
automatically into a `ModsByServer` folder for that server. Read
[forum: Client, Server and "Both" mods](https://www.vintagestory.at/forums/topic/16149-client-server-and-both-mods-manual-downloads/).
A vanilla server does **not** examine or limit the client-only mods of a player.

There is direct evidence. The [ModIntegrity mod](https://github.com/chriswa/vsmod-ModIntegrity)
exists to add that missing limit. Its description is: *"Vintage Story mod which helps ensure
that the client and server use the same mods, **including client-only mods**"*. An admin must
install it to get this behaviour. Thus the vanilla default permits it.

There is practical evidence also. The ModDB holds a large catalog of client-side graphics
mods that players use on public servers. One example is
[Volumetric Shading Refreshed](https://mods.vintagestory.at/volumetricshadingrefreshed).

**Consequence for the design.** A Distant Horizons-style mod with `"side": "Client"` can join
a server that has no mods. But it can see only the chunk data that the server streams inside
the approved view distance. Thus it must build its own LOD cache from the chunks as they
arrive. This is exactly what Distant Horizons does on Minecraft servers.

## 2. Client chunk data access

### 2.1 Notification when a chunk arrives

The client event API is
[`IClientEventAPI`](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs),
which extends
[`IEventAPI`](https://github.com/anegostudios/vsapi/blob/master/Common/API/IEventAPI.cs).
`capi.Event` reaches it. Read the
[documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IClientEventAPI.html).

**`event ChunkDirtyDelegate ChunkDirty`** has the signature
`public delegate void ChunkDirtyDelegate(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)`.
The reasons are `enum EnumChunkDirtyReason { NewlyCreated, NewlyLoaded, MarkedDirty }`. Read
[IEventAPI.cs L31/L128](https://github.com/anegostudios/vsapi/blob/master/Common/API/IEventAPI.cs).
Its documentation is: *"Called whenever a chunk was marked dirty (as in, its blocks or light
values have been modified or it got newly loaded or newly created)."*

The behaviour on the client is confirmed. The decompiled 1.20.7
[`ClientWorldMap.loadChunkMT`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/NoObf/ClientWorldMap.cs)
shows that a `Packet_ServerChunk` arrives, the client stores the chunk, and then it fires
`game.api.eventapi.TriggerChunkDirty(vec, chunk, EnumChunkDirtyReason.NewlyLoaded)`. Thus
**`capi.Event.ChunkDirty` with the reason `NewlyLoaded` is the hook for "a chunk arrived" on
the client.** Real mods use exactly this, for example
[TrailChunkManager of TrailMod](https://github.com/Grifthegnome/OutlawMod/blob/master/mods-dll/trailmod/src/TrailChunkManager.cs).

These other client events are useful. They are all in
[IClientEventAPI.cs](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs).
`event Action BlockTexturesLoaded` has the documentation *"Fired when server assets were
received and all texture atlases have been created"*, which makes it a good point for
initialization. `event Action LevelFinalize` occurs when the client receives the
level-finalize packet, and then the world and the player are ready. The others are
`event ActionBoolReturn ReloadShader`, `event BlockChangedDelegate BlockChanged`,
`event Action LeaveWorld / LeftWorld`, and `MapRegionLoaded` with `MapRegionUnloaded`.

**There is no chunk-unloaded event on the client.** `IClientEventAPI` has none. The
decompiled unload path,
[`SystemUnloadChunks.HandleChunkUnload`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/NoObf/SystemUnloadChunks.cs),
shows that server packet id 11 drives the unload. The client calls `clientchunk.Dispose()`,
removes the chunk from `WorldMap.chunks`, and drops the `MapChunks` entry of the column. It
fires `BlockEntity.OnBlockUnloaded()` only, and no public event.

To find an unload, examine `IWorldChunk.Disposed`, which has the documentation *"Whether this
chunk got unloaded"*
([IWorldChunk documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldChunk.html)).
Alternatively, compare `IWorldAccessor.LoadedChunkIndices` or `LoadedMapChunkIndices` between
frames
([IWorldAccessor.cs L65-70](https://github.com/anegostudios/vsapi/blob/master/Common/API/IWorldAccessor.cs)).

**For an LOD mod this absence is an advantage.** Make a snapshot of the chunk into the LOD
store at `NewlyLoaded` or `MarkedDirty`. Then continue to draw after the real chunk unloads.

### 2.2 Reading block data on the client

Get a chunk through `capi.World.BlockAccessor`. The methods are
`IWorldChunk GetChunk(int chunkX, int chunkY, int chunkZ)`, `GetChunk(long chunkIndex3D)`,
`GetChunkAtBlockPos(BlockPos)` and `IMapChunk GetMapChunk(int chunkX, int chunkZ)`
([IBlockAccessor.cs L222-711](https://github.com/anegostudios/vsapi/blob/master/Common/API/IBlockAccessor.cs)).
The argument of the `ChunkDirty` callback also gives the chunk directly.

[`IWorldChunk`](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldChunk.html)
has these members. `IChunkBlocks Data` has the documentation *"Holds all the blockids for
each coordinate, access via index: (y * chunksize + z) * chunksize + x"*. `IChunkBlocks
MaybeBlocks` is a faster read-only access that does not block. `void Unpack()` and
`bool Unpack_ReadOnly()` are necessary before a read, because the RAM holds the chunks
compressed. The others are `int UnpackAndReadBlock(int index, int layer)`,
`Block GetLocalBlockAtBlockPos(IWorldAccessor, BlockPos)`,
`Dictionary<BlockPos, BlockEntity> BlockEntities`, `bool Disposed`, `bool Empty` and
`IMapChunk MapChunk`.

The edge length of a chunk is **32**. It is `GlobalConstants.ChunkSize = 32`
([GlobalConstants.cs L39](https://github.com/anegostudios/vsapi/blob/master/Config/GlobalConstants.cs)).

### 2.3 Heightmaps on the client

[`IMapChunk`](https://github.com/anegostudios/vsapi/blob/master/Common/API/IMapChunk.cs)
([documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IMapChunk.html))
gives these for each chunk column.

- `ushort[] RainHeightMap` has the documentation *"The position of the last block that is not
  rain permeable before the first airblock"*. It has 32 x 32 entries.
- `ushort[] WorldGenTerrainHeightMap` has the documentation *"The position of the last block
  before the first airblock before world gen pass Vegetation. For oceans/sealevel lakes this
  will be seafloor position."*
- `int[] TopRockIdMap` and `ushort YMax`, where `YMax` has the documentation *"The highest
  position of any non-air block"*.

**The client availability is confirmed** by the decompiled
[`ClientMapChunk`](https://github.com/Primer81/vintage-story-mods/blob/master/references/1.20.7/DecompiledSource/VintagestoryLib/Vintagestory/Client/ClientMapChunk.cs).
`UpdateFromPacket(Packet_ServerMapChunk)` deserializes `RainHeightMap`, `TerrainHeightMap`
and `Ymax`. Thus **the client receives the rain heightmap, the worldgen-terrain heightmap and
YMax for each loaded column**.

But on the client, `TopRockIdMap`, `SnowAccum` and `MapRegion` are all null, and
`Get/SetModdata` throw a `NotImplementedException`. Thus the surface *height* is free on the
client. The surface *color* and *material* must come from a read of the real surface blocks.
`RainHeightMap[z*32+x]` gives the Y value to sample in the correct chunk of the column, which
prevents a scan of the full column.

### 2.4 View distance and the limits on chunk streaming

The client asks for a view distance, and the server approves it.
`IWorldPlayerData.DesiredViewDistance` has the documentation *"The players desired viewing
distance in blocks"*. `LastApprovedViewDistance` has the documentation *"The players viewing
distance in blocks that is allowed by the server"*
([IWorldPlayerData.cs L26-35](https://github.com/anegostudios/vsapi/blob/master/Common/Entity/Player/IWorldPlayerData.cs)).
Reach them at `capi.World.Player.WorldData.DesiredViewDistance`. The renderer of Farseer uses
this for its fade-in radius.

The server limit is `/serverconfig maxchunkradius [int]`, with the documentation *"the highest
chunk radius a player may load"*
([wiki: serverconfig commands](https://wiki.vintagestory.at/List_of_server_commands/serverconfig)).
Hosting guides give a default of 12 chunks, which is 384 blocks
([BisectHosting guide](https://help.bisecthosting.com/hc/en-us/articles/42433637094939-How-to-Change-the-Max-View-Distance-on-a-Vintage-Story-Server)).

On the client, `.viewdistance [number]` *"sets the viewing distance, no limit (unlike the
limit in graphics settings)"*
([wiki: client commands](https://wiki.vintagestory.at/List_of_client_commands)). In
singleplayer the integrated server accepts large values. In multiplayer `maxchunkradius`
limits the value.

The server drives the unload, with packet 11, as section 2.1 gives. There is no "keep chunks"
option on the client. This is one more reason for an LOD cache of our own. Farseer and
ChunkLOD solve the same problem on the server, with a SQLite heightmap database.

## 3. Custom rendering

### 3.1 IRenderer and RegisterRenderer

[`IRenderer`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderer.html):

```csharp
double RenderOrder { get; }   // 0 = drawn first, 1 = last; terrain opaque ~ 0.37, entities 0.4
int RenderRange { get; }      // "currently not used!"
void OnRenderFrame(float deltaTime, EnumRenderStage stage);
void Dispose();
```

Registration
([IClientEventAPI.cs L211, L223](https://github.com/anegostudios/vsapi/blob/master/Client/API/IClientEventAPI.cs)):

```csharp
void RegisterRenderer(IRenderer renderer, EnumRenderStage renderStage, string profilingName = null);
void RegisterRenderer(IRenderer renderer, EnumRenderStage renderStage, string profilingName,
                      double reservedFirstOrder, double reservedLastOrder, Type firstType);
```

Farseer registers at `EnumRenderStage.Opaque` with `RenderOrder => 0.36`. Thus the distant
terrain draws immediately *before* the real terrain, and the real terrain hides it by depth
([FarRegionRenderer.cs](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Client/FarRegionRenderer.cs)).

### 3.2 Render stages

[`EnumRenderStage`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.EnumRenderStage.html)
has these values. `Before(0)` is *"Before any rendering has begun"*. `Opaque(1)` is
*"Opaque/Alpha tested rendering"*. `OIT(2)` is *"Order independent transparency"*. Then come
`AfterOIT(3)`, the shadow map passes `ShadowFar(4)`, `ShadowFarDone(5)`, `ShadowNear(6)` and
`ShadowNearDone(7)`, then `AfterPostProcessing(8)`, `AfterBlit(9)`, `Ortho(10)` for the 2D
GUI, `AfterFinalComposition(11)` and `Done(12)`.

For an overview, read [wiki: Rendering API](https://wiki.vintagestory.at/index.php/Modding:Rendering_API).
It says that "a thorough tutorial is still missing" and links sample repositories, which
include [vsmodexamples](https://github.com/anegostudios/vsmodexamples).

### 3.3 Custom GLSL shaders

[`IShaderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IShaderAPI.html)
is at `capi.Shader`. Its members are `IShaderProgram NewShaderProgram()`,
`IShader NewShader(EnumShaderType)`,
`int RegisterFileShaderProgram(string name, IShaderProgram program)`,
`RegisterMemoryShaderProgram`, `GetProgramByName(string)`,
`IsGLSLVersionSupported(string)` and `bool ReloadShaders()`.
`RegisterFileShaderProgram` loads `name.vsh` and `name.fsh` from the
`assets/<domain>/shaders/` folder of the mod.

[`IShaderProgram`](https://github.com/anegostudios/vsapi/blob/master/Client/Render/IShaderProgram.cs)
has `Use()`, `Stop()`, `bool Compile()`, `Uniform(string, float|int|Vec2f|Vec3f|Vec4f|...)`,
`UniformMatrix(string, float[])`, `UniformMatrices`,
`BindTexture2D(string samplerName, int textureId, int textureNumber)`, `HasUniform(string)`,
`string PrefixCode`, `string AssetDomain` and `bool LoadError`.

This is the standard pattern that permits a hot reload. It comes from
[FarRegionRenderer.LoadShader](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Client/FarRegionRenderer.cs)
of Farseer:

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

The shaders are plain GLSL. The
[region.vsh](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/assets/farseer/shaders/region.vsh)
of Farseer is `#version 330 core`. A shader can use `#include` for the stock includes of the
game: `vertexflagbits.ash`, `colorutil.ash`, `shadowcoords.vsh`, `fogandlight.vsh` which
gives `getFogLevel()`, and `vertexwarp.vsh` which gives `applyGlobalWarping()`. This is how a
mod matches the fog and the lighting of vanilla exactly.

### 3.4 Meshes

[`MeshData`](https://github.com/anegostudios/vsapi/blob/master/Client/Model/Mesh/MeshData.cs)
holds the raw arrays `float[] xyz`, `float[] Uv`, `byte[] Rgba`, `int[] Flags` and
`int[] Indices`. `CustomMeshDataPartFloat CustomFloats` holds custom vertex attributes. The
constructors are `MeshData(bool initialiseArrays = true)` and
`MeshData(int capacityVertices, int capacityIndices, bool withNormals = false, bool withUv = true, bool withRgba = true, bool withFlags = true)`.
The two counters are `SetVerticesCount(int)` and `SetIndicesCount(int)`.

[`IRenderAPI`](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html)
is at `capi.Render`:

```csharp
MeshRef UploadMesh(MeshData data);        // "load into a VAO"
void UpdateMesh(MeshRef meshRef, MeshData updatedata);
void RenderMesh(MeshRef meshRef);
void RenderMesh(MeshRef meshRef, int[] indicesStarts, int[] indicesSizes, int groupCount);
void RenderMeshInstanced(MeshRef meshRef, int quantity = 1);
void DeleteMesh(MeshRef vao);
```

`MeshRef` is an opaque VAO handle, and `Dispose()` frees the GPU memory
([MeshRef.cs](https://github.com/anegostudios/vsapi/blob/master/Client/API/MeshRef.cs)).

Farseer builds one heightmap grid mesh for each region. It has (gridSize+1) squared
vertices, holds a position only, and stitches its edges with the neighbour regions. Farseer
pools the CPU arrays with `ArrayPool<T>`, uploads one time, and then draws each region with a
model matrix for that region.

### 3.5 Camera matrices, fog uniforms and the projection

The matrices are on `IRenderAPI`: `float[] CurrentModelviewMatrix`,
`float[] CurrentProjectionMatrix`, `double[] CameraMatrixOrigin`,
`float[] CameraMatrixOriginf` and `StackMatrix4 MvMatrix / PMatrix`. The camera matrix with
the translation at the origin permits camera-relative rendering, which prevents a loss of
float precision. Read the
[documentation](https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html).

Farseer does `modelMat.Identity().Translate(regionPos).Translate(-camPos)`, with
`Vec3d camPos = capi.World.Player.Entity.CameraPos`. It then gives
`viewMatrix = rapi.CameraMatrixOriginf` and `projectionMatrix = rapi.CurrentProjectionMatrix`.

The fog and the ambient light are at `capi.Ambient`
([`IAmbientManager`](https://github.com/anegostudios/vsapi/blob/master/Client/API/IAmbientManager.cs)):
`Vec4f BlendedFogColor`, `float BlendedFogDensity`, `float BlendedFogMin` and
`float BlendedCloudDensity`. `IRenderAPI` also has `FogColor`, `FogDensity` and `FogMin`, and
the large `DefaultShaderUniforms ShaderUniforms` object. That object holds `ZNear`, `ZFar`,
`SunPosition3D`, `PointLights3`, `FogSpheres`, `FlatFogDensity`, `FlatFogStartYPos`, and the
shadow matrices `ToShadowMapSpaceMatrixFar` and `ToShadowMapSpaceMatrixNear`, and more
([DefaultShaderUniforms.cs](https://github.com/anegostudios/vsapi/blob/master/Client/Render/DefaultShaderUniforms.cs)).
The sun and the time are at `capi.World.Calendar.SunPositionNormalized`, `.SunColor` and
`.DayLightStrength`.

The projection methods on `IRenderAPI` are `void Set3DProjection(float zfar, float fov)` and
`void Reset3DProjection()`.

**The default zFar cuts the distant LOD terrain.** Thus Farseer uses an internal of the
engine: `((ClientMain)capi.World).MainCamera.ZFar = max(3000, farViewDistance);` followed by
`capi.Render.Reset3DProjection();`. `ClientMain` is in `Vintagestory.Client.NoObf`, inside
**VintagestoryLib.dll**, which a mod can reference. The csproj of Farseer does this. This API
is not official, but the practice is standard.

Vanilla has an LOD for near terrain. The chunk tessellation supports a `lodLevel` of 0 to 3
for each mesh, through `ITerrainMeshPool.AddMeshData`, and `FrustumCulling.InFrustumAndRange`
culls it
([wiki: Modding:Level of detail](https://wiki.vintagestory.at/index.php/Modding:Level_of_detail)).
That is a model-detail LOD inside the normal view distance. It is not an increased draw
distance.

### 3.6 OpenGL version: do not require compute shaders or MDI

The official requirement is **OpenGL 3.3 or newer**
([system requirements](https://www.vintagestory.at/sysrequirements/)). The engine asks for a
3.3 context by default, and it throws "OpenGL version 330 is required" below that
([VintageStory-Issues #850](https://github.com/anegostudios/VintageStory-Issues/issues/850),
[#643](https://github.com/anegostudios/VintageStory-Issues/issues/643)). A user or a mod can
set `glContextVersion` in `clientsettings.json`, for example to `"4.2"`. Note that some
drivers give back a 4.6 compatibility profile
([#2454](https://github.com/anegostudios/VintageStory-Issues/issues/2454)).

The shaders of the game are GLSL 330. The stock includes and the shader of Farseer both have
`#version 330 core`.

The renderer is built on **OpenTK**. The decompiled VintagestoryLib imports
`OpenTK.Graphics.OpenGL` throughout, for example in
[UBO.cs and ClientPlatformWindows.cs](https://github.com/Primer81/vintage-story-mods/tree/master/references/1.20.7/DecompiledSource/VintagestoryLib).
A mod runs in the same process. Thus a mod can make raw GL calls through the OpenTK assembly
of the game, for example `glMultiDrawElementsIndirect`.

**The practical limit is GL 4.1.** Compute shaders and MDI need GL 4.3. Vintage Story
officially supports macOS, and it is now native on Apple Silicon as of
[1.22.3](https://www.vintagestory.at/blog.html/news/1223-maintenance-patch-r445/). Apple
limits OpenGL to 4.1.

Thus any GL 4.3 path must be optional, with a fallback to 3.3. Test with
`capi.Shader.IsGLSLVersionSupported` and a query of the GL extensions. Design the core
renderer for GL 3.3: a static VAO for each LOD cell with one draw call each, which is the
approach of Farseer, or manual batching. Instancing with `RenderMeshInstanced` is available
in GL 3.3.

## 4. Existing mods

### 4.1 Farseer: open source, MIT, the primary reference

The ModDB page is [mods.vintagestory.at/show/mod/22371](https://mods.vintagestory.at/show/mod/22371).
Version 1.4.0 is from April 2026, for game 1.22.0, by the author badgerson. The source is at
[github.com/ViciousBadger/VSMod-Farseer](https://github.com/ViciousBadger/VSMod-Farseer),
under the **MIT license** (LICENSE.md, (c) 2026 Badgerson). Thus the client rendering code is
reusable with attribution.

The architecture comes from the source. The **server side** is `Server/FarRegionGen.cs`,
`FarRegionDB.cs` and `FarRegionProvider.cs`. It builds one 2D heightmap for each "far region"
from the heightmap data of the map chunks, stores it in SQLite, and streams `FarRegionData`
messages over a named network channel. The channel is
`api.Network.RegisterChannel("farseer").RegisterMessageType<...>()`. Read
[FarseerModSystem.cs](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/FarseerModSystem.cs).
The protocol is in `Protocol.cs`, with protobuf-net.

The **client side** is `Client/FarRegionRenderer.cs`, at approximately 300 lines. It builds a
heightmap grid mesh, stitches the neighbour edges, and rebuilds a dirty region later. It calls
`UploadMesh`, and then one `RenderMesh` for each region in the `Opaque` stage. Its GLSL 330
shader moves the far terrain down near the transition ring, applies a term for the curvature
of the globe, and uses the fog and sun uniforms of vanilla.

Farseer needs the mod on the server. The default is approximately 4000 blocks. The server
limits each client through the world config value
`capi.World.Config.GetInt("maxFarViewDistance")`.

Its 1.4.0 release integrates "Algernon's Terrain Sampler Lib", which makes the server-side
heightmap generation much faster
([mod page](https://mods.vintagestory.at/show/mod/22371)).

### 4.2 ChunkLOD: closed source

The ModDB page is [mods.vintagestory.at/chunklod](https://mods.vintagestory.at/chunklod), by
the author BiasHyperion. There is **no public repository**. The field `sourcecodeurl` is
empty in the [ModDB API record](https://mods.vintagestory.at/api/mod/chunklod), and it states
no license. Thus treat it as all rights reserved. Examine its behaviour, and copy no code.

The status from the ModDB API in July 2026 is: stable 1.1.0, and test builds 1.2.0-dev.1 to
1.2.0-dev.3, of which the most recent is from July 11, 2026, for game 1.22.0 to 1.22.3. The
page says that a "major overhaul underway". A backport for 1.21.6 is at
[mods.vintagestory.at/chunklodold](https://mods.vintagestory.at/chunklodold).

The design comes from the mod description. It needs the server. It keeps a SQLite database
for each world, with 1:1 chunk heightmaps and coloring data, at approximately 10 MB for a 4k
radius. The client draws colored and lit heightmap terrain. The base color comes from the
rock id, green shading comes from the forestation, blue marks water, and it applies seasonal
tinting. It has more than one grid resolution, which includes a distance-adaptive "Mixed"
mode. Face lighting and globe curvature are optional. The view distance reaches 16,384, and
the development builds reach 65,000. Its shader is *"a heavily modified instance"* of the
Farseer shader, with a credit.

Its changelogs give these known problems: z-fighting at the LOD seams, fog propagation, water
at sea level against the fog system, microblocks and chiseled blocks, and face rendering on a
Qualcomm iGPU.

### 4.3 The vanilla engine

Vanilla has no LOD for distant terrain as of 1.22.x. There is nothing in the
[1.21 release notes](https://www.vintagestory.at/blog.html/news/v1210-story-chapter-2-redux-stable-r420/)
or on the [1.22 feature page](https://info.vintagestory.at/v1dot22). Community answers to
"[Are there any plans for LOD?](https://www.vintagestory.at/forums/topic/11348-are-there-any-plans-for-level-of-detail-lod/)"
report no plans.

The only LOD in the game is the `lodLevel` of 0 to 3 for each mesh in the chunk tessellation
([wiki](https://wiki.vintagestory.at/index.php/Modding:Level_of_detail)).

## 5. Development setup

**Template.** Run `dotnet new install VintageStory.Mod.Templates`, then
`dotnet new vsmod --AddSolutionFile -o mymod`
([wiki: Setting up your Development Environment](https://wiki.vintagestory.at/Modding:Setting_up_your_Development_Environment)).
The template source is at [github.com/anegostudios/vsmodtemplate](https://github.com/anegostudios/vsmodtemplate).

**The `VINTAGE_STORY` environment variable** points at the game installation. On Linux, put
`export VINTAGE_STORY="$HOME/ApplicationData/vintagestory"` in `~/.bashrc`. The same wiki page
gives this.

This is the csproj pattern, from
[Farseer.csproj](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Farseer.csproj).
It uses `<TargetFramework>net10.0</TargetFramework>`,
`<GamePath Condition="Exists('$(VINTAGE_STORY)')">$(VINTAGE_STORY)</GamePath>`, and then
`<Reference Include="VintagestoryAPI"><HintPath>$(GamePath)\VintagestoryAPI.dll</HintPath><Private>false</Private></Reference>`.
Add `VintagestoryLib.dll` when the mod needs the internals of `Vintagestory.Client.NoObf`,
such as `ClientMain.MainCamera`. Add `Mods/VSSurvivalMod.dll`, `Mods/VSEssentials.dll`,
`Lib/protobuf-net.dll` and `Lib/0Harmony.dll` as necessary.

CAUTION: Give `Private=false` to each reference. Never copy a game DLL into the mod zip.

**Run and debug on Linux.** This is the
[launchSettings.json](https://github.com/ViciousBadger/VSMod-Farseer/blob/main/Farseer/Properties/launchSettings.json)
of Farseer. It operates in Rider and in VS Code.

```json
"Client": {
  "commandName": "Executable",
  "executablePath": "dotnet",
  "commandLineArgs": "\"$(VINTAGE_STORY)/Vintagestory.dll\" --tracelog --addModPath \"$(ProjectDir)/bin/$(Configuration)/Mods\" --addOrigin \"$(ProjectDir)/assets\"",
  "workingDirectory": "$(VINTAGE_STORY)"
}
```

`--addModPath` loads the build output as a mod folder. `--addOrigin` mounts the assets
directory, thus an edit to a shader or a lang file reloads without a new package. The README
of the [vsmodtemplate](https://github.com/anegostudios/vsmodtemplate) notes that Linux and Mac
must use the form without an extension, or the dll launch form.

A shader can reload during a session. A change in the graphics settings fires `ReloadShader`,
and `IShaderAPI.ReloadShaders()` compiles all of them again.

## Key design results for a client-only Distant Horizons for Vintage Story

1. A mod with `"side": "Client"` joins any vanilla server (section 1.3). It must **collect
   the terrain itself**. It subscribes to `capi.Event.ChunkDirty` for `NewlyLoaded` and
   `MarkedDirty`, reads `chunk.MapChunk.RainHeightMap` or `WorldGenTerrainHeightMap` for the
   height (section 2.3), samples the surface blocks for the color, and stores the result in a
   local SQLite cache for each world and server. `TopRockIdMap` is null on the client. This is
   the client-side equivalent of what Farseer and ChunkLOD do on the server. It is also the
   reason why they chose the server: they get full coverage immediately, and this mod builds
   up coverage as the player explores, as Distant Horizons does.
2. Draw with an `IRenderer` at `EnumRenderStage.Opaque` with a `RenderOrder` of approximately
   0.36. Use camera-relative model matrices with `CameraMatrixOriginf`. Use a custom GLSL 330
   program that includes the vanilla `fogandlight.vsh` and `vertexwarp.vsh`. Take the uniforms
   from `capi.Ambient.Blended*` and `capi.World.Calendar`. The MIT renderer and shader of
   Farseer are a usable start (sections 3 and 4.1).
3. Increase `ZFar` through `Vintagestory.Client.NoObf.ClientMain.MainCamera.ZFar` and
   `Reset3DProjection()`. This needs a reference to VintagestoryLib (section 3.5).
4. Plan for GL 3.3 as the core, because macOS has a maximum of 4.1. Treat compute shaders and
   MDI as optional fast paths only (section 3.6).
5. Target game 1.22.x and .NET 10. The most recent stable version is 1.22.3, from May 30, 2026
   (section 0).
