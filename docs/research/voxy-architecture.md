# Voxy architecture report

This report extracts concepts for a clean-room reimplementation in C# for Vintage Story.

CAUTION: Voxy reserves all rights. This report gives *ideas and structures only*. Do not
copy code from the reference source.

All paths are relative to the root of the repository. The path
`src/main/java/me/cortex/voxy/...` becomes `voxy/...` here. The shaders are under
`src/main/resources/assets/voxy/shaders/`.

Voxy is a client-side Fabric mod in Java. It needs Sodium. Its license reserves all rights,
in `src/main/resources/fabric.mod.json`. Each statement here was examined against the source.

## 0. The 20 classes that carry the design

| Concern | Class | Path |
|---|---|---|
| Lifecycle/DI | `VoxyInstance` / `VoxyClientInstance` | `voxy/commonImpl/VoxyInstance.java`, `voxy/client/VoxyClientInstance.java` |
| World store | `WorldEngine` | `voxy/common/world/WorldEngine.java` |
| Section (32x32x32) | `WorldSection` | `voxy/common/world/WorldSection.java` |
| Section cache | `ActiveSectionTracker` | `voxy/common/world/ActiveSectionTracker.java` |
| Palette registry | `Mapper` | `voxy/common/world/other/Mapper.java` |
| Downsampling | `Mipper`, `WorldVoxilizedSectionMipper` | `voxy/common/world/other/Mipper.java`, `voxy/common/voxelization/WorldVoxilizedSectionMipper.java` |
| Chunk-to-voxel convert | `WorldConversionFactory`, `VoxelizedSection` | `voxy/common/voxelization/` |
| Update write path | `WorldUpdater` | `voxy/common/world/WorldUpdater.java` |
| Ingest service | `VoxelIngestService` | `voxy/common/world/service/VoxelIngestService.java` |
| Save service | `SectionSavingService` | `voxy/common/world/service/SectionSavingService.java` |
| Serialization | `SaveLoadSystem3` | `voxy/common/world/SaveLoadSystem3.java` |
| Storage abstraction | `StorageBackend` (with RocksDB and LMDB backends) | `voxy/common/config/storage/` |
| Thread pool | `UnifiedServiceThreadPool`, `ServiceManager`, `Service`, `MultiThreadPrioritySemaphore` | `voxy/common/thread/` |
| Model baking | `ModelFactory`, `ModelBakerySubsystem`, `SoftwareModelTextureBakery` | `voxy/client/core/model/` |
| Meshing | `RenderDataFactory`, `ScanMesher2D`, `RenderGenerationService` | `voxy/client/core/rendering/building/`, `voxy/client/core/util/ScanMesher2D.java` |
| Octree (CPU) | `NodeManager`, `NodeStore`, `AsyncNodeManager` | `voxy/client/core/rendering/hierachical/` |
| Octree (GPU) | `HierarchicalOcclusionTraverser`, `NodeCleaner` | same directory, and `shaders/lod/hierarchical/` |
| Draw backend | `MDICSectionRenderer` | `voxy/client/core/rendering/section/backend/mdic/MDICSectionRenderer.java` |
| GPU memory | `BasicAsyncGeometryManager`, `AllocationArena`, `UploadStream`, `DownloadStream` | `voxy/client/core/rendering/section/geometry/`, `voxy/common/util/AllocationArena.java`, `voxy/client/core/rendering/util/` |
| Update routing | `SectionUpdateRouter`, `RenderDistanceTracker` | `voxy/client/core/rendering/` |
| Vanilla blending | `BoundRenderer`, `NormalRenderPipeline` | `voxy/client/core/rendering/bounding/BoundRenderer.java`, `voxy/client/core/NormalRenderPipeline.java` |

---

## 1. Chunk ingestion, fully on the client

**Session hooks.** `voxy/client/mixin/minecraft/session/MixinClientPacketListener.java` sends
the login packet to `ClientSessionEvents.sessionStart()`. `MixinMinecraft.java` sends the
disconnect to `sessionEnd()`. These two hooks give the lifetime of a Voxy instance, in
`voxy/client/ClientSessionEvents.java`. There is no server-side component. All that follows
reads data that the client holds already.

**The chunk lifecycle of the renderer is the primary trigger, not the network layer.**
`voxy/client/mixin/sodium/MixinRenderSectionManager.java` injects three points.

1. **`onChunkAdded(x,z)`** gets the full client chunk and calls
   `VoxelIngestService.tryAutoIngestChunk(chunk)`. A chunk becomes ingestible at exactly the
   moment when it becomes renderable. The block states, the biomes and the light are all
   present by then.
2. **`onChunkRemoved(x,z)`** is the last chance to ingest the chunk before the unload. It goes
   through `voxy/client/ICheekyClientChunkCache.java` and
   `voxy/client/mixin/minecraft/MixinClientChunkCache.java`, which do not do the range checks.
   The location of the hook changes when the Bobby chunk-caching mod is present.
3. **A redirect on the upload path of the section geometry**, `voxy$updateOnUpload`. When
   Sodium meshes a section again, which occurs after any block update, Voxy ingests *that one
   16x16x16 section* again. It takes the `LevelChunkSection` and **copies** of the block-light
   and sky-light `DataLayer` objects. It does this only when the neighbour status shows that
   the section has a full surround, thus the light is stable.

**The edge case of a boundary block.**
`voxy/client/mixin/minecraft/MixinClientLevel.java` hooks `setBlocksDirty`. The remesh hook
above covers a change inside a section. But a change at a section boundary, at local
coordinate 0 or 15, needs the *neighbour* section to be ingested again. This mixin does that,
and only for a removal of a block, when `updated.isAir()`.

**Voxy captures three things for each 16x16x16 section.** They are the vanilla paletted
container of block states, the paletted container of biomes, and copies of the sky-light and
block-light arrays. It packs the light to one byte for each voxel, as `(sky | block<<4)`,
through `ILightingSupplier` closures
(`voxy/common/world/service/VoxelIngestService.java:59`).

**The ingest job** is `VoxelIngestService.processJob`. A `ConcurrentLinkedDeque` holds records
of `(cx,cy,cz, engine, section, blockLight, skyLight)`. A worker takes the newest record
first. It converts with `WorldConversionFactory.convert`, mips in place with
`WorldVoxilizedSectionMipper.mipSection`, and writes into the world with
`WorldUpdater.insertUpdate`.

Each enqueue increases a reference count on the `WorldEngine`. Thus an idle-world reaper
cannot free the engine during a job. A section that is fully air writes zeros immediately.
The explicit clear of air is important, because it removes old data.

This queue has no removal of duplicates. It depends on its very high scheduling weight of
5000 to stay empty.

**Fast palette conversion** is in
`voxy/common/voxelization/WorldConversionFactory.java`. It does not call
`getBlockState(x,y,z)` 4096 times. Instead it builds a small local lookup table from the
palette to the Voxy id, with a thread-local cache for each `Mapper`. Then it reads the raw
bit-packed storage words of the chunk directly, and decodes the indexes inline. It reads the
biomes one time for each 4 x 4 x 4 cell, into a cache of 64 entries.

This difference decides between free ingestion and a frame-time problem. For Vintage Story,
the equivalent is a direct read of the palette and data arrays of the chunk. Do not use the
accessor API for each block.

**Bulk import** is in `voxy/commonImpl/importers/WorldImporter.java` and `DHImporter.java`.
These offline importers read region files and Distant Horizons SQLite databases, and they
feed the same `insertUpdate` path. Limits on the work in flight control them: 10,000 chunks
and 100 Distant Horizons sections. A rate-limiter lambda also stops the import service
whenever the save queue holds more than 1200 items
(`voxy/commonImpl/VoxyInstance.java:43`).

---

## 2. World and LOD data model

**There are five stored mip levels of 32x32x32 sections.**
`WorldEngine.MAX_LOD_LAYER = 4` (`voxy/common/world/WorldEngine.java:15`). A `WorldSection`
(`voxy/common/world/WorldSection.java`) always holds **32 x 32 x 32 voxels**, as
`long[32768]`. At level L each voxel is 2^L blocks, thus a section at level L covers `32<<L`
blocks. Level 0 is 32x32x32 blocks, which is 2 x 2 x 2 vanilla chunk sections. Level 4 is
512x512x512 blocks. One flat array holds it, with the index `(y<<10)|(z<<5)|x`.

**A section key is one 64-bit long.** `WorldEngine.getWorldSectionId` is at line 91. The
level is in bits 60 to 63. A signed 8-bit `y` starts at bit 52. A signed 24-bit `z` starts at
bit 28. A signed 24-bit `x` starts at bit 4. The low 4 bits are spare.

This one long is the key in four places: the cache in memory, the database, the dirty
callbacks, and the node position on the GPU. It is one identifier through the full system.

**Each voxel is one 64-bit id** (`voxy/common/world/other/Mapper.java:68-95`):

- Bits 27 to 46, which is 20 bits, hold the **block id**. It is an index into a permanent
  registry of block states, to which Voxy only adds.
- Bits 47 to 55, which is 9 bits, hold the **biome id**.
- Bits 56 to 63, which is 8 bits, hold the **light**, as 4 bits of block light and 4 bits of
  sky light.
- Bits 0 to 26 have no use.

Air has all block bits at zero, and the light does not change this. Thus `isAir` is one mask
test.

**The `Mapper` is the palette idea.** Voxy gives a small dense id to each block state and
biome when it first sees it. It does this through `registerNewBlockState`, with a lock and a
concurrent map. It writes each new mapping immediately to the id-mapping keyspace of the database, as
serialized NBT, through `Mapper.StateEntry.serialize`.

Voxy never uses an id again for something else. Thus a stored section stays valid forever. At
load, a state that Voxy cannot parse gets a data fix, or it becomes air.

This is the important trick. It lets one voxel be one machine word that Voxy can compare.
Thus all meshing, mipping and change detection become a comparison of `long` values.

**Voxelization with an inline mip pyramid.** `VoxelizedSection`
(`voxy/common/voxelization/VoxelizedSection.java`) is one array,
`long[16^3 + 8^3 + 4^3 + 2^3 + 1]`. It holds a 16x16x16 chunk section and its 4 mip levels.
`WorldVoxilizedSectionMipper` fills the pyramid from the bottom up.

**The mip rule** is in `voxy/common/world/other/Mipper.java`. For each 2 x 2 x 2 cell it takes
the child with the **maximum opacity**. A fixed corner priority breaks a tie, and it prefers
the top corner. It forces the leaves to opacity 15, thus a forest stays solid at a distance.
If all 8 children are air, the result is air with the **average light**, and it rounds the
sky light up.

This is deliberately *not* an average of the content. A mip that takes a representative
sample keeps the hard boundaries between materials. It also does not invent a blended block.
Comments give planned improvements: mipping that knows about visibility, and an air bias that
depends on the level.

**The write path exits early.** `voxy/common/world/WorldUpdater.insertUpdate` runs for levels
0 to 4. It takes the section at `(x>>(lvl+1), y>>(lvl+1), z>>(lvl+1))` and copies the data of
that level from the pyramid into the correct subcube. It calculates `didStateChange` with a
comparison of the old and new longs, in an unrolled loop. **If a level had no change, the
climb stops.** Thus a torch does not touch level 4.

Each section also holds two counters. `nonEmptyBlockCount` exists at level 0 only.
`nonEmptyChildren` is an 8-bit mask of the occupancy of the octants. Voxy maintains it
atomically, and it sends it to the parents only when a section changes between empty and not
empty (`WorldSection.updateEmptyChildState`).

At a change, `WorldEngine.markDirty(section, flags, neighborMask)` occurs. The 6-bit mask
gives the face neighbours that the change touches. Voxy sets it only when the changed chunk is
at the section edge at that level. Thus the renderer can mesh the adjacent sections again.

**The cache in memory** is `voxy/common/world/ActiveSectionTracker.java`. It holds 64 striped
`Long2ObjectOpenHashMap` objects under `StampedLock` objects. The sections have a reference
count in a packed atomic int, where bit 0 means loaded and the upper bits hold the count.

The load occurs one time. The first thread to insert a holder loads from the storage, and the
other threads wait on the holder. A second **LRU of released sections**, with 1024 to 2048
entries, lets a new acquire miss the database. A global pool of approximately 400 reusable
`long[32768]` arrays removes approximately 100 MB of churn. An unload starts a save when the
section is dirty.

This is intricate lock-free code. In C#, a `ConcurrentDictionary` with a reference count, an
LRU and an `ArrayPool<long>` gives the same concepts with much less risk.

---

## 3. Storage

**The backends compose into a stack.**
`voxy/common/config/storage/StorageBackend.java` gives
`getSectionData(longKey, scratch)`, `setSectionData` and `deleteSectionData`. A second
keyspace holds the id mappings, in `voxy/common/config/IMappingStorage.java`. JSON configures
the layers, and they stack freely. Read `voxy/common/config/Serialization.java` and
`ConfigBuildCtx.java`, which substitute the token `{base_save_path}/{world_identifier}/storage/`.

**The default stack** is in `voxy/common/StorageConfigUtil.java:54-69`. It is
`SectionSerializationStorage`, then `CompressionStorageAdaptor` with ZSTD level 1 and no
dictionary, then **RocksDB**.

**The RocksDB backend** is
`voxy/common/config/storage/rocksdb/RocksDBStorageBackend.java`. It has 3 column families.
`world_sections` has **compression off**, because the layer above compressed the blobs
already. It has a bloom filter at 10 bits for each key, a 128 MB block cache, and
optimization for point lookups. `id_mappings` uses ZSTD. Voxy stores the section keys with
the bytes in reverse order. Thus the lexicographic order is the numeric order, which permits
iteration by a prefix for each level, in `iteratePositions`.

**The LMDB backend** is `voxy/common/config/storage/lmdb/LMDBStorageBackend.java`. It exists,
but it is not the default. It has 2 named databases with integer keys. The map grows by 33 MB
at `MDB_MAP_FULL`, with a lock sequence that makes all transactions stop first.

**`FragmentedStorageBackendAdaptor`** is in
`voxy/common/config/storage/other/FragmentedStorageBackendAdaptor.java`. It divides the
sections across N backends, where N is a power of 2, by a Stafford-mixed hash of the key. It
**copies the id mappings to each shard**, and it takes the majority answer at load. Thus it
survives corruption.

There are three more backends: `ReadonlyCachingLayer`, which is a read-through cache, an
in-memory backend, and a Redis backend.

**The blob format of a section** is in `voxy/common/world/SaveLoadSystem3.java`. Voxy builds a
palette for each section in one pass. The layout is
`[8B key][8B metadata][32768 x u16 palette-index][unique 64-bit voxel ids, first-seen order]`.
The metadata packs the palette size in 16 bits and `nonEmptyChildren` in 8 bits.

The encoder uses the coherence of the runs. It does a hash lookup only when the current voxel
is different from the previous voxel. Normal terrain has tens of unique ids. Thus 32768
voxels are approximately 64 KB before compression, and ZSTD-1 makes the u16 plane much
smaller. There is no checksum, which the source marks as a TODO. The deserialize is a fast
expansion of a lookup table. `BIGGEST_SERIALIZED_SECTION_SIZE = 524296` limits the
thread-local scratch buffer.

**The save pipeline** is `voxy/common/world/service/SectionSavingService.java`. A dirty
section goes into a lock-free deque, which an atomic `inSaveQueue` flag guards. **A section
enters the queue at most one time.** A second dirty mark while the section is in the queue
joins the first one. The queue holds a reference to the section, thus nothing can free it.

The backpressure has a soft limit of 5000. Above that limit, *the thread that enqueues takes a
save job and runs it itself*. The service limiter stops the importers when the queue holds
more than 1200 items.

**The world identity on the client for a server** is important for this project. Read
`voxy/client/VoxyClientInstance.getBasePath` and `voxy/commonImpl/WorldIdentifier.java`.
Multiplayer storage is at `<gameDir>/.voxy/saves/<server-ip>/<worldId>/storage/`. The value is
`worldId = SHA-256(clientVisibleSeed + dimensionKey)[:32 hex]`. It comes only from data that
the client receives. Thus the same server and dimension always give the same local database.
`voxy/commonImpl/mixin/minecraft/MixinWorld.java` stamps the construction of a dimension.

---

## 4. Rendering, which is the difference

The renderer is a **sparse-octree LOD system that the GPU drives**. The CPU maintains the
octree. In each frame the GPU walks the octree. It selects the LOD levels by the screen-space
error. It culls by occlusion. It makes its own draw commands. Then it *asks the CPU for the
geometry that it does not have, through a readback*. The meshes are greedy quads that Voxy built earlier. There
is no CPU geometry work in a frame, and there is no draw call for each section.

### 4.1 Geometry generation: greedy meshing on the CPU into 8-byte quads

The meshing is in `voxy/client/core/rendering/building/RenderDataFactory.java`, which has 1806
lines, and `voxy/client/core/util/ScanMesher2D.java`. It does a 2D greedy scan mesh for each
axis, over 32 x 32 slices. It merges identical quad payloads up to **16 x 16**.

It runs for each category: opaque, fluid and not opaque. Each category has an inner pass and
an "outer" pass. The outer pass takes the 32 x 32 boundary slabs of the six neighbour
sections, thus it can cull the faces across sections. Occupancy bitmasks, with one 32-bit
column mask for each row, make the test for the existence of a face an XOR of the adjacent
masks.

Face culling reads the baked metadata of the model: `faceOccludes`, `faceCanBeOccluded`,
`cullsSame` and `isFullyOpaque`, in `voxy/client/core/model/ModelQueries.java`.

**The quad format** is in `shaders/lod/quad_format.glsl`. One quad is **one uint64**:

- the face in 3 bits
- the size minus 1 in x and y, at 4 bits each
- the position x, y and z, at 5 bits each
- the model or state id in 16 bits
- the biome in 9 bits
- the light in 8 bits
 Voxy puts the
quads into **8 buckets**: translucent, double-sided, and the 6 axial face directions.

**The output** is `voxy/client/core/rendering/building/BuiltSection.java`. It holds these
fields:

- the position key
- a byte for the existence of the children
- a packed 30-bit AABB of the section, as 6 fields of 5 bits
- the quad buffer
- the 8 bucket offsets

**The build service** is
`voxy/client/core/rendering/building/RenderGenerationService.java`. It has a priority queue,
which puts the finer LODs first and moves a failed attempt down. A map with the position as
its key makes duplicate build requests **join**. A build acquires the `WorldSection` again and
copies its data. Thus the meshing never blocks the ingestion.

### 4.2 Block appearance without real models: the bakery

Read `voxy/client/core/model/ModelFactory.java`, `bakery/SoftwareModelTextureBakery.java` and
`bakery/SoftwareRasterizer.java`.

Software rasterizes each block state that appears, one time, orthographically, from each of
the 6 directions, into a 16 x 16 RGBA tile with depth. It uses the real baked model of the
game and the real texture atlas, which it reads back from the GPU.

The 6 tiles go into one large atlas, with 3 x 2 tiles for each model. There are 256 x 256
model slots, which is 65,536 models, in a 12288 x 8192 texture. The CPU generates the mips. A
flood-fill "solidify" step prevents a halo of transparent pixels, and a box downsample in
linear space follows. Read `voxy/client/core/model/MipGen.java` and `TextureUtils.java`.

The raster result also *gives* the metadata for the mesher. The occlusion for each face is
true when the face covers more than 90% of the pixels and the indentation is less than 0.1.
These are the other derived values:

- the bounding box of the face
- the depth of the indentation
- the need for an alpha cutout
- the translucency
- whether the face is double-sided
- whether the color depends on the biome
- the light emission
 Voxy finds the biome dependence by a probe
of the color provider with false biome getters.

A GPU record of 64 bytes for each model holds the UV bounds and flags for each face, and the
tint information, in `shaders/lod/block_model.glsl`. Voxy calculates the colors that depend on
the biome for each pair of model and biome, into a GPU lookup table. The draw indexes it by
the biome id of the voxel.

**Identical bakes join.** Voxy hashes the set of 6 textures. Thus states that look the same
share one model id, which also improves the greedy merge.

**The bake is lazy.** The mesher calls `getModelId(blockId)`. If the bake did not occur, the
call throws a preallocated `IdNotYetComputedException` with no stack trace. The build task
puts itself in the queue again. It asks for a bake of each state in the section, and of the
states at the neighbour boundary. Then it tries again. Thus a new block never stops the pipeline
(`RenderGenerationService.processJob:165-259`).

*Note for Vintage Story:* this subsystem exists because a Minecraft block model is an
arbitrary mesh. The block shapes of Vintage Story are similar in nature. Thus the concept
moves across directly. Bake 6 orthographic face impostors for each block one time. Take the
occlusion and tint metadata from the rasterization. Join the duplicates by a hash of the
content.

### 4.3 The hierarchical node system: a CPU octree with GPU traversal

**On the CPU**, `voxy/client/core/rendering/hierachical/NodeManager.java` and `NodeStore.java`
hold a flat array octree. Each node is 32 bytes, and there are at most 2^24 nodes. A node
holds the position key, a 24-bit pointer to the geometry, a 24-bit pointer to the children,
and flags for a request in flight. Sentinel values in the geometry pointer separate *no mesh
yet* from *meshed and empty*. The children have **contiguous** allocation, with a 3-bit count
and an 8-bit mask of the existence of the children, which comes from the world data.

The top-level nodes are level-4 sections. A ring tracker around the camera feeds them, in
`voxy/client/core/rendering/RenderDistanceTracker.java`. The rings are 512 blocks. The tracker
recenters after 128 blocks, and it processes 40 ring cells in each frame.

All changes to the octree run on a **dedicated thread**, in `AsyncNodeManager.java`. It
synchronizes to the render thread through a triple-buffered CAS hand-off. That hand-off holds
GPU **scatter-write batches**, where a compute shader patches individual 16-byte GPU nodes and
32-byte section-metadata records in place. It also holds GPU memcpy batches for the geometry
uploads. Thus the render thread never runs the octree logic.

**The GPU mirror** is in `shaders/lod/hierarchical/node.glsl`. Each node is 16 bytes: the
position as a uvec2, the geometry pointer with flags, and the child pointer with flags.

**The traversal** is `shaders/lod/hierarchical/traversal_dev.comp` with
`HierarchicalOcclusionTraverser.java`. It is breadth-first, with **one indirect compute
dispatch for each of the 5 octree levels**. Ping-pong queues start with the ids of the
top-level nodes. The metadata of a queue is also the indirect arguments of the next dispatch.

For each node it does a frustum test, then a **Hi-Z occlusion test**. That test projects the 8
corners of the AABB, selects a mip from the screen bounds, and compares against the depth
pyramid, in `screenspace.glsl`. Then it does **LOD selection by the area on the screen**. It
descends only when the projected area is more than `subDivisionSize` squared pixels. Voxy
tunes `subDivisionSize` automatically between 28 and 256 to hold 55 to 65 FPS, in
`VoxyRenderSystem.autoBalanceSubDivSize`. A node that it selects for the draw appends its
geometry pointer to the render list of the frame, and stamps `lastRenderFrame[nodeId]`.

**Feedback from the GPU to the CPU.** A node that Voxy *must* draw or split, but that has no
geometry or no children, appends its position to a small **request buffer**. The hard limit is
50 for each frame, and a soft limit decreases with the square of the mesh backlog. The node
marks itself as requested, thus duplicates do not occur. An asynchronous readback then takes
the buffer, in `voxy/client/core/rendering/util/DownloadStream.java`. `NodeManager` subscribes
to that section, meshes it, and scatter-writes the result.

**Thus Voxy loads, meshes and uploads only the geometry that the camera can see.** This
demand-driven loop is the core idea that makes it scale.

**Eviction** is `NodeCleaner.java` with `cleaner/*.comp`. When the free GPU geometry falls
below 256 MB, a compute pass partly sorts the 256 nodes that were visible least recently, by
`lastRenderFrame`. It reads them back and removes their geometry and their tree nodes. This is
an LRU that the GPU calculates.

### 4.4 Draw submission: vertex pulling with MDIC

Read `voxy/client/core/rendering/section/backend/mdic/MDICSectionRenderer.java` and
`shaders/lod/gl46/`.

`prep.comp` runs on 1 thread. It sets the counters to zero and writes the indirect arguments
for the dispatch and the draw.

A **raster occlusion pass**, `cull/raster.vert` with `cull/raster.frag`, draws the AABB cube of
each section in the render list against the depth buffer. It stamps
`visibilityData[section]=frameId` for each section that survives. The scheme covers two
frames. A section that becomes visible in this frame goes into a separate "temporal" bucket,
which Voxy draws later to hide the popping.

`cmdgen.comp` turns each visible section into **one draw command for each face-direction
bucket that is not empty**. It skips a bucket that faces away from the camera, thus
approximately half of all quads never reach the vertex shader.

A translucent section goes into a histogram of 1024 buckets by the distance from the camera.
A GPU prefix sum and `buildtranslucents.comp` then give a **draw list for each section, sorted
from back to front**. This is coarse, but it is sufficient for LOD water.

The draw uses **`glMultiDrawElementsIndirectCount`**. Thus the full LOD world needs
approximately 3 CPU draw calls: opaque, temporal and translucent. The budgets are 400k, 100k
and 100k commands.

**There are no vertex buffers.** One shared index buffer holds a repeating pattern of 6
indexes for a quad, in `voxy/client/core/rendering/util/SharedIndexBuffer.java`. The vertex
shader `gl46/quads3.vert` gets the uint64 quad through `gl_VertexID>>2`. It calculates the
position of the corner from the packed position, size and face, with the LOD scale. It packs
all flat attributes on the provoking vertex only.

The fragment shader `gl46/quads.frag` builds the tiled UVs into the atlas cell of the model
with `textureGrad`. It does the alpha cutout. It does the biome tint, with a per-quad LUT
color and a per-pixel refinement by a grayscale mask. It does the directional shading of the
face.

**The GPU geometry memory** is one large SSBO. Where the driver supports it, the buffer is
sparse and Voxy commits pages on demand. A best-fit free list with coalescing of the
neighbours suballocates it, in `voxy/common/util/AllocationArena.java`, which uses packed
`(size,addr)` red-black sets. The bookkeeping runs on the asynchronous thread. The bytes
stream through a 64 MB upload ring that stays mapped and that a fence reclaims, in
`voxy/client/core/rendering/util/UploadStream.java`. A 32 MB download ring handles the
readbacks.

A CPU-side `GeometryCache` of 4 GB, in `voxy/client/core/rendering/GeometryCache.java`, keeps
the built sections that Voxy removed. Thus a return to that place needs an upload only, and no
new mesh.

### 4.5 Blending with vanilla terrain, and translucency at the seam

**The depth-bounding buffer** is
`voxy/client/core/rendering/bounding/BoundRenderer.java` with
`voxy/client/mixin/sodium/MixinVisibleChunkCollector.java`. In each frame, Voxy streams the
exact set of vanilla sections that are built and visible into a depth-only buffer. It does
this by rasterizing the **back faces** of their AABBs.

In `quads.frag`, Voxy discards each LOD fragment whose depth is nearer than that bound. Thus
LOD terrain can never draw over the real terrain or through it. A hole in the vanilla
coverage, from an unloaded chunk, fills with LOD automatically.

**The depth and stencil bridge** is
`voxy/client/core/AbstractRenderPipeline.initDepthStencil` with
`shaders/post/setup_stencil_depth.frag`. Voxy copies the vanilla depth into its own D24S8
target, and the stencil marks the pixels that vanilla covers. Thus the LOD draws into the gaps
only.

The final composite is `post/blit_texture_depth_cutout.frag` with
`NormalRenderPipeline.finish`. It projects the Voxy depth into the vanilla projection, applies
the environmental fog and SSAO, and alpha-blends into the vanilla framebuffer.

Voxy uses its own projection, with a near plane at 16 and a far plane at 48,000 blocks. The
near plane starts *at* the vanilla boundary. That is what makes a far plane of 3000 chunks
safe for precision.

Translucent LOD, which is the water, draws after the opaque pass with standard alpha blending.
The same depth-bound discard applies to it. Thus the water plane meets the vanilla water
without a double blend.

### 4.6 Why it is approximately 10 times Distant Horizons

1. The frame cost is O(visible nodes on the GPU), and not O(loaded LODs on the CPU). The
   traversal, the LOD selection, the occlusion, the command generation and the sorting all run
   in compute. The CPU issues an approximately constant number of draw calls.
2. Quads of 8 bytes with vertex pulling use approximately one tenth of the geometry memory and
   bandwidth of a mesh with vertex buffers. The greedy merge to 16 x 16 comes on top of that.
3. The loading depends on demand. The visibility result of the GPU decides what Voxy meshes.
   Voxy never builds what is outside the frustum and the occlusion set.
4. Hi-Z, the raster AABB occlusion, and the removal of backfacing buckets for each direction
   together remove most of the overdraw before the shading.
5. The LOD threshold uses the screen-space error, with a servo on the FPS. Thus the resolution
   decreases, and the frame rate does not.

---

## 5. Threading model

Read `voxy/common/thread/`. One **unified worker pool** serves all services. The default size
is `coreCount/1.5` threads, at Java priority 3.

A service, in `Service.java`, is a source of jobs with a semaphore count, a **weight** and an
optional boolean **limiter**. The scheduler `ServiceManager.runAJob0` selects the next job by
**weighted random sampling, proportional to `pendingJobs x weight`**. The weights are: ingest
5000, saving 100, mesh generation 10, Distant Horizons import 10, and world import 3.

The effects are clear. Voxy empties the ingest queue almost immediately. Meshing fills the
idle time. An import runs only when there is spare time. One pool balances all of this, and no
service needs its own thread tuning.

These parts support the pool.

- **`MultiThreadPrioritySemaphore`** lets a *foreign* thread pool give its idle blocking time
  to Voxy. Voxy impersonates the queue semaphore of the chunk-builder threads of Sodium, in
  `voxy/client/mixin/sodium/MixinChunkJobQueue.java`. Thus while those threads wait for Sodium
  work, they run Voxy jobs. They prefer their own work when it arrives, with a poll of 10 ms.
- **`PerThreadContextExecutor`** gives a context object to each pair of service and worker
  thread. A context holds scratch buffers, the state of the mesher and the database
  statements. A weak map drives the cleanup with the GC. Thus a service gets thread-local
  state without a leak from a ThreadLocal.
- **The backpressure**, in summary: the save queue has a soft limit of 5000, above which the
  caller runs the job itself. The importer limiter starts when the save queue holds more than
  1200 items. The mesh queue removes duplicates by position. The application of the geometry
  results has three limits for each iteration of the octree thread. They are at most 300
  results, at least 50 MB of free GPU memory, and approximately 1 MB of uploads. The soft limit of the GPU
  request queue decreases with the mesh backlog. Each stage that can flood the stage below it
  has its own valve.
- Three dedicated threads are outside the pool. They are the octree thread in
  `AsyncNodeManager`, the model-bake thread in `ModelBakerySubsystem`, and an idle-world
  reaper with a 10-second timeout in `VoxyInstance`.
- CPU-affinity code exists, in `voxy/common/util/cpu/CpuLayout.java`, and it knows about
  P-cores and E-cores. But nothing connects it to the workers now.

---

## 6. Ideas that are worth a move into C#

1. **One `long` for each voxel**, as blockId, biome and light, with air as zero block bits.
   Thus mipping, comparison, meshing and serialization are all integer operations. In C# this
   is a `ulong[]` with bit operations, which is identical.
2. **A permanent id registry to which the mod only adds**, the `Mapper`, stored beside the
   sections. The mod never uses an id again for something else, thus a stored world never
   becomes invalid. Copy the mappings across the shards and take the majority answer, if the
   design uses shards.
3. **Sections of 32x32x32 at each mip level, with one code path for all levels**, keyed by one
   packed long. Each section keeps an 8-bit mask of the occupancy of its children. The mod
   maintains that mask step by step, and sends it up only at a change between empty and not
   empty.
4. **A mip write that finds changes and exits early**, as `WorldUpdater` does. Compare during
   the copy. Stop the climb through the levels when nothing changed. Give a 6-bit neighbour
   mask for a remesh at the boundary.
5. **Downsampling that takes a representative sample**: select the corner with the maximum
   opacity, force the leaves to opaque, and average the light for air. This is cheap and it
   looks correct. Do not average the materials.
6. **Streaming palette serialization**: a plane of u16 indexes, a lookup table in first-seen
   order, and a shortcut for the previous run, then ZSTD-1. Set the database compression to
   off for that column family. The encode and the decode take less than a millisecond.
7. **Meshing that depends on demand, through GPU feedback.** The visibility pass itself asks
   for the geometry that is absent, through a small request buffer with a limit. A requested
   flag on the node removes the duplicates. The mod never builds what is invisible. The
   concept moves even without compute traversal: the culling result is the load queue.
8. **Updates that a subscription routes**, through `SectionUpdateRouter`. A dirty event of the
   world reaches the renderer only for a section that an octree node watches. The watch set is
   the set of resident nodes. Thus the world engine and the renderer are fully separate, and
   the engine has one `dirtyCallback` only.
9. **Quads of 8 bytes, vertex pulling, one shared index buffer, and buckets for each face
   direction.** Add backface culling of the buckets on the GPU, and MultiDrawIndirectCount.
   On a modern GL or Vulkan-class API in the engine of Vintage Story, this is the largest
   single gain. The fallback ladder is: persistent-thread traversal, then indirect dispatches for each
   level, then CPU traversal with GPU culling, then all on the CPU.
10. **Raster AABB occlusion over two frames with a temporal bucket**, and a **Hi-Z pyramid**,
    for culling at the node level. Servo the LOD threshold of the screen-space error to the
    FPS.
11. **A depth-bounding buffer built from the real set of visible vanilla chunks.** Capture
    that set from the visibility walk of the renderer, and discard in the fragment shader.
    This is the best known solution for the seam between the LOD and the real terrain. It
    also covers the holes and the water.
12. **Bake 6 orthographic face impostors for each block state one time.** Join the duplicates
    by a hash of the content. Take the occlusion metadata from the bake and give it to the
    mesher. Make the bake lazy, with an exception that puts the job in the queue again.
13. **An asynchronous octree thread with a triple-buffered hand-off and GPU scatter-write
    patching.** The render thread applies batched byte patches, and it never runs the tree
    logic.
14. **A unified job pool with weighted random selection**, with a weight and a limiter for
    each service, and backpressure where the caller takes the work. C# expresses this easily
    over a semaphore and workers that do not depend on the `ThreadPool`.
15. **Memory hygiene.** Use these:

    - pooled section arrays
    - thread-local scratch buffers
    - an LRU of released sections
    - a 4 GB cache of built geometry, keyed by the section position, which the mod empties at
      a world change
    - a free-list arena with coalescing, for the GPU memory
    - leak detectors on each native handle, which the GC drives

    In C# the last item is `SafeHandle` and assertions in a finalizer.

**Known gaps in Voxy.** Do not take these. There are no checksums on the blobs. The baking of
a block entity, such as a chest, is not complete. The iteration of positions in LMDB has no
implementation. The ingest queue has no limit and no removal of duplicates. The ambient
occlusion that uses the occupancy is off. Several heuristics are at the quality of a TODO,
which include the mip rule and the magic thresholds for the occlusion. A new design can make
those choices more deliberately.
