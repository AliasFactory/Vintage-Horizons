# Distant Horizons Architecture Report

> NOTE: Distant Horizons is LGPL-3.0. VintageHorizons is a clean reimplementation — this report
> documents architecture/concepts for design reference; we do not copy code.

**Scope**: `distant-horizons` repo, current master. Engine-agnostic core in `coreSubProjects/core`, public API in `coreSubProjects/api`, MC-version glue in `common/`, `fabric/`, `forge/`, `neoforge/`. All paths below are relative to the repo root; core paths abbreviate `coreSubProjects/core/src/main/java/com/seibel/distanthorizons/core` as `core/...`.

**The one-sentence architecture**: mod-loader hooks snapshot chunks the client already has → a bounded, distance-prioritized queue converts them into column-RLE "full data sources" (64×64 columns, palette + packed longs) → these are merged into a persistent SQLite-backed mip pyramid keyed by (detailLevel, x, z) → a player-centered quadtree of render sections pulls from that pyramid, meshes greedy quads off-thread, and renders them into DH's own framebuffer which is composited behind/around vanilla terrain via clip-plane and dithered-fade tricks.

---

## 0. Big-picture data flow

```
MC chunk (client packet / server save / interaction event)
  └─ platform proxy/mixin (fabric|forge|neoforge|common)
      └─ SharedApi.applyChunkUpdate()                    [core/api/internal/SharedApi.java]
          └─ ChunkUpdateQueueManager (per level, 2-stage, distance-priority, hash-gated)
              └─ DhLightingEngine block-light bake
              └─ IDhLevel.updateChunkAsync()             [core/level/AbstractDhLevel.java]
                  └─ LodDataBuilder.createFromChunk → FullDataSourceV2 (leaf, applyToParent=true)
                      └─ DelayedDataSourceSaveCache (1s write-merging)
                          └─ FullDataUpdaterV2.updateDataSource (merge + SQLite save + listeners)
                              ├─ FullDataUpdatePropagatorV2 (child→parent mip pyramid, DB-flag driven)
                              └─ ClientLevelModule.OnDataSourceUpdated → LodQuadTree.queuePosToReload
                                  └─ LodRenderSection: FullData → ColumnRenderSource → quads → VBO
                                      └─ RenderBufferHandler / LodRenderer (own FBO, composited to MC)
```

---

## 1. Chunk ingestion

### 1.1 Hooks (platform layer)

DH deliberately has **no server dependency**. On a vanilla server everything comes from data the client already holds:

- **Chunk arrival from server** — `fabric/src/main/java/com/seibel/distanthorizons/fabric/mixins/client/MixinClientPacketListener.java`: injects into `ClientPacketListener.enableChunkLight` (MC ≥1.20), i.e. the moment a network chunk *and its light* are fully received. It immediately hops off the network/render thread onto the IO executor, wraps the chunk (`new ChunkWrapper(chunk, levelWrapper)`), and calls `SharedApi.INSTANCE.applyChunkUpdate(...)`. For 1.18–1.20 the equivalent hook is `ClientLevel.setLightReady` in `fabric/src/main/java/com/seibel/distanthorizons/fabric/mixins/client/MixinClientLevel.java`. The same mixin's `handleLogin`/`close` injections drive `ClientApi.onClientOnlyConnected/Disconnected` (world lifecycle).
- **Chunk load event** — `fabric/src/main/java/com/seibel/distanthorizons/fabric/FabricClientProxy.java` (~line 122) also registers `ClientChunkEvents.CHUNK_LOAD`, but **only when `MC.clientConnectedToDedicatedServer()`** — in singleplayer the server-side hooks below are authoritative and higher quality.
- **Singleplayer / integrated & dedicated server** — `common/src/main/java/com/seibel/distanthorizons/common/commonMixins/MixinChunkMapCommon.java` hooks **chunk save** (`ServerApi.INSTANCE.serverChunkSaveEvent`) with validation guards (light correct, not a `ProtoChunk`, biomes populated); `fabric/src/main/java/com/seibel/distanthorizons/fabric/FabricServerProxy.java` hooks `ServerChunkEvents.CHUNK_LOAD` → `ServerApi.serverChunkLoadEvent`. Both funnel to the exact same `SharedApi.applyChunkUpdate` (`core/api/internal/ServerApi.java` lines 107–108). Chunk *save* is the change-detection mechanism when a server is available — MC saves dirty chunks periodically, so edits get picked up for free.
- **Client-side block changes on remote servers** — Fabric has no client block-change event, so `FabricClientProxy.java` registers `AttackBlockCallback` (break) and `UseBlockCallback` (place) as approximations; each re-fetches the chunk at the interaction pos and re-submits it. Forge/NeoForge use `PlayerInteractEvent.LeftClickBlock/RightClickBlock` (`forge/src/main/java/com/seibel/distanthorizons/forge/ForgeClientProxy.java` lines 113–176). Note the cheap pre-check `SharedApi.isChunkAtBlockPosAlreadyUpdating(...)` to avoid pulling a chunk from MC (slow, render-thread-hostile) when it's already queued.

### 1.2 The funnel: `SharedApi.applyChunkUpdate`

`core/api/internal/SharedApi.java` (~line 190). Responsibilities:

- Drop updates when no DH world/level exists yet; if the client level hasn't loaded, chunks are parked in `ClientApi.INSTANCE.waitingChunkByClientLevelAndPos` and replayed later by `ClientApi.loadWaitingChunksForLevel` (`core/api/internal/ClientApi.java` ~line 249) — this handles the "chunks arrive before level-load event" race.
- Skip if the DH-on-server network protocol will provide updates instead (`DhClientLevel.shouldProcessChunkUpdate`, `core/level/DhClientLevel.java` ~line 326).
- Dedupe (`chunkManager.contains(pos)`) and enqueue into a per-level `ChunkUpdateQueueManager`, then kick the "LOD Builder" executor.

### 1.3 The queue: two stages, distance priority, hash gating

`core/api/internal/chunkUpdating/WorldChunkUpdateManager.java` holds one `ChunkUpdateQueueManager` (`core/api/internal/chunkUpdating/ChunkUpdateQueueManager.java`) per `ILevelWrapper` (prevents nether chunks bleeding into overworld LODs). Key mechanics:

- **preUpdateQueue → updateQueue** (both `ChunkPosQueue`, `core/api/internal/chunkUpdating/ChunkPosQueue.java`). Both are centered on the player chunk (`setCenter`); workers `popClosest()`, and when full the queue evicts via `popFurthest()`. Bounded at `MAX_UPDATING_CHUNK_COUNT_PER_THREAD_AND_PLAYER = 1_000` × threads × players, with a rate-limited "DH overloaded" chat/log warning.
- **Pre-update stage** = staleness check: `dhLevel.getChunkHash(pos)` (persisted, see §3) vs `chunkWrapper.getBlockBiomeHashCode()`. The hash (`core/wrapperInterfaces/chunk/IChunkWrapper.java` ~line 232) samples every 2nd block in x/y/z (block state + biome + y) plus the *full* surface from both heightmaps — cheap but sensitive to the edits players actually make. Unchanged chunks are dropped here; this is what makes re-received chunks nearly free.
- **Update stage**: gathers up to 8 neighbor chunk wrappers *from the in-memory queue cache* (`queuedChunkWrapperByChunkPos`, a Guava cache expiring after 20 s — avoids touching the MC level from worker threads), bakes DH's own **block** lighting (`DhLightingEngine.bakeChunkBlockLighting`, `core/generation/DhLightingEngine.java`, a Starlight-inspired standalone engine — DH never trusts/needs MC's light data), updates beacon beams, then calls `dhLevel.updateChunkAsync(chunkWrapper, newChunkHash)`.

`ChunkWrapper` (`common/src/main/java/com/seibel/distanthorizons/common/wrappers/chunk/ChunkWrapper.java`) is the snapshot boundary: it builds DH-side heightmaps (`createDhHeightMaps`), copies chunk sections on `copy()` (line 150/519), and stores DH-computed light arrays — so downstream processing never touches live MC state.

---

## 2. LOD data model

### 2.1 Position keying: `DhSectionPos`

`core/pos/DhSectionPos.java` — a packed `long`: 8 bits detail level, 28+28 bits x/z. A **section** is always **64×64 data columns** regardless of detail. `SECTION_MINIMUM_DETAIL_LEVEL = 6`, so:
- section detail 6 (leaf) = 64×64 *block*-sized columns = 4×4 MC chunks (the file/quadtree unit; the class doc explains the size tradeoff),
- each +1 detail doubles column footprint; root is `ROOT_SECTION_DETAIL_LEVEL = 6 + REGION_DETAIL_LEVEL(9) = 15` (`core/file/fullDatafile/V2/FullDataSourceProviderV2.java` lines 71–79; `LodUtil.REGION_DETAIL_LEVEL=9` in `core/util/LodUtil.java`).
- "data detail" = section detail − 6 (0 = per-block, 1 = 2×2 blocks per column, …).

### 2.2 Full (source-of-truth) format: `FullDataSourceV2`

`core/dataObjects/fullData/sources/FullDataSourceV2.java`:

- `WIDTH = 64`; `dataPoints` = `LongArrayList[64*64]`, one **vertical RLE column** per x/z, sorted **top-down**.
- Per column, one byte each of `columnGenerationSteps` (`EDhApiWorldGenerationStep` — how complete the data is) and `columnWorldCompressionMode` (`MERGE_SAME_BLOCKS` vs lossy `VISUALLY_EQUAL`, `coreSubProjects/api/.../enums/config/EDhApiWorldCompressionMode.java`).
- `mapping`: a per-source palette `FullDataPointIdMap` (`core/dataObjects/fullData/FullDataPointIdMap.java`) mapping int ID → (biome wrapper, block-state wrapper) pair; serialized as strings (`"biome_DH-BSW_blockstate"`). Merging sources merges palettes and remaps IDs (`mergeAndReturnRemappedEntityIds`), and `removeUnusedIdsAndRemap()` (line 1145) prevents unbounded palette growth.
- **Datapoint packing** (`core/util/FullDataPointUtil.java` lines 30–70): one `long` = **32-bit palette ID | 12-bit height | 12-bit bottom-Y (relative to level min) | 4-bit sky light | 4-bit block light**. A column is a stack of these runs with no gaps (air is stored explicitly so lighting downsampling works — enforced in `LodDataBuilder.validateOrThrowApiDataColumn`).
- `applyToParent` / `applyToChildren` — tri-state dirty flags that are *persisted* (see §6).
- `isEmpty`, timestamps, pooled backing arrays (`AbstractPhantomArrayList` pooling to fight GC).

**Chunk → leaf source**: `core/dataObjects/transformers/LodDataBuilder.java#createFromChunk`. Walks each of the 16×16 columns top-down starting at max(lightBlockingHeightmap, solidHeightmap), emitting a new run whenever block-state **or biome** changes; a "force single block" rule keeps thin colored tops (snow layers, flowers, fire) from tinting whole columns (lines 195–256). Chunk position maps into one quadrant of the 4×4-chunk leaf section (`chunkPos & 3`). Sky light is *not* set here — it's baked later per 64×64 source (`AbstractDhLevel.onDataSourceSaveAsync` → `DhLightingEngine.bakeDataSourceSkyLight`, `core/level/AbstractDhLevel.java` line 183) so cross-chunk light is consistent.

### 2.3 Downsampling (mip generation)

`FullDataSourceV2.updateFromDataSource` (line 374) accepts input at the same level, one level below, or one level above:

- **Child→parent** (`updateFromOneBelowDetailLevel`, line 591): the child covers one quadrant of the parent; every 2×2 input columns merge into 1 output column via `mergeInputTwoByTwoDataColumn` (line 722): collect all y-transition boundaries of the 4 columns, sweep slices bottom-up, sample each column at slice midpoint, pick the **most common palette ID** (ties broken; air handled) and **average the light**, then re-RLE adjacent identical slices. Generation step per merged column is the **min** of the 4 (never claim generated data that isn't).
- **Same-level**: column-by-column replace, gated on generation-step (won't overwrite more-complete data with less), hash-compared to detect "did anything actually change".
- **Parent→child** (`downsampleFromOneAboveDetailLevel`, line 1046): upsampling used by the experimental "fill holes with low-detail data" feature.
- After a change, optional occlusion culling of hidden runs (`core/dataObjects/transformers/FullDataOcclusionCuller.java`) when lossy compression is on.

### 2.4 Render format: `ColumnRenderSource`

`core/dataObjects/render/ColumnRenderSource.java` — also 64×64 columns, but a **fixed max vertical slice count** per column (from `EDhApiVerticalQuality`) and a different 64-bit packing (`core/util/RenderDataPointUtil.java` lines 30–60): **4-bit material ID | 4-bit alpha | 8+8+8-bit RGB | 12-bit yMax | 12-bit yMin | 4+4-bit block/sky light**. I.e. the render pipeline works purely on *pre-resolved colors*, no palette. `core/dataObjects/transformers/FullDataToRenderDataTransformer.java` does the conversion: resolves block+biome → tinted color via level wrapper, applies ignored-block config, cave culling (skylight==0 heuristics, lines 280–300), water-surface replacement, and reduces columns that exceed the vertical budget (`RenderDataPointReducingList`). Render sources are **transient** — built on demand from full data, never persisted (the old render cache table was dropped: `sqlScripts/0040-sqlite-removeRenderCache.sql`).

---

## 3. Storage

- **Engine**: SQLite over JDBC, one DB file per level: `AbstractDhRepo` (`core/sql/repo/AbstractDhRepo.java`, `DEFAULT_DATABASE_TYPE = "jdbc:sqlite"`), per-thread `Connection`s, corruption detection/quarantine, WAL journaling (`sqlScripts/0031-sqlite-useSqliteWalJournaling.sql`).
- **Migrations**: numbered SQL scripts in `coreSubProjects/core/src/main/resources/sqlScripts/` applied by `core/sql/DatabaseUpdater.java`; legacy V1 data is migrated lazily by `core/file/fullDatafile/V2/DataMigratorV1.java` on its own thread.
- **Schema** (`0020-sqlite-createFullDataSourceV2Tables.sql`): table `FullData`, PK **(DetailLevel, PosX, PosZ)** — DetailLevel is *data* detail (0,1,2…), matching the mip pyramid. Blob columns: `Data` (all 4096 columns, length-prefixed packed longs), `ColumnGenerationStep`, `ColumnWorldCompressionMode`, `Mapping` (palette strings), plus `DataFormatVersion`, `CompressionMode`, `ApplyToParent`/`ApplyToChildren` dirty flags, timestamps. Migration `0090-sqlite-addAdjacentFullDataColumns.sql` adds `North/South/East/WestAdjData` blobs — **pre-extracted one-column-wide edge strips** so meshing a section's seams only deserializes a strip of each neighbor instead of the whole 64×64 source (`FullDataSourceProviderV2.getAdjForDirection`, line 303).
- **Compression**: whole-blob LZ4 or ZStd (`coreSubProjects/api/.../EDhApiDataCompressionMode.java`; ~0.26 ratio for ZStd per its javadoc), chosen by config, recorded per row. DTO layer: `core/sql/dto/FullDataSourceV2DTO.java`.
- **Chunk hashes**: table `ChunkHash` (`0060-sqlite-createChunkHashTable.sql`, `core/sql/repo/ChunkHashRepo.java`), written transactionally after the owning data source saves (`AbstractDhLevel.onDataSourceSaveAsync`, line 194+) so a hash never exists without its data.
- **Per-server/world keying**: `core/file/structure/ClientOnlySaveStructure.java` — on remote servers, saves under `<mcInstall>/Distant_Horizons_server_data/<folderName>/<dimensionName>/DistantHorizons.sqlite`, where `folderName` is configurable (server name / IP / port / MC version modes, percent-escaped). If the server runs the optional DH plugin protocol it can supply an explicit **level key** (`IServerKeyedClientLevel`, managed via `core/api/internal/ClientPluginChannelApi.java`) which disambiguates multiverse/multi-world servers — otherwise DH can only key by dimension name. Singleplayer uses `core/file/structure/LocalSaveStructure.java` (inside the world save).
- **Write merging**: `core/util/delayedSaveCache/AbstractDelayedSaveCache.java` + `DelayedDataSourceSaveCache` — chunk updates are merged in memory per section pos and flushed after 1 s of quiescence (`AbstractDhLevel.java` line 69), because adjacent chunk arrivals overwhelmingly hit the same 4×4-chunk section.

---

## 4. Rendering

### 4.1 Quadtree of render sections

`core/render/QuadTree/LodQuadTree.java` over generic `core/util/objects/quadTree/QuadTree.java`, values are `LodRenderSection` (`core/render/QuadTree/LodRenderSection.java`). One tree per rendering client level, created in `ClientLevelModule.ClientRenderState` (`core/level/ClientLevelModule.java` line 229) and rebuilt when render distance changes.

- Tick (`tryTick`/`updateAllRenderSections`, ~100 ms cadence from `DhClientWorld`'s timer): recenters tree on player/camera, walks each root recursively.
- **Expected detail** = `log(blockDistance / distanceUnit) / log(quadraticBase)` clamped to [maxHorizontalResolution … root] (line 1162) — a logarithmic distance→LOD dropoff whose base/unit come from the "horizontal quality" config.
- `recursivelyUpdateRenderSectionNode` (line 514): if a node is coarser than desired, recurse into 4 children; **a parent keeps rendering until all 4 children have uploaded buffers** (`onDetailLevelTooLow`, line 601), and children get deleted only on the render thread after the parent takes over (`addEnableDeleteChildrenNode` + `RenderThreadTaskHandler` cleanup, line 416) — this is the no-holes/no-flicker mechanism. Root nodes never render (avoids edge flashing when crossing root boundaries, line 623).
- Sections needing (re)builds are collected and loaded lower-detail-first / nearest-first (`loadQueuedSections`, line 770). Data-updated sections come through `queuePosToReload` → `sectionsToReload` queue (line 1218).
- The tree is also where **on-demand world gen** (singleplayer only) is queued: missing positions → `FullDataSourceProviderV2.canRetrieveMissingDataSources()` is false on client-only levels and true in `core/file/fullDatafile/GeneratedFullDataSourceProvider.java`; on DH-enabled servers, `core/file/fullDatafile/RemoteFullDataSourceProvider.java` fetches LODs over the plugin channel instead. On a vanilla server neither runs — LODs exist only where chunks have been seen.

### 4.2 Mesh building

`LodRenderSection.uploadRenderDataToGpuAsync` (line 136) → on the "Render Loader" executor:

1. Load center full data + 4 neighbor **edge strips** from SQLite, transform each to `ColumnRenderSource` (§2.4).
2. `core/dataObjects/render/bufferBuilding/ColumnRenderBufferBuilder.java#makeLodRenderData`: for each of 64×64 columns, fetch the 4 adjacent columns (in-section or from the neighbor strip, with detail-level mismatch handling, lines 146–235), then for each vertical run call `addRenderDataPointToBuilder` → `ColumnBox.addBoxQuadsToBuilder` (`core/dataObjects/render/bufferBuilding/ColumnBox.java`) which emits only the visible faces of the run's box: up/down faces culled against the runs above/below, side faces clipped against the adjacent column's runs (occluded spans marked `SKYLIGHT_COVERED` and skipped) — this is the "column quads with neighbor culling" strategy.
3. `core/dataObjects/render/bufferBuilding/LodQuadBuilder.java`: quads are bucketed by the 6 face directions; incremental merge on insert plus a full **greedy merge pass** (`mergeQuads`, line 265) along both axes (`BufferQuad.tryMerge`, `core/dataObjects/render/bufferBuilding/BufferQuad.java`) — transparent quads only merge east-west to preserve sort order.
4. **Vertex format = 16 bytes** (`putVertex`, line 477): 3×int16 section-relative position, int16 meta (4+4 sky/block light + 6-bit per-axis "micro offset" used in the vertex shader to fight cracks), RGBA8 color, uint8 Iris material ID, uint8 normal index, int16 texture tile id. Positions are relative to the section corner; the section origin goes in a per-buffer uniform.
5. Upload: `core/dataObjects/render/bufferBuilding/LodBufferContainer.java` — CPU buffers handed to the render thread task queue for GL buffer creation/upload; the worker `join()`s the upload to cap staging-memory explosion (`LodRenderSection.java` lines 194–201). Old buffer swapped out only after the new one is complete (no flicker on updates).

### 4.3 Frame rendering, vanilla overlap, fog, depth

- Entry: mod loader render hook (Fabric `WorldRenderEvents.AFTER_SETUP` → `ClientApi.INSTANCE.renderLods()`, `fabric/.../FabricClientProxy.java` line 227; captures MC's model-view/projection matrices). `ClientApi.renderLodLayer` also drains the render-thread task queue with a **budget of half a frame** (`core/render/RenderThreadTaskHandler.java#runRenderThreadTasks`, line 128).
- `core/render/RenderBufferHandler.java#buildRenderList` (line 132): walks the tree's enabled-sections list, frustum-culls per section AABB (full world height), and produces a **near-to-far sorted** buffer list (Manhattan distance); opaque renders near→far, transparent far→near (comment cites the discrete-column translucency-ordering trick).
- `core/render/renderer/LodRenderer.java#renderTerrain` (line 119): renders into **DH's own color+depth framebuffer** — opaque pass → generic objects → SSAO → transparent pass → fog (`shaders/fog/gl/*`) → far-clip fade — then a fullscreen "apply" pass composites DH's buffer into MC's framebuffer using both depth buffers (`this.metaRenderer.applyToMcTexture`, shader `coreSubProjects/core/src/main/resources/assets/distanthorizons/shaders/shared/gl/apply.frag`).
- **Avoiding overlap with vanilla terrain** — three cooperating mechanisms, all in the projection/fragment domain (DH does *not* track which chunks vanilla renders):
  1. **Near clip plane** on DH's projection = a fraction ("overdraw prevention") of the vanilla render distance: `core/util/RenderUtil.java#getNearClipPlaneInBlocks` (line 149) — auto-scaled by vanilla RD (0.2–0.9), shrunk dynamically while flying fast (`DynamicOverdraw`) so LODs cover ground vanilla hasn't loaded yet, overridden when the camera is high above the terrain. `setDhProjectionMatrix` (line 95) rewrites only m22/m23 of MC's projection matrix so DH's far clip extends to LOD distance (reverse-Z aware, `EDhRenderDepth`).
  2. **Dithered near fade** in the terrain fragment shader: `shaders/terrain/gl/frag.frag` lines 137–154 — Bayer-matrix `discard` between `uClipDistance` and 1.5×, so the LOD edge dissolves into vanilla terrain instead of a hard line.
  3. **Vanilla fade** (opposite direction): `shaders/fade/gl/vanilla_fade.frag` — reconstructs world distance from *MC's* depth buffer and smoothstep-blends vanilla fragments toward the DH color between start/end fade distances, hiding vanilla's own fog/edge; driven by `ClientApi.renderFadeOpaque/Transparent` (`core/api/internal/ClientApi.java` lines 631–686).
- Sky/heights: level-height uniform prevents MC clouds drawing behind DH clouds (same shader, line 66); DH optionally disables vanilla clouds/fog (`LodRenderer` line 168).

---

## 5. Threading model

Defined in `core/util/threading/ThreadPoolUtil.java` + `core/util/threading/PriorityTaskPicker.java`:

- **One shared worker pool** sized by `Config.Common.MultiThreading.numberOfThreads`, multiplexed by `PriorityTaskPicker` into named logical executors: `Network Compression`, `IO` (file handler / SQLite), `Render Loader` (mesh building), `LOD Builder` (chunk→full-data), `Update Propagator`, `World Gen`. The picker caps total concurrent tasks at N and round-robins across executors; `Update Propagator` and `World Gen` have `canRun` predicates that **pause them while the camera moves fast** (elytra speed) or while the LOD-builder backlog is large (lines 117–118, 170–201) — throttling by starvation rather than priorities.
- Standalone single threads: network client handler, beacon culling, V1 data migration, cleanup, the delayed-save-cache flusher (`core/util/delayedSaveCache/AbstractDelayedSaveCache.java`), the propagator's 250 ms poll loop (`FullDataUpdatePropagatorV2.runUpdateQueue`), and the quadtree's retrieval-queue thread.
- Pools are created on world load and torn down on unload — `SharedApi.setDhWorld` (`core/api/internal/SharedApi.java` line 91).
- **Render thread**: the *only* GL work is done via `core/render/RenderThreadTaskHandler.java` — a queue drained during DH's render hook with a time budget of half the target frame time (VBO uploads, buffer closes, quadtree child deletion). Everything else (DB IO, lighting, transform, meshing, downsampling) is off-thread.
- **Game-tick decoupling**: `DhClientWorld` ticks its levels (and thus the quadtree) from a `java.util.Timer` every 100 ms (`core/world/DhClientWorld.java`, `IDhClientWorld.TICK_RATE_IN_MS`), not from MC's tick loop.
- Concurrency control on data: per-section-pos `ReentrantLock`s (`core/util/threading/PositionalLockProvider.java`) in `FullDataUpdaterV2`; parent-then-child lock ordering with `tryLock` on parents to avoid deadlock in the propagator; `LodQuadTree.treeTickLock` (`tryLock` — skip tick rather than block) since `RenderBufferHandler` walks the tree concurrently each frame.

---

## 6. Update / staleness propagation

End-to-end path when a block changes or a chunk re-arrives:

1. **Detect**: hook fires → hash check in `ChunkUpdateQueueManager.processQueuedChunkPreUpdate` (persisted `ChunkHash` table). Unchanged → dropped.
2. **Leaf update**: `AbstractDhLevel.updateChunkAsync` (line 150) builds a leaf `FullDataSourceV2` with `applyToParent = true` (`LodDataBuilder.java` line 79), merges it into the in-memory delayed-save cache (1 s window).
3. **Persist + notify**: on flush, `onDataSourceSaveAsync` bakes sky light, then `FullDataUpdaterV2.updateDataSource` (`core/file/fullDatafile/V2/FullDataUpdaterV2.java` line 117): lock pos → load recipient section from SQLite → `updateFromDataSource` merge → save DTO (with `ApplyToParent=1`) → fire `IDataSourceUpdateListenerFunc` listeners.
4. **Render refresh**: `ClientLevelModule.OnDataSourceUpdated` (`core/level/ClientLevelModule.java` line 169) → `LodQuadTree.queuePosToReload(pos)` → next tree tick rebuilds that section's mesh *if it currently has a buffer* (`reloadQueuedSections`, `LodQuadTree.java` line 738). Sections that scroll out of range are only flagged `renderDataDirty` and rebuilt when they come back (`LodQuadTree.java` lines 282–291).
5. **Pyramid propagation**: the `ApplyToParent` flag lives in the DB row, so propagation is **pull-based and crash-safe**. `core/file/fullDatafile/V2/FullDataUpdatePropagatorV2.java` polls every 250 ms: `repo.getPositionsToUpdate(...)` runs a SQL query `WHERE ApplyToParent = 1 ORDER BY <manhattan distance to player> LIMIT n` (`core/sql/repo/FullDataSourceV2Repo.java` lines 435–480), groups dirty children by parent, merges each child into the parent (2×2→1 downsample, §2.3), clears the child flags, sets the parent's flag (unless at root, detail 15), and saves — which itself fires step 4's listeners, so **coarser render sections refresh as the wave climbs the pyramid**. There are ≤9 levels, so a block edit reaches the coarsest LOD after ≤9 asynchronous, player-proximity-prioritized hops. `ApplyToChildren`/`runChildUpdates` do the same downward for the optional upsample-holes feature.

---

## The ~20 load-bearing classes

| # | Path (repo-relative) | Role |
|---|---|---|
| 1 | `coreSubProjects/core/.../core/api/internal/SharedApi.java` | Chunk-update funnel, world lifecycle, thread-pool lifecycle |
| 2 | `coreSubProjects/core/.../core/api/internal/chunkUpdating/ChunkUpdateQueueManager.java` | 2-stage distance-priority chunk queue, hash gating, overload cap |
| 3 | `coreSubProjects/core/.../core/api/internal/ClientApi.java` | Client connect/disconnect, waiting-chunk replay, render entry, render-thread task drain |
| 4 | `common/src/.../common/wrappers/chunk/ChunkWrapper.java` | Chunk snapshot boundary, DH heightmaps, block/biome hash |
| 5 | `fabric/src/.../fabric/mixins/client/MixinClientPacketListener.java` (+ `FabricClientProxy`, `MixinChunkMapCommon`) | The actual MC hooks (packet chunk+light, interactions, chunk save) |
| 6 | `coreSubProjects/core/.../core/dataObjects/transformers/LodDataBuilder.java` | Chunk → column-RLE full data |
| 7 | `coreSubProjects/core/.../core/util/FullDataPointUtil.java` | 64-bit full datapoint packing (id/height/minY/lights) |
| 8 | `coreSubProjects/core/.../core/dataObjects/fullData/sources/FullDataSourceV2.java` | The core LOD container + same/up/down merge (downsampling) |
| 9 | `coreSubProjects/core/.../core/dataObjects/fullData/FullDataPointIdMap.java` | Per-section (block,biome) palette |
| 10 | `coreSubProjects/core/.../core/pos/DhSectionPos.java` | Packed section position / detail-level algebra |
| 11 | `coreSubProjects/core/.../core/level/AbstractDhLevel.java` (+ `DhClientLevel`, `ClientLevelModule`) | updateChunkAsync, delayed save, chunk-hash persistence, render-state wiring |
| 12 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataSourceProviderV2.java` | DB-backed get/update of sources, adjacent-strip loads |
| 13 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataUpdaterV2.java` | Locked merge+save+listener dispatch |
| 14 | `coreSubProjects/core/.../core/file/fullDatafile/V2/FullDataUpdatePropagatorV2.java` | DB-flag-driven mip-pyramid propagation |
| 15 | `coreSubProjects/core/.../core/sql/repo/AbstractDhRepo.java` (+ `FullDataSourceV2Repo`, `sqlScripts/`) | SQLite layer, schema, migrations |
| 16 | `coreSubProjects/core/.../core/file/structure/ClientOnlySaveStructure.java` | Per-server/dimension DB keying, server level keys |
| 17 | `coreSubProjects/core/.../core/generation/DhLightingEngine.java` | Standalone block/sky lighting for LODs |
| 18 | `coreSubProjects/core/.../core/render/QuadTree/LodQuadTree.java` | Player-centered LOD quadtree, detail selection, load/reload orchestration |
| 19 | `coreSubProjects/core/.../core/render/QuadTree/LodRenderSection.java` | Per-section build-transform-mesh-upload pipeline |
| 20 | `coreSubProjects/core/.../core/dataObjects/transformers/FullDataToRenderDataTransformer.java` + `core/util/RenderDataPointUtil.java` | Full → color render data (the second packed-long format) |
| 21 | `coreSubProjects/core/.../core/dataObjects/render/bufferBuilding/{ColumnRenderBufferBuilder,ColumnBox,LodQuadBuilder,LodBufferContainer}.java` | Face culling, greedy quad merge, 16-byte vertex format, GPU upload |
| 22 | `coreSubProjects/core/.../core/render/{RenderBufferHandler,renderer/LodRenderer}.java` + `core/util/RenderUtil.java` + `shaders/{terrain,fade}/gl/*.frag` | Frame render list, own-FBO compositing, near-clip/dither/vanilla-fade overlap handling |
| 23 | `coreSubProjects/core/.../core/util/threading/{ThreadPoolUtil,PriorityTaskPicker}.java` + `core/render/RenderThreadTaskHandler.java` | The complete threading/throttling model |

---

## Properties that make client-side-only work (checklist for a Vintage Story port)

1. **Ingest only what the client already has**, at the moment light data is complete (packet hook), plus interaction events as a change signal; never block the render/network thread — snapshot the chunk and leave.
2. **Persistent content hash per chunk** to make redundant re-sends nearly free and to detect edits without any server cooperation.
3. **Bounded, player-centered priority queues everywhere** (chunk queue, mesh loads, propagation SQL `ORDER BY distance LIMIT n`) — the system is designed to shed load, not to keep up.
4. **One canonical, compact, mergeable format** (palette + vertical RLE + per-column completeness byte) that serves as both persistence and mip source; render data is derived and disposable.
5. **Dirty flags persisted in the store** so LOD-pyramid consistency survives crashes and doesn't require an in-memory dependency graph.
6. **Quadtree that renders parents until all children are ready**, and swaps buffers atomically — no holes, no flicker.
7. **Overlap with vanilla solved in screen space** (near-clip fraction of vanilla view distance + dithered discard + depth-based crossfade of the vanilla image), not by tracking which chunks the engine renders.
