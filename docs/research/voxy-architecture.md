# Voxy Architecture Report — Clean-Room Concept Extraction for a Vintage Story Reimplementation

> NOTE: Voxy is All-Rights-Reserved. This report describes *ideas and structures only* for a
> clean-room C# reimplementation — no code may be copied from the reference source.

All paths relative to the repo root (`src/main/java/me/cortex/voxy/...` abbreviated to `voxy/...`; shaders under `src/main/resources/assets/voxy/shaders/`). Voxy is Fabric/Java, client-side, Sodium-dependent, license All-Rights-Reserved (`src/main/resources/fabric.mod.json`) — this report describes *ideas and structures only*, verified against the source.

## 0. The ~20 load-bearing classes

| Concern | Class | Path |
|---|---|---|
| Lifecycle/DI | `VoxyInstance` / `VoxyClientInstance` | `voxy/commonImpl/VoxyInstance.java`, `voxy/client/VoxyClientInstance.java` |
| World store | `WorldEngine` | `voxy/common/world/WorldEngine.java` |
| Section (32³) | `WorldSection` | `voxy/common/world/WorldSection.java` |
| Section cache | `ActiveSectionTracker` | `voxy/common/world/ActiveSectionTracker.java` |
| Palette registry | `Mapper` | `voxy/common/world/other/Mapper.java` |
| Downsampling | `Mipper`, `WorldVoxilizedSectionMipper` | `voxy/common/world/other/Mipper.java`, `voxy/common/voxelization/WorldVoxilizedSectionMipper.java` |
| Chunk→voxel convert | `WorldConversionFactory`, `VoxelizedSection` | `voxy/common/voxelization/` |
| Update write path | `WorldUpdater` | `voxy/common/world/WorldUpdater.java` |
| Ingest service | `VoxelIngestService` | `voxy/common/world/service/VoxelIngestService.java` |
| Save service | `SectionSavingService` | `voxy/common/world/service/SectionSavingService.java` |
| Serialization | `SaveLoadSystem3` | `voxy/common/world/SaveLoadSystem3.java` |
| Storage abstraction | `StorageBackend` (+ RocksDB/LMDB backends) | `voxy/common/config/storage/` |
| Thread pool | `UnifiedServiceThreadPool`, `ServiceManager`, `Service`, `MultiThreadPrioritySemaphore` | `voxy/common/thread/` |
| Model baking | `ModelFactory`, `ModelBakerySubsystem`, `SoftwareModelTextureBakery` | `voxy/client/core/model/` |
| Meshing | `RenderDataFactory`, `ScanMesher2D`, `RenderGenerationService` | `voxy/client/core/rendering/building/`, `voxy/client/core/util/ScanMesher2D.java` |
| Octree (CPU) | `NodeManager`, `NodeStore`, `AsyncNodeManager` | `voxy/client/core/rendering/hierachical/` |
| Octree (GPU) | `HierarchicalOcclusionTraverser`, `NodeCleaner` | same dir + `shaders/lod/hierarchical/` |
| Draw backend | `MDICSectionRenderer` | `voxy/client/core/rendering/section/backend/mdic/MDICSectionRenderer.java` |
| GPU memory | `BasicAsyncGeometryManager`, `AllocationArena`, `UploadStream`, `DownloadStream` | `voxy/client/core/rendering/section/geometry/`, `voxy/common/util/AllocationArena.java`, `voxy/client/core/rendering/util/` |
| Update routing | `SectionUpdateRouter`, `RenderDistanceTracker` | `voxy/client/core/rendering/` |
| Vanilla blending | `BoundRenderer`, `NormalRenderPipeline` | `voxy/client/core/rendering/bounding/BoundRenderer.java`, `voxy/client/core/NormalRenderPipeline.java` |

---

## 1. Chunk ingestion — fully client-side capture

**Session hooks.** `voxy/client/mixin/minecraft/session/MixinClientPacketListener.java` (login packet → `ClientSessionEvents.sessionStart()`) and `MixinMinecraft.java` (disconnect → `sessionEnd()`) bracket a Voxy instance's lifetime (`voxy/client/ClientSessionEvents.java`). No server-side component exists; everything below reads data the client already has.

**Primary trigger is the renderer's chunk lifecycle, not the network layer.** `voxy/client/mixin/sodium/MixinRenderSectionManager.java` injects three points:
1. **`onChunkAdded(x,z)`** → fetch the full client chunk and `VoxelIngestService.tryAutoIngestChunk(chunk)` — a chunk becomes ingestible exactly when it becomes renderable (block states, biomes, and light are all present by then).
2. **`onChunkRemoved(x,z)`** → last-chance ingest of the chunk before unload (via `voxy/client/ICheekyClientChunkCache.java` + `voxy/client/mixin/minecraft/MixinClientChunkCache.java`, which bypasses range checks; the hook location flips depending on whether the Bobby chunk-caching mod is installed).
3. **A redirect on the section-geometry-upload path (`voxy$updateOnUpload`)** → when Sodium remeshes a section (i.e. after any block update), Voxy re-ingests *that single 16³ section*, grabbing the `LevelChunkSection` plus **copies** of the block-light and sky-light `DataLayer`s (only when neighbor status shows the section is surrounded, so light is settled).

**Boundary-block edge case.** `voxy/client/mixin/minecraft/MixinClientLevel.java` hooks `setBlocksDirty`: interior changes are covered by the remesh hook above, but a change at a section boundary (local coord 0 or 15) requires re-ingesting the *neighboring* section, which this mixin does (and only for block removals — `updated.isAir()`).

**What is captured per 16³ section:** the vanilla paletted block-state container, the paletted biome container, and copied sky/block light arrays. Light is packed to one byte per voxel `(sky | block<<4)` via `ILightingSupplier` closures (`voxy/common/world/service/VoxelIngestService.java:59`).

**Ingest job** (`VoxelIngestService.processJob`): a `ConcurrentLinkedDeque` of `(cx,cy,cz, engine, section, blockLight, skyLight)` records; workers pop LIFO, convert (`WorldConversionFactory.convert`), mip in place (`WorldVoxilizedSectionMipper.mipSection`), and write into the world (`WorldUpdater.insertUpdate`). Each enqueue ref-counts the `WorldEngine` so an idle-world reaper can't free it mid-flight. Empty all-air sections short-circuit to a zeroed write (explicit air clears matter — they erase stale data). No dedup on this queue; it relies on its very high scheduling weight (5000) to stay drained.

**Fast palette conversion** (`voxy/common/voxelization/WorldConversionFactory.java`): rather than calling `getBlockState(x,y,z)` 4096 times, it builds a small **local palette→Voxy-id LUT** (with a thread-local per-`Mapper` cache) and then walks the chunk's *raw bit-packed storage words* directly, decoding indices inline. Biomes are read once per 4×4×4 cell into a 64-entry cache. This is the difference between ingestion being free and being a frame-time problem — for Vintage Story, the analog is reading the chunk's palette/data arrays directly instead of the per-block accessor API.

**Bulk import** (`voxy/commonImpl/importers/WorldImporter.java`, `DHImporter.java`): offline importers for region files and Distant Horizons SQLite DBs feed the same `insertUpdate` path, throttled by in-flight caps (10 000 chunks / 100 DH sections) and by a rate-limiter lambda that suspends the import service whenever the save queue exceeds 1200 (`voxy/commonImpl/VoxyInstance.java:43`).

---

## 2. World / LOD data model

**Five stored mip levels of 32³ sections.** `WorldEngine.MAX_LOD_LAYER = 4` (`voxy/common/world/WorldEngine.java:15`). A `WorldSection` (`voxy/common/world/WorldSection.java`) is always **32×32×32 voxels** (`long[32768]`); at level L each voxel represents `2^L` blocks, so a level-L section spans `32<<L` blocks (level 0 = 32³ blocks = 2×2×2 vanilla chunk-sections; level 4 = 512³ blocks). One flat array, index `(y<<10)|(z<<5)|x`.

**Section key = one 64-bit long** (`WorldEngine.getWorldSectionId`, line 91): `lvl` in bits 60–63, signed 8-bit `y` at 52, signed 24-bit `z` at 28, signed 24-bit `x` at 4, 4 spare low bits. This single long is the key for the in-memory cache, the DB, the dirty callbacks, and the GPU node position — one identifier through the entire system.

**Per-voxel encoding = one 64-bit id** (`voxy/common/world/other/Mapper.java:68-95`):
- bits 27–46 (20 bits): **block id** — index into a persistent, append-only blockstate registry
- bits 47–55 (9 bits): **biome id**
- bits 56–63 (8 bits): **light** (4-bit block | 4-bit sky)
- bits 0–26: unused. Air = block bits all zero regardless of light, so `isAir` is a single mask test.

**The `Mapper` is the palette idea.** Block states and biomes are interned into small dense ids on first sight (lock + concurrent map, `registerNewBlockState`), and each new mapping is immediately persisted to the DB's id-mapping keyspace as serialized NBT (`Mapper.StateEntry.serialize`). Ids are never reused, so stored sections stay valid forever; on load, unparseable states are data-fixed or degrade to air. This is the crucial trick that lets a voxel be one comparable machine word: *all* meshing, mipping, and change-detection reduces to `long` compares.

**Voxelization + inline mip pyramid.** `VoxelizedSection` (`voxy/common/voxelization/VoxelizedSection.java`) is a single `long[16³+8³+4³+2³+1]` holding a 16³ chunk section plus its 4 mip levels. `WorldVoxilizedSectionMipper` fills the pyramid bottom-up.

**Mip rule** (`voxy/common/world/other/Mipper.java`): for each 2×2×2 cell, pick the child with **maximum opacity**, tie-broken by a fixed corner-priority (favoring the top corner); leaves are forced to opacity 15 so forests stay solid at distance. If all 8 children are air, the result is air with **averaged light** (skylight rounded up). Deliberately *not* an average of content — a representative-sample mip keeps hard material boundaries and avoids inventing blended blocks. (Comments show planned refinements: visibility-aware mipping, level-dependent air bias.)

**Write path with early-out propagation** (`voxy/common/world/WorldUpdater.insertUpdate`): for lvl 0..4, acquire the section at `(x>>(lvl+1), y>>(lvl+1), z>>(lvl+1))` and blit the pyramid's level-lvl data into the right subcube, computing `didStateChange` by comparing old vs new longs in an unrolled loop. **If a level saw no change, stop climbing** — a torch placement doesn't touch level 4. Alongside, each section maintains:
- `nonEmptyBlockCount` (level 0 only) and
- `nonEmptyChildren` — an 8-bit octant-occupancy mask maintained atomically and bubbled to parents only on empty↔nonempty transitions (`WorldSection.updateEmptyChildState`).
On change, `WorldEngine.markDirty(section, flags, neighborMask)` fires with a 6-bit mask of which face-neighbors the change touches (only set when the changed chunk borders the section edge at that level) so the renderer can remesh adjacent sections.

**In-memory cache** (`voxy/common/world/ActiveSectionTracker.java`): 64-way striped `Long2ObjectOpenHashMap`s under `StampedLock`s; ref-counted sections (packed atomic int: bit 0 = loaded, upper bits = refcount); load-once semantics (first thread to insert a holder loads from storage, others spin-wait on the holder); a secondary **LRU of released sections** (1024–2048 entries) so re-acquire skips the DB; and a global pool of ~400 reusable `long[32768]` arrays (~100 MB churn eliminated). Unload triggers a save if dirty. This is intricate lock-free code — in C#, a `ConcurrentDictionary` + refcount + LRU + `ArrayPool<long>` gets the same concepts with far less peril.

---

## 3. Storage

**Composable backend stack** (`voxy/common/config/storage/StorageBackend.java`): `getSectionData(longKey, scratch)` / `setSectionData` / `deleteSectionData` plus a second keyspace for id-mappings (`voxy/common/config/IMappingStorage.java`). Layers are JSON-configured and freely stacked (`voxy/common/config/Serialization.java`, `ConfigBuildCtx.java` with `{base_save_path}/{world_identifier}/storage/` token substitution):

- **Default stack** (`voxy/common/StorageConfigUtil.java:54-69`): `SectionSerializationStorage` → `CompressionStorageAdaptor(ZSTD level 1, no dictionary)` → **RocksDB**.
- **RocksDB backend** (`voxy/common/config/storage/rocksdb/RocksDBStorageBackend.java`): 3 column families — `world_sections` with **compression disabled** (blobs pre-compressed above), bloom filter (10 bits/key), 128 MB block cache, point-lookup-optimized; `id_mappings` with ZSTD. Section keys stored byte-reversed so lexicographic order = numeric order, enabling per-level prefix iteration (`iteratePositions`).
- **LMDB backend** (`voxy/common/config/storage/lmdb/LMDBStorageBackend.java`): exists but is not the default; 2 named integer-keyed DBs, map grown by 33 MB on `MDB_MAP_FULL` with a quiesce-all-transactions lock dance.
- **FragmentedStorageBackendAdaptor** (`voxy/common/config/storage/other/FragmentedStorageBackendAdaptor.java`): shards sections across N (power-of-2) backends by a Stafford-mixed hash of the key; id-mappings are **replicated to every shard** and majority-voted on load for corruption resilience.
- Also: `ReadonlyCachingLayer` (read-through cache backend), in-memory backend, Redis backend.

**Section blob format** (`voxy/common/world/SaveLoadSystem3.java`) — a per-section palette, built in one pass:
`[8B key][8B metadata][32768 × u16 palette-index][unique 64-bit voxel ids, first-seen order]`.
Metadata packs palette size (16 bits) + `nonEmptyChildren` (8 bits). The encoder exploits run coherence (only does a hash lookup when the current voxel differs from the previous). Typical terrain has tens of unique ids, so 32768 voxels ≈ 64 KB pre-compression, and ZSTD-1 crushes the u16 plane further. No checksum (a TODO). Deserialize is a trivially fast LUT expansion. `BIGGEST_SERIALIZED_SECTION_SIZE = 524 296` bounds the thread-local scratch buffer.

**Save pipeline** (`voxy/common/world/service/SectionSavingService.java`): dirty sections enter a lock-free deque guarded by an atomic `inSaveQueue` flag (**a section is enqueued at most once**; re-dirtying while queued is coalesced); the queue holds a section ref so it can't be freed. Backpressure: soft cap 5000, above which the *enqueuing thread steals and executes save jobs itself*; importers are gated at queue > 1200 via the service limiter.

**Client-side world identity for servers** — critical for our use case (`voxy/client/VoxyClientInstance.getBasePath`, `voxy/commonImpl/WorldIdentifier.java`): multiplayer storage lives at `<gameDir>/.voxy/saves/<server-ip>/<worldId>/storage/`, where `worldId = SHA-256(clientVisibleSeed + dimensionKey)[:32 hex]` — computed purely from data the client receives, so the same server+dimension always maps to the same local DB. Dimension construction is stamped via `voxy/commonImpl/mixin/minecraft/MixinWorld.java`.

---

## 4. Rendering — the differentiator

The renderer is a **GPU-driven sparse-octree LOD system**: the CPU maintains the octree; the GPU traverses it every frame, picks LOD levels by screen-space error, occlusion-culls, generates its own draw commands, and *requests missing geometry back to the CPU via readback*. Meshes are pre-built greedy quads; there is no per-frame CPU geometry work and no per-section draw call.

### 4.1 Geometry generation — CPU greedy meshing into 8-byte quads

- **Meshing** (`voxy/client/core/rendering/building/RenderDataFactory.java`, 1806 lines; `voxy/client/core/util/ScanMesher2D.java`): per-axis 2D greedy scan meshing over 32×32 slices, merging identical quad payloads up to **16×16**. Run per category: opaque / fluid / non-opaque, each with an inner pass and an "outer" pass that pulls the six neighboring sections' 32×32 boundary slabs for cross-section face culling. Occupancy bitmasks (one 32-bit column mask per row) make the face-existence test an XOR of adjacent masks. Face culling consults baked model metadata (`faceOccludes`, `faceCanBeOccluded`, `cullsSame`, `isFullyOpaque` — `voxy/client/core/model/ModelQueries.java`).
- **Quad format** (`shaders/lod/quad_format.glsl`): one quad = **one uint64**: face (3b), size-1 x/y (4b+4b), position x/y/z (5b each), model/state id (16b), biome (9b), light (8b). Quads are binned into **8 buckets**: translucent, double-sided, and the 6 axial face directions.
- **Output** (`voxy/client/core/rendering/building/BuiltSection.java`): position key, child-existence byte, a packed 30-bit section AABB (6×5 bits), the concatenated quad buffer, and the 8 bucket offsets.
- **Build service** (`voxy/client/core/rendering/building/RenderGenerationService.java`): priority queue (finer LODs first, failed attempts deprioritized) + a position-keyed map so duplicate build requests **coalesce**; builds re-acquire the `WorldSection` and copy its data, so meshing never blocks ingestion.

### 4.2 Block appearance without real models — the bakery

(`voxy/client/core/model/ModelFactory.java`, `bakery/SoftwareModelTextureBakery.java`, `bakery/SoftwareRasterizer.java`.) Every block state that appears is **software-rasterized once, orthographically, from each of the 6 directions, into a 16×16 RGBA+depth tile** using the game's real baked model + the real texture atlas read back from the GPU. The 6 tiles go into one big atlas (3×2 tiles per model, 256×256 model slots = 65 536 models, 12288×8192 texture) with CPU-generated mips (flood-fill "solidify" to prevent transparent-halo bleed, linear-space box downsample — `voxy/client/core/model/MipGen.java`, `TextureUtils.java`). From the raster result it also *derives* the mesher metadata: per-face occlusion (face covers >90% of pixels and indentation < 0.1), face bounding box, indentation depth, alpha-cutout need, translucency, double-sidedness, biome-tint dependence (probing the color provider with fake biome getters), light emission. A 64-byte GPU record per model carries per-face UV bounds/flags plus tint info (`shaders/lod/block_model.glsl`); biome-dependent colors are precomputed per (model, biome) into a GPU LUT indexed at draw time by the voxel's biome id.

**Identical bakes deduplicate**: the 6-texture set is hashed, and visually identical states alias one model id — which also improves greedy merging. **Lazy baking**: the mesher calls `getModelId(blockId)`; if unbaked it throws a preallocated no-stacktrace `IdNotYetComputedException`, the build task requeues itself, requests bakes for every state in the section (and neighbor-boundary states), and retries — the pipeline never stalls on a novel block (`RenderGenerationService.processJob:165-259`).

*Vintage Story note:* this whole subsystem exists because MC block models are arbitrary meshes. VS block shapes are similar in spirit; the concept transfers directly — bake each block's 6 orthographic face impostors once, derive occlusion/tint metadata from the rasterization, dedupe by content hash.

### 4.3 The hierarchical node system (CPU octree, GPU traversal)

- **CPU** (`voxy/client/core/rendering/hierachical/NodeManager.java`, `NodeStore.java`): flat array octree, 32 bytes/node, ≤2²⁴ nodes; each node = position key, 24-bit geometry ptr (sentinels distinguish *no mesh yet* vs *meshed-and-empty*), 24-bit child ptr with children **allocated contiguously** (3-bit count, 8-bit child-existence mask from the world data), request-in-flight flags. Top-level nodes are level-4 sections fed by a ring tracker around the camera (`voxy/client/core/rendering/RenderDistanceTracker.java` — 512-block rings, recenter after 128 blocks, 40 ring cells processed per frame).
- All octree mutation runs on a **dedicated thread** (`AsyncNodeManager.java`), which syncs to the render thread via a triple-buffered CAS hand-off containing GPU **scatter-write batches** (a compute shader patches individual 16-byte GPU nodes and 32-byte section-metadata records in place) and GPU-side memcpy batches for geometry uploads — the render thread never touches octree logic.
- **GPU mirror** (`shaders/lod/hierarchical/node.glsl`): 16 bytes/node (position uvec2, geometry ptr+flags, child ptr+flags).
- **Traversal** (`shaders/lod/hierarchical/traversal_dev.comp` + `HierarchicalOcclusionTraverser.java`): breadth-first, **one indirect compute dispatch per octree level (5)**, ping-pong queues seeded with the top-level node ids; queue metadata doubles as the next dispatch's indirect args. Per node: frustum test, **Hi-Z occlusion test** (project the 8 AABB corners, pick a mip from the screen bounds, compare against the depth pyramid — `screenspace.glsl`), and **LOD selection by screen-space area**: descend iff projected area > `subDivisionSize²` pixels, where `subDivisionSize` is auto-tuned 28–256 to hold 55–65 FPS (`VoxyRenderSystem.autoBalanceSubDivSize`). Nodes selected for render append their geometry ptr to the frame's render list and stamp `lastRenderFrame[nodeId]`.
- **GPU→CPU feedback**: a node that *should* be rendered/split but lacks geometry/children appends its position to a tiny **request buffer** (hard cap 50/frame, soft cap shrunk quadratically as the mesh backlog grows), marks itself requested to dedupe, and the buffer is asynchronously read back (`voxy/client/core/rendering/util/DownloadStream.java`) → `NodeManager` subscribes to that section, meshes it, and scatter-writes the result. **Only geometry the camera can actually see is ever loaded, meshed, or uploaded** — this demand-driven loop is the core scalability idea.
- **Eviction** (`NodeCleaner.java` + `cleaner/*.comp`): when free GPU geometry < 256 MB, a compute pass partial-sorts the 256 least-recently-visible nodes (by `lastRenderFrame`), reads them back, and prunes their geometry/tree nodes — a GPU-computed LRU.

### 4.4 Draw submission — vertex-pulling MDIC

(`voxy/client/core/rendering/section/backend/mdic/MDICSectionRenderer.java`, `shaders/lod/gl46/`.)
- `prep.comp` (1 thread) zeroes counters and writes indirect dispatch/draw args. A **raster occlusion pass** (`cull/raster.vert/frag`) draws each render-listed section's AABB cube against the depth buffer, stamping `visibilityData[section]=frameId` for survivors (two-frame temporal scheme; sections newly visible this frame go in a separate "temporal" bucket drawn later to hide popping).
- `cmdgen.comp` turns each visible section into **one draw command per non-empty face-direction bucket**, skipping buckets backfacing the camera (~half of all quads never reach the vertex shader). Translucent sections are instead histogrammed into 1024 camera-distance buckets; a GPU prefix sum + `buildtranslucents.comp` emits a **back-to-front sorted per-section draw list** — coarse but sufficient for LOD water.
- Rendering is **`glMultiDrawElementsIndirectCount`** — the entire LOD world in ~3 CPU draw calls (opaque, temporal, translucent), with budgets of 400k/100k/100k commands.
- **No vertex buffers.** A single shared index buffer of a repeating 6-index quad pattern (`voxy/client/core/rendering/util/SharedIndexBuffer.java`); the vertex shader (`gl46/quads3.vert`) fetches the uint64 quad via `gl_VertexID>>2`, synthesizes the corner position arithmetically from packed position/size/face + LOD scale, and packs all flat attributes on the provoking vertex only. The fragment shader (`gl46/quads.frag`) reconstructs tiled UVs into the model's atlas cell with `textureGrad`, does alpha cutout, biome tint (per-quad LUT color, per-pixel grayscale-mask refinement), and directional face shading.
- **GPU geometry memory**: one large SSBO (sparse-buffer committed on demand where supported), suballocated by a best-fit free-list with neighbor coalescing (`voxy/common/util/AllocationArena.java` — packed `(size,addr)` red-black sets), allocation bookkeeping done on the async thread, actual bytes streamed through a 64 MB persistent-mapped, fence-reclaimed upload ring (`voxy/client/core/rendering/util/UploadStream.java`; 32 MB download ring for readbacks). A 4 GB CPU-side `GeometryCache` (`voxy/client/core/rendering/GeometryCache.java`) keeps evicted built sections so revisiting is upload-only, no remesh.

### 4.5 Blending with vanilla terrain and translucency at the seam

- **Depth-bounding buffer** (`voxy/client/core/rendering/bounding/BoundRenderer.java` + `voxy/client/mixin/sodium/MixinVisibleChunkCollector.java`): each frame the exact set of *built, visible* vanilla sections is streamed into a depth-only buffer by rasterizing their AABB **back faces**. In `quads.frag` every LOD fragment whose depth is nearer than that bound is discarded — LOD terrain can never draw over or poke through real terrain, and holes in vanilla coverage (unloaded chunks) are automatically filled by LOD.
- **Depth/stencil bridge** (`voxy/client/core/AbstractRenderPipeline.initDepthStencil` + `shaders/post/setup_stencil_depth.frag`): vanilla depth is copied into Voxy's own D24S8 target with stencil marking vanilla-covered pixels, so LOD renders only into the gaps; the final composite (`post/blit_texture_depth_cutout.frag`, `NormalRenderPipeline.finish`) reprojects Voxy depth into vanilla's projection, applies environmental fog + SSAO, and alpha-blends into the vanilla framebuffer. Voxy uses its own projection with near=16, far=48 000 blocks — the near plane starting *at* the vanilla boundary is what makes 3000-chunk far planes precision-safe.
- Translucent LOD (water) draws after opaque with standard alpha blending, subject to the same depth-bound discard, so the water plane meets vanilla water without double-blending.

### 4.6 Why it's ~10× Distant Horizons (synthesis)

1. Frame cost is O(visible nodes on GPU), not O(loaded LODs on CPU): traversal, LOD selection, occlusion, command generation, and sorting all run in compute; CPU issues ~constant draw calls.
2. 8-byte quads + vertex pulling: ~an order of magnitude less geometry memory/bandwidth than vertex-buffer meshes, and greedy 16×16 merging on top.
3. Demand-driven loading: the GPU's own visibility result decides what gets meshed — nothing outside the frustum/occlusion set is ever built.
4. Hi-Z + raster AABB occlusion + per-direction bucket backface elimination kill most overdraw before shading.
5. Screen-space-error LOD with an FPS-servo threshold degrades resolution, never frame rate.

---

## 5. Threading model

(`voxy/common/thread/`.) One **unified worker pool** (default `coreCount/1.5` threads, Java priority 3) serves all "services." A service (`Service.java`) is just a semaphore-counted job source with a **weight** and an optional boolean **limiter**; the scheduler (`ServiceManager.runAJob0`) picks the next job by **weighted random sampling proportional to `pendingJobs × weight`** — ingest 5000, saving 100, mesh-gen 10, DH import 10, world import 3. Effects: ingest is drained near-instantly, meshing fills idle time, imports run only in slack, and one pool auto-balances everything with zero per-service thread tuning.

Supporting pieces:
- **`MultiThreadPrioritySemaphore`**: lets *foreign* thread pools donate idle blocking time — Sodium's chunk-builder threads' queue semaphore is impersonated (`voxy/client/mixin/sodium/MixinChunkJobQueue.java`) so while waiting for Sodium work they execute Voxy jobs, preferring their own when it arrives (10 ms poll).
- **`PerThreadContextExecutor`**: per-(service, worker-thread) context objects (scratch buffers, mesher state, DB statements) with GC-driven cleanup via a weak map — services get thread-local state without ThreadLocal leaks.
- **Backpressure summary**: save queue soft-cap 5000 with caller self-stealing; importer limiter at save-queue >1200; mesh queue position-deduped; geometry-result application throttled per octree-thread iteration (≤300 results, ≥50 MB GPU free, ~1 MB uploads); GPU request queue soft-cap shrinks with mesh backlog. Every stage that can flood a downstream stage has a specific valve.
- Dedicated non-pool threads: the octree thread (`AsyncNodeManager`), the model-bake thread (`ModelBakerySubsystem`), an idle-world reaper (10 s timeout, `VoxyInstance`).
- CPU-affinity machinery exists (`voxy/common/util/cpu/CpuLayout.java`, P-core/E-core aware) but is currently not wired to the workers.

---

## 6. Clever ideas worth carrying into C#

1. **One `long` per voxel** (blockId|biome|light) with air = zero block bits — makes mipping, diffing, meshing, and serialization pure integer ops. In C#: `ulong[]` + bit twiddling, identical.
2. **Persistent append-only id registry** (`Mapper`) stored beside the sections; ids never reused → stored worlds never invalidate. Replicate mappings across shards + majority vote if you shard.
3. **32³ sections at every mip level, same code path for all levels**, keyed by one packed long; per-section 8-bit child-occupancy mask maintained incrementally and bubbled only on empty↔nonempty flips.
4. **Change-detecting mip write with early-out** (`WorldUpdater`): compare-as-you-copy; stop climbing levels when nothing changed; emit a 6-bit neighbor mask for boundary remesh.
5. **Representative-sample downsampling** (max-opacity corner pick, leaves forced opaque, light averaged for air) — cheap and looks right; don't average materials.
6. **Streaming palette serialization**: u16 index plane + first-seen LUT, prev-run shortcut, then ZSTD-1; DB compression off for that column family. Sub-ms encode/decode.
7. **Demand-driven meshing via GPU feedback**: the visibility pass itself requests missing geometry through a tiny capped readback buffer; requested-flag on the node dedupes. Nothing invisible is ever built. (Even without compute traversal, the concept — "the culling result is the load queue" — is portable.)
8. **Subscription-routed updates** (`SectionUpdateRouter`): world dirty events only reach the renderer for sections some octree node is *watching*; watch set == resident node set. Decouples world engine from renderer completely (the engine has just one `dirtyCallback`).
9. **8-byte quads + vertex pulling + one shared index buffer + per-face-direction buckets** with GPU backface bucket culling and MultiDrawIndirectCount. On modern GL/Vulkan-class APIs in VS's engine this is the single biggest win; the fallback ladder is: persistent-thread traversal → per-level indirect dispatches → CPU traversal with GPU culling → CPU everything.
10. **Two-frame raster AABB occlusion + temporal bucket** and **Hi-Z pyramid** for node-level culling; screen-space-error LOD threshold servoed to FPS.
11. **Depth-bounding buffer built from the real vanilla-visible chunk set** (captured from the renderer's own visibility walk) + fragment discard — the cleanest known solution to the LOD/real-terrain seam, including holes and water.
12. **Bake-once 6-face orthographic impostors per block state**, content-hash deduped, with derived occlusion metadata driving the mesher; lazy bake via requeue-on-miss exception.
13. **Async octree thread with triple-buffered hand-off and GPU scatter-write patching** — render thread applies batched byte patches, never runs tree logic.
14. **Weighted-random unified job pool** with per-service weights/limiters, plus caller-steals backpressure — trivially expressible in C# over a semaphore + `ThreadPool`-independent workers.
15. **Memory hygiene**: pooled section arrays, thread-local scratch buffers, LRU of released sections, a 4 GB built-geometry cache keyed by section pos (evict on world change), free-list arena with coalescing for GPU memory, GC-based leak detectors on every native handle (in C#: `SafeHandle`/finalizer asserts).

**Known gaps in Voxy itself** (avoid inheriting): no blob checksums; block-entity (chest/etc.) baking unfinished; LMDB position iteration unimplemented; ingest queue unbounded/undeduped; occupancy-based AO disabled; several TODO-grade heuristics (mip rule, magic occlusion thresholds) that you can design more deliberately.
