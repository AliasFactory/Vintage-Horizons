# VintageHorizons — Design

A Distant Horizons-style extended-render-distance LOD mod for Vintage Story that is
**fully client-side**: it works on any server, vanilla or modded, because it builds its
LODs exclusively from chunk data the client already receives.

Supporting research (file-path-anchored deep dives) lives in `docs/research/`:

- [`distant-horizons-architecture.md`](docs/research/distant-horizons-architecture.md) — the veteran design (LGPL-3.0; concepts only, clean reimplementation)
- [`voxy-architecture.md`](docs/research/voxy-architecture.md) — the fast newcomer (all-rights-reserved; **ideas only, never copy code**)
- [`vintage-story-api.md`](docs/research/vintage-story-api.md) — everything the VS 1.22.x client API gives us, with citations

## 1. Why this is possible (and why nobody has done it in VS yet)

- A `"side": "Client"` code mod can join unmodded servers — VS mod verification is
  one-directional (server→client requirements only).
- The client receives, for every loaded chunk column: full block data (32³ chunks,
  palette-compressed in RAM), `RainHeightMap`, `WorldGenTerrainHeightMap`, and `YMax`.
  `capi.Event.ChunkDirty` (reason `NewlyLoaded`/`MarkedDirty`) fires on arrival/change.
- Existing VS LOD mods (Farseer, ChunkLOD) generate LOD data **server-side** for instant
  full-map coverage. The cost is requiring server installation. We accept Distant
  Horizons' trade instead: coverage builds up as you explore, cached persistently
  per-server on disk — and works everywhere.
- The community objection to a DH port ("VS terrain changes with seasons/snow, cached
  LODs go stale") is solved by **not baking appearance into geometry**: we store block
  identity and resolve color at render time, applying seasonal/snow tint in the shader
  (§6). Geometry only changes when blocks change, which we detect exactly like DH does.

## 2. Constraints (from research)

| Constraint | Consequence |
|---|---|
| VS 1.22.3, .NET 10, C# | Ground-up reimplementation; Java references are conceptual only |
| OpenGL **3.3 baseline** (macOS ceiling 4.1) | Core renderer = VAOs + per-section draws (DH-style). Voxy's compute/MDI pipeline is an *optional* GL 4.3+ fast path, gated at runtime |
| VS chunk = **32³**; world height configurable (256 default, up to 16k) | Section/column math uses 32 as the base unit; y fields sized for 16k |
| Client `TopRockIdMap`/`SnowAccum`/`MapRegion` are null | Surface material must be read from actual block data (guided by heightmaps) |
| No client chunk-unload event | Irrelevant: we snapshot on arrival; our cache outlives the chunk |
| Voxy license = all-rights-reserved | Concepts only. DH is LGPL — also concepts only (user decision: clean room, permissive license) |
| Farseer is **MIT** | Its client renderer/shader (camera-relative rendering, ZFar extension, fog-matched GLSL) is legally reusable with attribution — our rendering bootstrap |

## 3. Architecture overview

```
capi.Event.ChunkDirty (NewlyLoaded / MarkedDirty)
  └─ snapshot chunk (Unpack_ReadOnly → block ids, heightmaps; never touch live state after)
      └─ bounded player-centered priority queue, hash-gated (persisted per-chunk content hash)
          └─ ChunkToLod: 32×32 column scan → palette + vertical-RLE columns (§4)
              └─ LodStore: merge into leaf section, persist (SQLite), set ApplyToParent flag
                  ├─ MipPropagator: DB-flag-driven child→parent downsample (crash-safe, §5)
                  └─ dirty listeners → QuadTree.queueReload(sectionKey)
                      └─ RenderSectionBuilder (worker): section + neighbor edge strips
                          → resolve palette → colors/tint-classes → greedy quads → vertex buffer
                          └─ render-thread task queue (frame-budgeted) → GL upload
                              └─ LodRenderer (IRenderer @ Opaque, order 0.36):
                                 quadtree walk, frustum cull, near→far opaque / far→near water,
                                 seasonal tint uniforms, fog-matched shader, dithered near fade
```

Design DNA: **DH's pipeline shape** (column-RLE + persisted mip pyramid + quadtree +
crash-safe dirty flags) with **Voxy's encoding/scheduling ideas** (packed single-word
keys and voxel ids, early-out mip propagation, unified weighted worker pool, palette
serialization) and **Farseer's VS-specific rendering techniques** (render order, ZFar,
fog/curvature shader).

## 4. Data model

**Section** = 64×64 *data columns* at every detail level (VS: leaf section = 2×2 chunk
columns). Detail level D means each column covers 2^D × 2^D blocks.

**Section key** = one packed `long`: `detail(6) | x(29) | z(29)` (signed). One key
through cache, DB, quadtree, and render map (Voxy's "one identifier everywhere").

**Column data** = vertical RLE, top-down, gap-free (air runs stored explicitly, so light
and downsampling stay correct — DH's rule). One run = one packed `ulong`:

```
palette id (20 bits) | yTop (14 bits) | yBottom (14 bits) | skyLight (4) | blockLight (4) | flags (8)
```

14-bit y supports 16k-high worlds; flags reserve space for material class (§6) and
water/lava markers. Air = palette id 0 (id-zero test, Voxy's trick).

**Palette**: per-section id → VS block code (domain:path string), serialized with the
section blob (DH-style self-contained sections — no global registry to corrupt; palettes
are merged/remapped on section merge, compacted when they grow).

**Chunk → leaf conversion**: walk each of the 32×32 columns top-down from
`max(RainHeightMap[i], YMax)`; emit a new run on block-code change. Read via the raw
palette/data arrays (`IChunkBlocks`), not per-block accessors — Voxy showed this is the
difference between free ingestion and a frame-time problem.

**Downsampling (mip rule)**: 2×2 columns → 1: collect y-boundaries, sweep slices, pick
most-common (ties: most-opaque) palette id per slice, average light, re-RLE. Early-out:
if a level's merge produced no change, stop climbing (Voxy).

## 5. Storage

- **SQLite**, WAL mode, one DB per (server, world). Path:
  `VintagestoryData/ModData/vintagehorizons/<serverAddress>/<worldId>.db`. `worldId`
  derives from client-visible world identity (seed/dimension when available — Voxy's
  hash trick — else server address + world name).
- Tables: `Sections(detail, x, z, blob, palette, applyToParent, timestamps)` with PK
  `(detail,x,z)`; `ChunkHash(cx, cy, cz, hash)`; `Meta` (format version).
- Blob = palette-index plane + run list, ZSTD-1 or LZ4 compressed (decide by benchmark;
  both have good .NET libs).
- **`applyToParent` dirty flags persisted in rows** (DH): the mip propagator polls
  `WHERE applyToParent=1 ORDER BY dist(player) LIMIT n` — crash-safe pyramid consistency
  with zero in-memory dependency tracking.
- **Chunk hash gating** (DH): sparse-sampled content hash per chunk, persisted
  transactionally with its section; re-received identical chunks cost one hash compare.
- Write-merging cache: chunk updates merge in memory per section, flushed after ~1s of
  quiescence (adjacent chunk arrivals overwhelmingly hit the same section).

## 6. Rendering

**MVP path (GL 3.3, works everywhere):**

- `IRenderer` registered at `EnumRenderStage.Opaque`, `RenderOrder = 0.36` (just before
  real terrain → depth-occluded by it; Farseer-proven).
- ZFar extension via `ClientMain.MainCamera.ZFar` + `Reset3DProjection()`
  (VintagestoryLib internals — standard practice, Farseer does it).
- Player-centered **quadtree** of render sections; expected detail = log(distance).
  **A parent renders until all 4 children have uploaded buffers** (DH's no-holes rule);
  buffer swaps are atomic; root ring never renders.
- Mesh building on workers: load section + 4 neighbor **edge strips** (precomputed
  column strips stored beside each section — DH's trick to avoid deserializing whole
  neighbors), emit visible faces of each run-box culled against vertical neighbors and
  adjacent columns, then greedy-merge per face direction. Compact vertex format
  (~16 B/vertex): section-relative int16 position, RGBA color, normal index, light,
  tint-class index.
- GL uploads only on the render thread through a **frame-budgeted task queue** (~half a
  frame max, DH), camera-relative model matrices (`CameraMatrixOriginf`) for precision.
- Shader: GLSL 330, `#include`s the game's `fogandlight.vsh` / `vertexwarp.vsh` so fog,
  shadow, and globe-curvature match vanilla exactly (Farseer's approach, MIT).
- **Seam with real terrain**: skip LOD sections fully inside the approved view distance;
  Bayer-dithered discard fade at the boundary ring (DH); optionally push the innermost
  LOD ring down a few blocks like Farseer to hide silhouette mismatch.

**Seasonal/snow staleness — the VS-specific problem, solved in the shader:**

- Each palette entry is classified once into a **tint class**: `grass`, `foliage-deciduous`,
  `foliage-conifer`, `water`, `ice`, `rock`, `soil`, `sand`, `manmade`, `snow`.
- Vertex color stores the block's *base* color; the tint class indexes a small uniform
  array of *current* seasonal multipliers computed each frame from
  `capi.World.Calendar` + climate — so the whole LOD world re-colors continuously with
  seasons **without touching a single vertex buffer**.
- Snow cover: compute the current snow line from calendar/climate; the fragment shader
  whitens up-facing fragments above it. Approximate, but at LOD distances
  indistinguishable — and it changes daily like the real thing.
- Actual block changes (player builds, tree falls) arrive via `ChunkDirty(MarkedDirty)`
  → hash check → normal update path.

**Fast path (later, optional, GL 4.3+ detected at runtime):** Voxy-inspired — 8-byte
packed quads + vertex pulling, per-face-direction buckets, indirect multi-draw, Hi-Z
occlusion. Never required; the 3.3 path remains complete.

## 7. Threading

- **One unified worker pool** (n = cores/1.5), services scheduled by weighted-random
  proportional to `pending × weight` (Voxy): ingest ≫ save ≫ mesh ≫ mip-propagate.
  Trivial to express in C# (semaphore + dedicated threads); auto-balances with no
  per-service tuning.
- All queues **bounded and player-centered** (pop nearest, evict farthest — DH). The
  system sheds load rather than falling behind.
- Backpressure valves at every stage boundary (save-queue soft cap with caller-steal,
  mesh-queue dedup by section key, upload budget per frame).
- GL work exclusively on the render thread via the budgeted task queue.

## 8. Milestones

- **M0 — skeleton**: client-only ModSystem, ChunkDirty subscription logging, buildable
  csproj, launch config. *(done with initial commit)*
- **M1 — first pixels**: in-memory heightmap LOD from received chunks → colored
  heightmap mesh rendered past normal view distance (Farseer-class visuals, but
  client-built). Proves the whole loop: ingest → build → extended-ZFar render.
- **M2 — persistence**: SQLite store, per-server/world keying, chunk-hash gating,
  reload cache on join. Now horizons persist across sessions.
- **M3 — LOD pyramid**: full column-RLE model, mip levels + crash-safe propagation,
  quadtree detail selection with parent-until-children rule.
- **M4 — true 3D LODs**: run-box meshing with neighbor culling + greedy merge
  (overhangs, cliffs, caves-from-outside; DH-class visuals).
- **M5 — polish**: seasonal tint classes + snow line, water surface, config GUI,
  in-chat commands, ModDB release.
- **M6 — fast path (optional)**: GL 4.3 vertex-pulling/MDI renderer behind a runtime
  capability gate.

## 9. Licensing

VintageHorizons is **MIT plus a conduct condition** (see LICENSE); it is deliberately not plain MIT and not OSI-approved. DH (LGPL) and Voxy (ARR) inform concepts only — no code is
copied from either; `reference/` clones are gitignored and never redistributed. Farseer
(MIT) code may be adapted with attribution (will be credited in README and source
headers where used).
