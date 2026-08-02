# Distant Horizons architecture report

CAUTION: Distant Horizons is LGPL-3.0. VintageHorizons is a clean reimplementation. This
report gives the architecture and the concepts for reference. This project copies no code.

**Scope.** This report covers the `distant-horizons` repository at the current master. The
core is engine-agnostic, in `coreSubProjects/core`. The public API is in
`coreSubProjects/api`. The code for each Minecraft version is in `common/`, `fabric/`,
`forge/` and `neoforge/`.

All paths below are relative to the root of the repository. The core path
`coreSubProjects/core/src/main/java/com/seibel/distanthorizons/core` becomes `core/...` here.

**The architecture in one paragraph.** The hooks of the mod loader make a snapshot of the
chunks that the client holds already. A bounded queue, with the distance as its priority,
converts them into column-RLE "full data sources". Each source holds 64 x 64 columns, as a
palette with packed longs. The mod merges these into a mip pyramid on SQLite, keyed by the
detail level with x and z. A quadtree of render sections, centered on the player, takes data
from that pyramid. It meshes greedy quads off the main thread. Then it draws them into a
framebuffer of its own, which it composites behind and around the vanilla terrain, with a
clip plane and a dithered fade.

---

## 0. The data flow

```
MC chunk (client packet / server save / interaction event)
  - platform proxy/mixin (fabric|forge|neoforge|common)
      - SharedApi.applyChunkUpdate()                    [core/api/internal/SharedApi.java]
          - ChunkUpdateQueueManager (per level, 2-stage, distance-priority, hash-gated)
              - DhLightingEngine block-light bake
              - IDhLevel.updateChunkAsync()             [core/level/AbstractDhLevel.java]
                  - LodDataBuilder.createFromChunk -> FullDataSourceV2 (leaf, applyToParent=true)
                      - DelayedDataSourceSaveCache (1s write-merging)
                          - FullDataUpdaterV2.updateDataSource (merge + SQLite save + listeners)
                              - FullDataUpdatePropagatorV2 (child-to-parent mip pyramid, DB-flag driven)
                              - ClientLevelModule.OnDataSourceUpdated -> LodQuadTree.queuePosToReload
                                  - LodRenderSection: FullData -> ColumnRenderSource -> quads -> VBO
                                      - RenderBufferHandler / LodRenderer (own FBO, composited to MC)
```

---

## 1. Chunk ingestion

### 1.1 Hooks in the platform layer

Distant Horizons deliberately has **no dependency on the server**. On a vanilla server, all
data comes from what the client holds already.

**A chunk arrives from the server.**
`fabric/src/main/java/com/seibel/distanthorizons/fabric/mixins/client/MixinClientPacketListener.java`
injects into `ClientPacketListener.enableChunkLight` for Minecraft 1.20 and later. That is the
moment when the network chunk *and its light* are both complete. The hook immediately moves
off the network thread and the render thread, onto the IO executor. It wraps the chunk with
`new ChunkWrapper(chunk, levelWrapper)`, and calls `SharedApi.INSTANCE.applyChunkUpdate(...)`.

For Minecraft 1.18 to 1.20 the equivalent hook is `ClientLevel.setLightReady`, in
`fabric/src/main/java/com/seibel/distanthorizons/fabric/mixins/client/MixinClientLevel.java`.
The `handleLogin` and `close` injections of the same mixin drive
`ClientApi.onClientOnlyConnected` and `onClientOnlyDisconnected`, which give the world
lifecycle.

**A chunk load event.**
`fabric/src/main/java/com/seibel/distanthorizons/fabric/FabricClientProxy.java`, at
approximately line 122, also registers `ClientChunkEvents.CHUNK_LOAD`. But it does this **only
when `MC.clientConnectedToDedicatedServer()`**. In singleplayer the server-side hooks below
give better data.

**Singleplayer, the integrated server, and a dedicated server.**
`common/src/main/java/com/seibel/distanthorizons/common/commonMixins/MixinChunkMapCommon.java`
hooks the **chunk save**, through `ServerApi.INSTANCE.serverChunkSaveEvent`. It has guards: the
light must be correct, the chunk must not be a `ProtoChunk`, and the biomes must be present.
`fabric/src/main/java/com/seibel/distanthorizons/fabric/FabricServerProxy.java` hooks
`ServerChunkEvents.CHUNK_LOAD` and sends it to `ServerApi.serverChunkLoadEvent`. Both paths go
to the same `SharedApi.applyChunkUpdate` (`core/api/internal/ServerApi.java` lines 107-108).

The chunk *save* is the mechanism that finds a change when a server is available. Minecraft
saves a dirty chunk from time to time. Thus the mod gets the edits at no cost.

**Block changes on the client, against a remote server.** Fabric has no client event for a
block change. Thus `FabricClientProxy.java` registers `AttackBlockCallback` for a break and
`UseBlockCallback` for a place, as approximations. Each one gets the chunk at the position of
the interaction again, and submits it again.

Forge and NeoForge use `PlayerInteractEvent.LeftClickBlock` and `RightClickBlock`
(`forge/src/main/java/com/seibel/distanthorizons/forge/ForgeClientProxy.java` lines 113-176).
Note the cheap first test, `SharedApi.isChunkAtBlockPosAlreadyUpdating(...)`. It prevents a
pull of a chunk from Minecraft, which is slow and bad for the render thread, when that chunk
is in the queue already.

### 1.2 The funnel: `SharedApi.applyChunkUpdate`

This is in `core/api/internal/SharedApi.java`, at approximately line 190. It has three
responsibilities.

It drops an update when no Distant Horizons world or level exists yet. If the client level did
not load, the chunks wait in `ClientApi.INSTANCE.waitingChunkByClientLevelAndPos`. Then
`ClientApi.loadWaitingChunksForLevel` replays them later
(`core/api/internal/ClientApi.java`, at approximately line 249). This handles the race where
chunks arrive before the level-load event.

It skips the update when the network protocol of Distant Horizons on the server gives the
updates instead (`DhClientLevel.shouldProcessChunkUpdate`, `core/level/DhClientLevel.java`, at
approximately line 326).

It removes duplicates with `chunkManager.contains(pos)`, puts the chunk into a
`ChunkUpdateQueueManager` for that level, and then starts the "LOD Builder" executor.

### 1.3 The queue: two stages, priority by distance, and a hash gate

`core/api/internal/chunkUpdating/WorldChunkUpdateManager.java` holds one
`ChunkUpdateQueueManager`
(`core/api/internal/chunkUpdating/ChunkUpdateQueueManager.java`) for each `ILevelWrapper`.
Thus nether chunks cannot enter the overworld LODs.

**There are two queues, `preUpdateQueue` and `updateQueue`.** Both are a `ChunkPosQueue`
(`core/api/internal/chunkUpdating/ChunkPosQueue.java`), and both have the player chunk at
their center, through `setCenter`. A worker calls `popClosest()`. When a queue is full, it
removes an item with `popFurthest()`.

The limit is `MAX_UPDATING_CHUNK_COUNT_PER_THREAD_AND_PLAYER = 1_000`, multiplied by the
thread count and the player count. Above that limit, a warning goes to the chat and the log,
at a limited rate.

**The pre-update stage tests for stale data.** It compares `dhLevel.getChunkHash(pos)`, which
is on disk (read section 3), against `chunkWrapper.getBlockBiomeHashCode()`. The hash is in
`core/wrapperInterfaces/chunk/IChunkWrapper.java`, at approximately line 232. It samples every
second block in x, y and z, with the block state, the biome and the y value. It also samples
the *full* surface from both heightmaps. The hash is cheap, but it finds the edits that
players really make. A chunk with no change stops here. This is what makes a chunk that
arrives again almost free.

**The update stage** collects up to 8 neighbour chunk wrappers *from the cache of the queue*,
`queuedChunkWrapperByChunkPos`. That is a Guava cache that expires after 20 seconds, which
prevents a read of the Minecraft level from a worker thread.

Then it bakes the **block** lighting of Distant Horizons, with
`DhLightingEngine.bakeChunkBlockLighting` (`core/generation/DhLightingEngine.java`). That is a
standalone engine, and Starlight gave the idea. Distant Horizons never trusts or needs the
light data of Minecraft. Then it updates the beacon beams, and calls
`dhLevel.updateChunkAsync(chunkWrapper, newChunkHash)`.

`ChunkWrapper`
(`common/src/main/java/com/seibel/distanthorizons/common/wrappers/chunk/ChunkWrapper.java`) is
the snapshot boundary. It builds the heightmaps of Distant Horizons with `createDhHeightMaps`.
It copies the chunk sections in `copy()`, at lines 150 and 519. It holds the light arrays that
Distant Horizons calculated. Thus the processing that follows never reads live Minecraft
state.

---

## 2. LOD data model

### 2.1 Position keys: `DhSectionPos`

`core/pos/DhSectionPos.java` holds a packed `long`: 8 bits for the detail level, and 28 bits
each for x and z. A **section** always holds **64 x 64 data columns**, at each detail level.

`SECTION_MINIMUM_DETAIL_LEVEL = 6`. Thus:

- Section detail 6, the leaf, is 64 x 64 columns of one *block* each, which is 4 x 4 Minecraft
  chunks. That is the unit of a file and of the quadtree. The class documentation explains the
  trade in the size.
- Each step of +1 in the detail doubles the area of a column. The root is
  `ROOT_SECTION_DETAIL_LEVEL = 6 + REGION_DETAIL_LEVEL(9) = 15`
  (`core/file/fullDatafile/V2/FullDataSourceProviderV2.java` lines 71-79, and
  `LodUtil.REGION_DETAIL_LEVEL=9` in `core/util/LodUtil.java`).
- The "data detail" is the section detail minus 6. Thus 0 is one block for each column, and 1
  is 2 x 2 blocks for each column.

### 2.2 The format that holds the truth: `FullDataSourceV2`

This is `core/dataObjects/fullData/sources/FullDataSourceV2.java`.

`WIDTH = 64`. The field `dataPoints` is a `LongArrayList[64*64]`, which is one **vertical RLE
column** for each x and z, sorted from the top down.

Each column has one byte of `columnGenerationSteps`, which is an `EDhApiWorldGenerationStep`
that gives how complete the data is. It also has one byte of `columnWorldCompressionMode`,
which is `MERGE_SAME_BLOCKS` or the lossy `VISUALLY_EQUAL`
(`coreSubProjects/api/.../enums/config/EDhApiWorldCompressionMode.java`).

The field `mapping` is a palette for each source, a `FullDataPointIdMap`
(`core/dataObjects/fullData/FullDataPointIdMap.java`). It maps an int id to a pair of a biome
wrapper and a block-state wrapper. It serializes as strings, in the form
`"biome_DH-BSW_blockstate"`. A merge of two sources merges the palettes and maps the ids
again, in `mergeAndReturnRemappedEntityIds`. `removeUnusedIdsAndRemap()`, at line 1145,
prevents a palette that grows without a limit.

**The packing of a data point** is in `core/util/FullDataPointUtil.java`, lines 30-70. One
`long` holds a **32-bit palette id, a 12-bit height, a 12-bit bottom Y relative to the minimum
of the level, 4 bits of sky light and 4 bits of block light**. A column is a stack of these
runs with no gaps. Air is stored, thus the downsampling of the lighting operates.
`LodDataBuilder.validateOrThrowApiDataColumn` makes this a rule.

The flags `applyToParent` and `applyToChildren` have three states, and the mod *stores* them.
Read section 6.

The other fields are `isEmpty`, the timestamps, and pooled backing arrays. The pooling is in
`AbstractPhantomArrayList`, and it reduces the work of the GC.

**Chunk to leaf source.** `core/dataObjects/transformers/LodDataBuilder.java#createFromChunk`
reads each of the 16 x 16 columns from the top down. It starts at
`max(lightBlockingHeightmap, solidHeightmap)`, and it writes a new run when the block state
**or the biome** changes.

A "force single block" rule prevents a thin colored top, such as a snow layer, a flower or
fire, from giving its color to the full column. Read lines 195-256.

The chunk position maps into one quadrant of the leaf section of 4 x 4 chunks, as
`chunkPos & 3`.

The sky light is *not* set here. The mod bakes it later, for each 64 x 64 source, in
`AbstractDhLevel.onDataSourceSaveAsync` through `DhLightingEngine.bakeDataSourceSkyLight`
(`core/level/AbstractDhLevel.java` line 183). Thus the light is consistent across the chunks.

### 2.3 Downsampling, which makes the mips

`FullDataSourceV2.updateFromDataSource`, at line 374, accepts an input at the same level, one
level below, or one level above.

**Child to parent** is `updateFromOneBelowDetailLevel`, at line 591. The child covers one
quadrant of the parent. Each 2 x 2 input columns merge into 1 output column, through
`mergeInputTwoByTwoDataColumn` at line 722.

That method collects all the y boundaries of the 4 columns. It sweeps the slices from the
bottom up, and samples each column at the middle of the slice. It takes the **most common
palette id**, with a rule for a tie and a rule for air. It **averages the light**. Then it
makes the RLE again for the adjacent slices that are identical.

The generation step of a merged column is the **minimum** of the 4. Thus the mod never claims
data that it did not generate.

**Same level** replaces one column at a time. A gate on the generation step prevents an
overwrite of more complete data with less complete data. A hash comparison finds whether
anything changed.

**Parent to child** is `downsampleFromOneAboveDetailLevel`, at line 1046. This upsampling
supports the experimental function that fills holes with low-detail data.

After a change, an optional occlusion cull removes the hidden runs, in
`core/dataObjects/transformers/FullDataOcclusionCuller.java`. This occurs when the lossy
compression is on.

### 2.4 The render format: `ColumnRenderSource`

`core/dataObjects/render/ColumnRenderSource.java` also holds 64 x 64 columns. But each column
has a **fixed maximum count of vertical slices**, which comes from `EDhApiVerticalQuality`. It
also uses a different 64-bit packing (`core/util/RenderDataPointUtil.java` lines 30-60): a
**4-bit material id, a 4-bit alpha, 8 bits each of R, G and B, a 12-bit yMax, a 12-bit yMin,
and 4 bits each of block light and sky light**.

Thus the render pipeline operates on colors that the mod resolved already, and it uses no
palette.

`core/dataObjects/transformers/FullDataToRenderDataTransformer.java` does the conversion. It
turns a block and a biome into a tinted color, through the level wrapper. It applies the
config for ignored blocks. It culls caves, with heuristics that use `skylight==0`, at lines
280-300. It replaces the water surface. It also reduces a column that is above the vertical
budget, with `RenderDataPointReducingList`.

A render source is **temporary**. The mod builds it on demand from the full data, and it never
stores it. The old table for the render cache is gone, in
`sqlScripts/0040-sqlite-removeRenderCache.sql`.

---

## 3. Storage

**The engine** is SQLite over JDBC, with one database file for each level. `AbstractDhRepo`
(`core/sql/repo/AbstractDhRepo.java`) has `DEFAULT_DATABASE_TYPE = "jdbc:sqlite"`. Each thread
gets a `Connection`. The layer finds corruption and quarantines it. It uses WAL journaling
(`sqlScripts/0031-sqlite-useSqliteWalJournaling.sql`).

**Migrations** are numbered SQL scripts in
`coreSubProjects/core/src/main/resources/sqlScripts/`. `core/sql/DatabaseUpdater.java` applies
them. `core/file/fullDatafile/V2/DataMigratorV1.java` migrates the old V1 data later, on its
own thread.

**The schema** is in `0020-sqlite-createFullDataSourceV2Tables.sql`. The table `FullData` has
the primary key **(DetailLevel, PosX, PosZ)**. `DetailLevel` is the *data* detail, as 0, 1, 2
and more, which matches the mip pyramid.

The blob columns are `Data`, which holds all 4096 columns as packed longs with a length
prefix, then `ColumnGenerationStep`, `ColumnWorldCompressionMode`, and `Mapping` for the
palette strings. The other columns are `DataFormatVersion`, `CompressionMode`, the dirty flags
`ApplyToParent` and `ApplyToChildren`, and the timestamps.

The migration `0090-sqlite-addAdjacentFullDataColumns.sql` adds the blobs `NorthAdjData`,
`SouthAdjData`, `EastAdjData` and `WestAdjData`. These are **edge strips of one column, taken
out in advance**. Thus a mesh of the seams of a section deserializes one strip of each
neighbour, and not the full 64 x 64 source. Read `FullDataSourceProviderV2.getAdjForDirection`
at line 303.

**Compression** is LZ4 or ZStd over the full blob
(`coreSubProjects/api/.../EDhApiDataCompressionMode.java`). The javadoc gives a ratio of
approximately 0.26 for ZStd. The config selects it, and each row records which one it uses.
The DTO layer is `core/sql/dto/FullDataSourceV2DTO.java`.

**Chunk hashes** go into the table `ChunkHash`
(`0060-sqlite-createChunkHashTable.sql`, `core/sql/repo/ChunkHashRepo.java`). The mod writes a
hash in the same transaction as the save of its data source
(`AbstractDhLevel.onDataSourceSaveAsync`, line 194 and later). Thus a hash never exists
without its data.

**The key for each server and world** is in `core/file/structure/ClientOnlySaveStructure.java`.
On a remote server, the mod saves under
`<mcInstall>/Distant_Horizons_server_data/<folderName>/<dimensionName>/DistantHorizons.sqlite`.
The `folderName` is configurable, from the server name, the IP, the port or the Minecraft
version, with percent escapes.

If the server runs the optional plugin protocol of Distant Horizons, it can give an explicit
**level key**, as `IServerKeyedClientLevel`, through
`core/api/internal/ClientPluginChannelApi.java`. That key separates the worlds of a multiverse
server. Without it, the mod can use the dimension name only. Singleplayer uses
`core/file/structure/LocalSaveStructure.java`, inside the world save.

**Write merging** is `core/util/delayedSaveCache/AbstractDelayedSaveCache.java` with
`DelayedDataSourceSaveCache`. The mod merges the chunk updates in memory for each section
position, and writes them after 1 second with no new data (`AbstractDhLevel.java` line 69).
Chunks that arrive near each other almost always go into the same section of 4 x 4 chunks.

---

## 4. Rendering

### 4.1 The quadtree of render sections

`core/render/QuadTree/LodQuadTree.java` uses the generic
`core/util/objects/quadTree/QuadTree.java`. The values are `LodRenderSection`
(`core/render/QuadTree/LodRenderSection.java`). There is one tree for each client level that
draws. `ClientLevelModule.ClientRenderState` makes it (`core/level/ClientLevelModule.java`
line 229), and a change of the render distance builds it again.

**The tick** is `tryTick` with `updateAllRenderSections`, at approximately 100 ms from the
timer of `DhClientWorld`. It puts the tree center at the player or the camera, and then walks
each root recursively.

**The expected detail** is `log(blockDistance / distanceUnit) / log(quadraticBase)`, clamped
to the range from `maxHorizontalResolution` to the root, at line 1162. This is a logarithmic
decrease of the LOD with the distance. The base and the unit come from the "horizontal
quality" config.

`recursivelyUpdateRenderSectionNode`, at line 514, does the walk. When a node is coarser than
the mod wants, it goes into the 4 children. **A parent continues to draw until all 4 children
have uploaded their buffers** (`onDetailLevelTooLow`, line 601). The mod deletes the children
only on the render thread, after the parent takes over. Read `addEnableDeleteChildrenNode`
with the cleanup in `RenderThreadTaskHandler`, at line 416. This is the mechanism that
prevents holes and flicker.

A root node never draws. Thus there is no flash at an edge when the player crosses a root
boundary, at line 623.

The mod collects the sections that need a build, and loads them with the lower detail first
and the nearest first (`loadQueuedSections`, line 770). A section whose data changed comes
through `queuePosToReload` into the `sectionsToReload` queue, at line 1218.

The tree also queues **world generation on demand**, in singleplayer only. For a missing
position, `FullDataSourceProviderV2.canRetrieveMissingDataSources()` is false on a client-only
level, and true in `core/file/fullDatafile/GeneratedFullDataSourceProvider.java`. On a server
that has Distant Horizons, `core/file/fullDatafile/RemoteFullDataSourceProvider.java` gets the
LODs over the plugin channel instead.

On a vanilla server neither one runs. Thus an LOD exists only where the client saw a chunk.

### 4.2 Mesh building

`LodRenderSection.uploadRenderDataToGpuAsync`, at line 136, runs on the "Render Loader"
executor. It has five steps.

1. It loads the full data of the center and the 4 neighbour **edge strips** from SQLite. Then
   it converts each one to a `ColumnRenderSource`, as section 2.4 gives.
2. `core/dataObjects/render/bufferBuilding/ColumnRenderBufferBuilder.java#makeLodRenderData`
   runs for each of the 64 x 64 columns. It gets the 4 adjacent columns, from inside the
   section or from the neighbour strip, and it handles a mismatch in the detail level, at
   lines 146-235. Then for each vertical run it calls `addRenderDataPointToBuilder`, which
   calls `ColumnBox.addBoxQuadsToBuilder`
   (`core/dataObjects/render/bufferBuilding/ColumnBox.java`).

   That method writes the visible faces of the box of the run only. It culls the up face and
   the down face against the runs above and below. It clips a side face against the runs of
   the adjacent column, and it marks an occluded span as `SKYLIGHT_COVERED` and skips it. This
   is the strategy of column quads with neighbour culling.
3. `core/dataObjects/render/bufferBuilding/LodQuadBuilder.java` puts the quads into buckets
   for the 6 face directions. It merges during the insert, and then it does a full **greedy
   merge pass** (`mergeQuads`, line 265) along both axes, through `BufferQuad.tryMerge`
   (`core/dataObjects/render/bufferBuilding/BufferQuad.java`). A transparent quad merges from
   east to west only, which keeps the sort order.
4. **The vertex format is 16 bytes** (`putVertex`, line 477). It holds 3 int16 values for the
   position relative to the section, an int16 of metadata with 4 bits each of sky light and
   block light and a 6-bit "micro offset" for each axis that the vertex shader uses against
   cracks, an RGBA8 color, a uint8 material id for Iris, a uint8 normal index, and an int16
   texture tile id. The positions are relative to the corner of the section. The origin of the
   section goes into a uniform for each buffer.
5. The upload uses `core/dataObjects/render/bufferBuilding/LodBufferContainer.java`. The CPU
   buffers go to the task queue of the render thread, which makes and uploads the GL buffers.
   The worker calls `join()` on the upload, which limits the growth of the staging memory
   (`LodRenderSection.java` lines 194-201). The mod removes the old buffer only after the new
   one is complete, thus an update does not flicker.

### 4.3 Frame rendering, overlap with vanilla, fog and depth

**The entry point** is the render hook of the mod loader. On Fabric,
`WorldRenderEvents.AFTER_SETUP` calls `ClientApi.INSTANCE.renderLods()`
(`fabric/.../FabricClientProxy.java` line 227), and it captures the model-view matrix and the
projection matrix of Minecraft. `ClientApi.renderLodLayer` also empties the task queue of the
render thread, with a **budget of half a frame**
(`core/render/RenderThreadTaskHandler.java#runRenderThreadTasks`, line 128).

`core/render/RenderBufferHandler.java#buildRenderList`, at line 132, walks the list of enabled
sections of the tree. It culls each section against the frustum, with an AABB that covers the
full world height. It gives a buffer list **sorted from near to far**, by the Manhattan
distance. The opaque pass draws from near to far. The transparent pass draws from far to near.
A comment gives the trick for the order of translucency with discrete columns.

`core/render/renderer/LodRenderer.java#renderTerrain`, at line 119, draws into **the color and
depth framebuffer of Distant Horizons**. The order is: the opaque pass, then the generic
objects, then SSAO, then the transparent pass, then the fog (`shaders/fog/gl/*`), then the fade
at the far clip.

Then a fullscreen "apply" pass composites that buffer into the framebuffer of Minecraft, and
it uses both depth buffers. Read `this.metaRenderer.applyToMcTexture` and the shader
`coreSubProjects/core/src/main/resources/assets/distanthorizons/shaders/shared/gl/apply.frag`.

**Three mechanisms prevent an overlap with the vanilla terrain.** They all operate in the
projection domain and the fragment domain. Distant Horizons does *not* track which chunks
vanilla draws.

1. **A near clip plane** on the projection of Distant Horizons. It is a fraction of the vanilla
   render distance, which the config calls "overdraw prevention". Read
   `core/util/RenderUtil.java#getNearClipPlaneInBlocks` at line 149. The value scales
   automatically with the vanilla render distance, between 0.2 and 0.9. `DynamicOverdraw`
   makes it smaller while the player flies fast, thus the LODs cover ground that vanilla did
   not load. The mod overrides it when the camera is high above the terrain.

   `setDhProjectionMatrix`, at line 95, rewrites only m22 and m23 of the projection matrix of
   Minecraft. Thus the far clip of Distant Horizons reaches the LOD distance. It knows about
   reverse Z, through `EDhRenderDepth`.
2. **A dithered fade near the boundary**, in the fragment shader of the terrain. Read
   `shaders/terrain/gl/frag.frag` lines 137-154. A Bayer matrix drives a `discard` between
   `uClipDistance` and 1.5 times that value. Thus the LOD edge dissolves into the vanilla
   terrain, and there is no hard line.
3. **A fade of the vanilla image**, in the opposite direction. `shaders/fade/gl/vanilla_fade.frag`
   calculates the world distance again from the depth buffer *of Minecraft*. Then it uses a
   smoothstep to blend the vanilla fragments toward the color of Distant Horizons, between a
   start distance and an end distance. This hides the fog and the edge of vanilla.
   `ClientApi.renderFadeOpaque` and `renderFadeTransparent` drive it
   (`core/api/internal/ClientApi.java` lines 631-686).

**The sky and the heights.** A uniform for the level height prevents the Minecraft clouds from
drawing behind the clouds of Distant Horizons, in the same shader at line 66. Distant Horizons
can also turn off the vanilla clouds and fog (`LodRenderer` line 168).

---

## 5. Threading model

The model is in `core/util/threading/ThreadPoolUtil.java` and
`core/util/threading/PriorityTaskPicker.java`.

**There is one shared worker pool.** `Config.Common.MultiThreading.numberOfThreads` gives its
size. `PriorityTaskPicker` divides it into named logical executors: `Network Compression`,
`IO` for the file handler and SQLite, `Render Loader` for the mesh building, `LOD Builder` for
the conversion from a chunk to full data, `Update Propagator`, and `World Gen`.

The picker limits the total concurrent tasks to N, and it goes through the executors in turn.
`Update Propagator` and `World Gen` have `canRun` predicates. Those predicates **stop them
while the camera moves fast**, at the speed of an elytra, or while the backlog of the LOD
builder is large. Read lines 117-118 and 170-201. Thus the throttle comes from starvation, and
not from priorities.

**Some threads are standalone**, and not in the pool: the network client handler, the beacon
culling, the migration of V1 data, the cleanup, the flusher of the delayed-save cache
(`core/util/delayedSaveCache/AbstractDelayedSaveCache.java`), the 250 ms poll loop of the
propagator (`FullDataUpdatePropagatorV2.runUpdateQueue`), and the retrieval-queue thread of
the quadtree.

The mod makes the pools at world load, and stops them at world unload, in
`SharedApi.setDhWorld` (`core/api/internal/SharedApi.java` line 91).

**The render thread** does the *only* GL work, through
`core/render/RenderThreadTaskHandler.java`. A queue empties during the render hook of Distant
Horizons, with a time budget of half the target frame time. It does the VBO uploads, the
buffer closes, and the deletion of the quadtree children. All the other work is off the render
thread: the database IO, the lighting, the transform, the meshing and the downsampling.

**The game tick does not drive the mod.** `DhClientWorld` ticks its levels, and thus the
quadtree, from a `java.util.Timer` every 100 ms (`core/world/DhClientWorld.java`,
`IDhClientWorld.TICK_RATE_IN_MS`). It does not use the tick loop of Minecraft.

**The concurrency control on the data** uses a `ReentrantLock` for each section position
(`core/util/threading/PositionalLockProvider.java`), in `FullDataUpdaterV2`. The lock order is
the parent and then the child, with `tryLock` on a parent. Thus the propagator cannot
deadlock. `LodQuadTree.treeTickLock` also uses `tryLock`, and it skips a tick instead of
blocking, because `RenderBufferHandler` walks the tree at the same time in each frame.

---

## 6. Propagation of an update

This is the full path when a block changes, or when a chunk arrives again.

1. **Find the change.** A hook fires, and then `ChunkUpdateQueueManager.processQueuedChunkPreUpdate`
   compares the hash against the stored `ChunkHash` table. A chunk with no change stops here.
2. **Update the leaf.** `AbstractDhLevel.updateChunkAsync`, at line 150, builds a leaf
   `FullDataSourceV2` with `applyToParent = true` (`LodDataBuilder.java` line 79). It merges
   that source into the delayed-save cache in memory, which has a window of 1 second.
3. **Store the data and give notice.** At the flush, `onDataSourceSaveAsync` bakes the sky
   light. Then `FullDataUpdaterV2.updateDataSource`
   (`core/file/fullDatafile/V2/FullDataUpdaterV2.java` line 117) locks the position, loads the
   recipient section from SQLite, merges with `updateFromDataSource`, saves the DTO with
   `ApplyToParent=1`, and calls the `IDataSourceUpdateListenerFunc` listeners.
4. **Refresh the render.** `ClientLevelModule.OnDataSourceUpdated`
   (`core/level/ClientLevelModule.java` line 169) calls `LodQuadTree.queuePosToReload(pos)`.
   The next tick of the tree builds the mesh of that section again, *if that section has a
   buffer now* (`reloadQueuedSections`, `LodQuadTree.java` line 738). A section that moves out
   of range gets the flag `renderDataDirty` only, and the mod builds it again when it returns
   (`LodQuadTree.java` lines 282-291).
5. **Propagate up the pyramid.** The `ApplyToParent` flag is in the database row. Thus the
   propagation is **pull-based and crash-safe**.
   `core/file/fullDatafile/V2/FullDataUpdatePropagatorV2.java` polls every 250 ms.
   `repo.getPositionsToUpdate(...)` runs the SQL
   `WHERE ApplyToParent = 1 ORDER BY <manhattan distance to player> LIMIT n`
   (`core/sql/repo/FullDataSourceV2Repo.java` lines 435-480).

   It groups the dirty children by their parent, merges each child into the parent with the
   2 x 2 to 1 downsample from section 2.3, clears the flags of the children, sets the flag of
   the parent unless the parent is the root at detail 15, and saves. That save calls the
   listeners of step 4. Thus **the coarser render sections refresh as the wave goes up the
   pyramid**.

   There are at most 9 levels. Thus a block edit reaches the coarsest LOD after at most 9
   asynchronous steps, which the distance to the player prioritizes. `ApplyToChildren` with
   `runChildUpdates` do the same in the downward direction, for the optional function that
   fills holes by upsampling.

---

## The 20 classes that carry the design

| # | Path, relative to the repository | Role |
|---|---|---|
| 1 | `coreSubProjects/core/.../core/api/internal/SharedApi.java` | The chunk-update funnel, the world lifecycle, and the thread-pool lifecycle |
| 2 | `coreSubProjects/core/.../core/api/internal/chunkUpdating/ChunkUpdateQueueManager.java` | The 2-stage chunk queue with distance priority, the hash gate, and the overload limit |
| 3 | `coreSubProjects/core/.../core/api/internal/ClientApi.java` | Client connect and disconnect, replay of waiting chunks, the render entry, and the drain of the render-thread tasks |
| 4 | `common/src/.../common/wrappers/chunk/ChunkWrapper.java` | The chunk snapshot boundary, the heightmaps, and the hash of the blocks and biomes |
| 5 | `fabric/src/.../fabric/mixins/client/MixinClientPacketListener.java` (with `FabricClientProxy` and `MixinChunkMapCommon`) | The real Minecraft hooks: the packet chunk with light, the interactions, and the chunk save |
| 6 | `coreSubProjects/core/.../core/dataObjects/transformers/LodDataBuilder.java` | Chunk to column-RLE full data |
| 7 | `coreSubProjects/core/.../core/util/FullDataPointUtil.java` | The 64-bit packing of a full data point: id, height, minY and the lights |
| 8 | `coreSubProjects/core/.../core/dataObjects/fullData/sources/FullDataSourceV2.java` | The core LOD container, with the merge at the same level, one above and one below |
| 9 | `coreSubProjects/core/.../core/dataObjects/fullData/FullDataPointIdMap.java` | The block and biome palette for each section |
| 10 | `coreSubProjects/core/.../core/pos/DhSectionPos.java` | The packed section position and the arithmetic of the detail level |
| 11 | `coreSubProjects/core/.../core/level/AbstractDhLevel.java` (with `DhClientLevel` and `ClientLevelModule`) | `updateChunkAsync`, the delayed save, the storage of the chunk hash, and the render state |
| 12 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataSourceProviderV2.java` | Get and update of the sources in the database, and the loads of the adjacent strips |
| 13 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataUpdaterV2.java` | The locked merge, the save, and the dispatch to the listeners |
| 14 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataUpdatePropagatorV2.java` | The mip-pyramid propagation that the database flags drive |
| 15 | `coreSubProjects/core/.../core/sql/repo/AbstractDhRepo.java` (with `FullDataSourceV2Repo` and `sqlScripts/`) | The SQLite layer, the schema and the migrations |
| 16 | `coreSubProjects/core/.../core/file/structure/ClientOnlySaveStructure.java` | The database key for each server and dimension, and the server level keys |
| 17 | `coreSubProjects/core/.../core/generation/DhLightingEngine.java` | The standalone block and sky lighting for the LODs |
| 18 | `coreSubProjects/core/.../core/render/QuadTree/LodQuadTree.java` | The LOD quadtree centered on the player, the detail selection, and the load and reload |
| 19 | `coreSubProjects/core/.../core/render/QuadTree/LodRenderSection.java` | The build, transform, mesh and upload pipeline for each section |
| 20 | `coreSubProjects/core/.../core/dataObjects/transformers/FullDataToRenderDataTransformer.java` with `core/util/RenderDataPointUtil.java` | Full data to color render data, which is the second packed-long format |
| 21 | `coreSubProjects/core/.../core/dataObjects/render/bufferBuilding/{ColumnRenderBufferBuilder,ColumnBox,LodQuadBuilder,LodBufferContainer}.java` | Face culling, the greedy quad merge, the 16-byte vertex format, and the GPU upload |
| 22 | `coreSubProjects/core/.../core/render/{RenderBufferHandler,renderer/LodRenderer}.java` with `core/util/RenderUtil.java` and `shaders/{terrain,fade}/gl/*.frag` | The render list of a frame, the compositing of its own FBO, and the near clip, dither and vanilla fade for the overlap |
| 23 | `coreSubProjects/core/.../core/util/threading/{ThreadPoolUtil,PriorityTaskPicker}.java` with `core/render/RenderThreadTaskHandler.java` | The full model for threading and throttling |

---

## The properties that make a client-side-only mod operate

This is a checklist for a port to Vintage Story.

1. **Ingest only what the client holds already.** Do it at the moment when the light data is
   complete, which is the packet hook. Use the interaction events as a second signal of a
   change. Never block the render thread or the network thread. Make a snapshot of the chunk,
   and then leave.
2. **Store a content hash for each chunk.** Thus a chunk that arrives again is almost free,
   and the mod finds an edit without help from the server.
3. **Make each queue bounded and centered on the player.** This applies to the chunk queue,
   the mesh loads, and the propagation SQL with `ORDER BY distance LIMIT n`. The design loses
   work deliberately. It does not try to do all of it.
4. **Use one format that is canonical, compact and mergeable.** It is a palette, a vertical
   RLE, and a byte of completeness for each column. That one format is the storage and the
   source of the mips. The render data comes from it, and the mod can discard the render data.
5. **Store the dirty flags in the database.** Thus the pyramid stays consistent after a crash,
   and the mod needs no dependency graph in memory.
6. **Draw the parent in the quadtree until all children are ready**, and swap the buffers
   atomically. Thus there are no holes and no flicker.
7. **Solve the overlap with vanilla in screen space.** Use a near clip that is a fraction of
   the vanilla view distance, a dithered discard, and a crossfade of the vanilla image that
   uses the depth. Do not track which chunks the engine draws.
