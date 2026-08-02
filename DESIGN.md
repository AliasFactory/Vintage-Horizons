# VintageHorizons - Design

A Distant Horizons-style extended-render-distance LOD mod for Vintage Story that is
**fully client-side**: it works on any server, vanilla or modded, because it builds its
LODs exclusively from chunk data the client already receives.

Supporting research (file-path-anchored deep dives) lives in `docs/research/`:

- [`distant-horizons-architecture.md`](docs/research/distant-horizons-architecture.md) - the veteran design (LGPL-3.0; concepts only, clean reimplementation)
- [`voxy-architecture.md`](docs/research/voxy-architecture.md) - the fast newcomer (all-rights-reserved; **ideas only, never copy code**)
- [`vintage-story-api.md`](docs/research/vintage-story-api.md) - everything the VS 1.22.x client API gives us, with citations

## 1. Why this is possible (and why nobody has done it in VS yet)

- A `"side": "Client"` code mod can join unmodded servers - VS mod verification is
  one-directional (server→client requirements only).
- The client receives, for every loaded chunk column: full block data (32³ chunks,
  palette-compressed in RAM), `RainHeightMap`, `WorldGenTerrainHeightMap`, and `YMax`.
  `capi.Event.ChunkDirty` (reason `NewlyLoaded`/`MarkedDirty`) fires on arrival/change.
- Existing VS LOD mods (Farseer, ChunkLOD) generate LOD data **server-side** for instant
  full-map coverage. The cost is requiring server installation. We accept Distant
  Horizons' trade instead: coverage builds up as you explore, cached persistently
  per-server on disk - and works everywhere.
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
| Voxy license = all-rights-reserved | Concepts only. DH is LGPL - also concepts only (user decision: clean room, permissive license) |
| Farseer is **MIT** | Its client renderer/shader (camera-relative rendering, ZFar extension, fog-matched GLSL) is legally reusable with attribution - our rendering bootstrap |

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
and downsampling stay correct - DH's rule). One run = one packed `ulong`:

```
palette id (20 bits) | yTop (14 bits) | yBottom (14 bits) | skyLight (4) | blockLight (4) | flags (8)
```

14-bit y supports 16k-high worlds; flags reserve space for material class (§6) and
water/lava markers. Air = palette id 0 (id-zero test, Voxy's trick).

**Palette**: per-section id → VS block code (domain:path string), serialized with the
section blob (DH-style self-contained sections - no global registry to corrupt; palettes
are merged/remapped on section merge, compacted when they grow).

**Chunk → leaf conversion**: walk each of the 32×32 columns top-down from
`max(RainHeightMap[i], YMax)`; emit a new run on block-code change. Read via the raw
palette/data arrays (`IChunkBlocks`), not per-block accessors - Voxy showed this is the
difference between free ingestion and a frame-time problem.

**Downsampling (mip rule)**: 2×2 columns → 1: collect y-boundaries, sweep slices, pick
most-common (ties: most-opaque) palette id per slice, average light, re-RLE. Early-out:
if a level's merge produced no change, stop climbing (Voxy).

## 5. Storage

- **SQLite**, WAL mode, one DB per (server, world). Path:
  `VintagestoryData/ModData/vintagehorizons/<serverAddress>/<worldId>.db`. `worldId`
  derives from client-visible world identity (seed/dimension when available - Voxy's
  hash trick - else server address + world name).
- Tables: `Sections(detail, x, z, blob, palette, applyToParent, timestamps)` with PK
  `(detail,x,z)`; `ChunkHash(cx, cy, cz, hash)`; `Meta` (format version).
- Blob = palette-index plane + run list, ZSTD-1 or LZ4 compressed (decide by benchmark;
  both have good .NET libs).
- **`applyToParent` dirty flags persisted in rows** (DH): the mip propagator polls
  `WHERE applyToParent=1 ORDER BY dist(player) LIMIT n` - crash-safe pyramid consistency
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
  (VintagestoryLib internals - standard practice, Farseer does it).
- Player-centered **quadtree** of render sections; expected detail = log(distance).
  **A parent renders until all 4 children have uploaded buffers** (DH's no-holes rule);
  buffer swaps are atomic; root ring never renders.
- Mesh building on workers: load section + 4 neighbor **edge strips** (precomputed
  column strips stored beside each section - DH's trick to avoid deserializing whole
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

**Seasonal/snow staleness - the VS-specific problem, solved in the shader:**

- Each palette entry is classified once into a **tint class**: `grass`, `foliage-deciduous`,
  `foliage-conifer`, `water`, `ice`, `rock`, `soil`, `sand`, `manmade`, `snow`.
- Vertex color stores the block's *base* color; the tint class indexes a small uniform
  array of *current* seasonal multipliers computed each frame from
  `capi.World.Calendar` + climate - so the whole LOD world re-colors continuously with
  seasons **without touching a single vertex buffer**.
- Snow cover: compute the current snow line from calendar/climate; the fragment shader
  whitens up-facing fragments above it. Approximate, but at LOD distances
  indistinguishable - and it changes daily like the real thing.
- Actual block changes (player builds, tree falls) arrive via `ChunkDirty(MarkedDirty)`
  → hash check → normal update path.

**Fast path (later, optional, GL 4.3+ detected at runtime):** Voxy-inspired - 8-byte
packed quads + vertex pulling, per-face-direction buckets, indirect multi-draw, Hi-Z
occlusion. Never required; the 3.3 path remains complete.

## 7. Threading

- **One unified worker pool** (n = cores/1.5), services scheduled by weighted-random
  proportional to `pending × weight` (Voxy): ingest ≫ save ≫ mesh ≫ mip-propagate.
  Trivial to express in C# (semaphore + dedicated threads); auto-balances with no
  per-service tuning.
- All queues **bounded and player-centered** (pop nearest, evict farthest - DH). The
  system sheds load rather than falling behind.
- Backpressure valves at every stage boundary (save-queue soft cap with caller-steal,
  mesh-queue dedup by section key, upload budget per frame).
- GL work exclusively on the render thread via the budgeted task queue.

## 8. Milestones

- **M0 - skeleton**: client-only ModSystem, ChunkDirty subscription logging, buildable
  csproj, launch config. *(done with initial commit)*
- **M1 - first pixels**: in-memory heightmap LOD from received chunks → colored
  heightmap mesh rendered past normal view distance (Farseer-class visuals, but
  client-built). Proves the whole loop: ingest → build → extended-ZFar render.
- **M2 - persistence**: SQLite store, per-server/world keying, chunk-hash gating,
  reload cache on join. Now horizons persist across sessions.
- **M3 - LOD pyramid**: full column-RLE model, mip levels + crash-safe propagation,
  quadtree detail selection with parent-until-children rule.
- **M4 - true 3D LODs**: run-box meshing with neighbor culling + greedy merge
  (overhangs, cliffs, caves-from-outside; DH-class visuals).
- **M5 - polish**: seasonal tint classes + snow line, water surface, config GUI,
  in-chat commands, ModDB release.
- **M6 - fast path (optional)**: GL 4.3 vertex-pulling/MDI renderer behind a runtime
  capability gate.
- **M7 - optional server assist**: same mod, installable server-side, feeding clients
  terrain they have never visited. See §10.

## 9. Licensing

VintageHorizons is **MIT**. DH (LGPL), Voxy (ARR) and Algernon's Terrain Sampler (no
LICENSE shipped, so ARR) inform concepts only - no code is copied from any of them;
`reference/` clones are gitignored and never redistributed. Farseer (MIT) code may be
adapted with attribution (will be credited in README and source headers where used).
TopoHorizon (MIT, (c) 2026 Jack Brown) gets the same rule as Farseer: adaptable with
attribution. Section 14 uses its measured PeekChunkColumn constants, credited in
`LodPlayerPregen`. ChunkLOD ships as a binary with no license: treat it as ARR, copy
nothing.

## 10. Optional server assist (M7)

### 10.1 The problem it solves

The client-only design has exactly one weakness, and it is the one thing Farseer,
ChunkLOD and TopoHorizon genuinely do better: we can only draw terrain the server has
already sent us. A brand-new world shows nothing past the vanilla view distance until
the player travels, and the flanks of a flight path stay empty.

Those mods solve it by generating LOD server-side - and pay for it by being
`requiredOnClient`, so a server running one forces the mod on everybody and a client
running one cannot join a server without it. It is all-or-nothing in both directions.

The assist closes our gap without taking on theirs: **works on every server, better on
servers that opt in.**

### 10.2 The constraint everything else is subordinate to

The client must never require the server side. If installing this on a server starts
forcing it on joining players, we have reimplemented Farseer and thrown away the only
reason this project exists.

Vintage Story supports exactly what is needed. From `ModInfo.RequiredOnClient`:

> If set to false and the mod is universal, clients don't need the mod to join.

So one mod, shipped once:

```json
"side": "Universal",
"requiredOnClient": false,
"requiredOnServer": false
```

Both flags matter, in opposite directions:

| installed on | result |
| --- | --- |
| client only | today's behaviour, unchanged, on any vanilla server |
| server only | server serves data; clients without the mod are unaffected and still join |
| both | channel connects; unvisited terrain is filled in |
| neither | n/a |

`requiredOnServer: false` is what keeps a client with the mod able to join a vanilla
server - dropping it inverts the problem instead of solving it.

One mod rather than a companion download also removes a compatibility matrix that
would rot: no pairing of client 0.1.1 against server 0.2.0 to reason about, one
version number, one zip for both audiences.

### 10.3 Architecture: a third implementation of an existing seam

The section source is already pluggable, and the async path added in the storage work
is the exact shape a network source needs - request by key, answer arrives later,
install on the main thread:

- `LodWorld.LoadFromStore` - `Func<long, LodSection?>`, synchronous, local disk
- `LodWorld.RequestAsyncLoad` - `Action<long>`, results land via `InstallLoaded`
- `LodStore.Serialize` / `Deserialize` - already a self-contained deflated `byte[]`

That last point matters more than it looks: **the stored blob is the wire format.**
There is no second serialisation to design, and a section that survives a round trip
through the network is byte-identical to one loaded from disk.

So the client change is small: when the channel is connected, a key that misses
locally is asked for over the network instead of returning empty.

### 10.4 What the framing hides

Three parts are real work, and none of them is transport:

**The server has no LOD database to serve.** It has to build one - running the same
capture over chunks it holds and keeping it current as the world changes.
`LodWorker.Capture` reads `IWorldChunk` and `LodStore` needs only an `ILogger`, so both
port as-is; the coordinator around them is the client `ModSystem` and does not.

**The server cannot colour a palette.** `RegisterPaletteEntry` calls
`Block.GetColorWithoutTint(ICoreClientAPI, BlockPos)`, which bottoms out in
`capi.BlockTextureAtlas.GetAverageColor` - a dedicated server has no texture atlas at
all, so the one field it physically cannot fill is the one every palette entry needs.
Sections must therefore travel **colour-unresolved**, with the client filling colour in
on receipt. That is less invasive than it sounds: `ResolvePendingPalette` already runs
client-side on every section that comes off disk, already re-resolves block ids from
codes, and already has the block in hand - it gains a colour pass.

**And it needs no schema change.** The first plan here was to persist an
"unresolved" marker in the blob, which meant bumping `LodStore.SchemaVersion` - and that
version is a cache-wipe: every existing player would lose the horizons they had explored,
to enable something not yet switched on. Unnecessary, because the *transport* knows where
a section came from. A section arriving over the channel gets the flag set in memory
before install; a section off local disk never needs it. Server-side rows hold colour 0
and only the server reads them, and it never renders.

**The client cannot ask for what it does not know exists.** Quadtree descent is driven
by `HasDataSet`, populated at join by `LoadAllKeys` scanning the local DB. Against a
remote source the client has no key set, so it can neither descend into remote areas
nor tell that a request is worth making. The handshake therefore has to carry a **key
manifest** - keys only, exactly what `LoadAllKeys` already yields, no blobs.

### 10.5 Precedence

When both sources hold a section, **local capture wins**; the server fills gaps only.
The client's own capture is what it actually observed, including player edits it
witnessed, whereas the server's copy may be an older snapshot. Letting the server
overwrite would let stale terrain replace fresher ground the player is standing on.

### 10.6 Revealing the map

Sending terrain a player has never visited hands them a survey of the world:
coastlines, structures, other players' bases. The competing mods have the same
property, but that is not a reason to ship it thoughtlessly - some admins will
consider it cheating, and they are not wrong to.

It must be admin-configurable, and the default must be conservative:

- a radius cap on how far from a player the assist will serve
- already-generated chunks only by default; never trigger worldgen to satisfy a
  request (this is also what makes the server-side mods expensive)
- an outright off switch

### 10.7 Transport

- **The 508-byte limit does not apply here.** Measured against the source rather than
  assumed: the warning sits only on the two `RegisterUdpChannel` overloads, never on
  `RegisterChannel`, and it is about NAT fragmentation of datagrams. The reliable
  channel has no such cap. Sections are still tens to hundreds of KB, so chunk them
  anyway - for latency and peak memory, not because a limit forces it.
- **Rate limit and bound requests.** A client must not be able to ask for unlimited
  area; the server decides what it is willing to send, not the client.
- **Protocol version in the handshake.** 0.1.1 clients already exist; a client must
  ignore anything it does not understand rather than misparse it.

### 10.8 Code layout

Going Universal means the assembly loads on servers for the first time. Split by side
rather than branching inside one system:

- `VintageHorizonsModSystem` - `ShouldLoad(side) => side == Client` (unchanged)
- a new server system - `ShouldLoad(side) => side == Server`

The client system casts `capi.World` to `ClientMain`, compiles shaders and registers a
renderer. None of that may execute server-side, and the robust guarantee is that the
code never runs there at all, rather than a branch that is one refactor away from
being wrong.

### 10.9 Staging

1. ~~Handshake only~~ **done**: channel connects, versions exchanged, `.vhinfo` reports
   whether an assisting server was found. See §10.11 for what it proved.
2. ~~Server-side capture~~ **done**. Reordered ahead of the manifest: a manifest lists
   what the server has, and until it captures, it has nothing. `LodPipeline` +
   `LodBlockPolicy` are the extracted, side-agnostic coordinator; `LodServerCaptureSystem`
   drives it from `ChunkColumnLoaded` plus block-edit events, since a live column never
   fires `ChunkColumnLoaded` again. Measured: a dedicated server built 85 sections with a
   complete pyramid (51/19/8/4/1/1/1 across detail 0–6), 0 unflushed mip flags after a
   clean stop, 0 errors on either side.
3. ~~Key manifest~~ **done**. Sent in full at handshake, not by spatial query: a real
   5581-section world is 44 KB at 8 bytes a key, so the manifest is not the expensive
   part - the sections are, at a mean 45.9 KB each (median 44.2, p95 86.4, max 154.5).
   That is what stage 4 has to budget for: "send what the client lacks" for that world
   would be 262 MB. Measured at volume: 5665 keys in 3 chunks, announced count exact,
   0 errors. Welcome and manifest come from one main-thread snapshot, because answering
   from the message handler read a set the capture tick mutates and the announced count
   disagreed with what followed.
4. ~~Section transfer~~ **done**. The client asks only for keys the manifest offered that
   it has no local data for; the server answers with the stored blob verbatim, since the
   wire format *is* the storage format. Arrivals are installed and **persisted into the
   client's own cache**, so the server seeds the cache rather than streaming to it - at a
   measured mean 45.9 KB a section, re-fetching every session was never an option, and a
   player who leaves the server keeps what they pulled. Measured end to end: 96 requested,
   96 received, 96 installed, 0 declined, 0 errors; 225 sections persisted locally
   afterwards (129 captured here, 96 from the server) with a complete pyramid and no
   unflushed mip flags.

   Three limits, and the two that matter are server-side, because a modified client
   ignores its own: `MaxSectionsInFlight` (16, client courtesy),
   `MaxSectionsPerSecondPerPlayer` (8, ~370 KB/s at the measured mean) and
   `MaxSectionsPerSecondTotal` (32). The last one is the one that protects the server:
   per-player fairness does not bound the *sum*, and every section served is a main-thread
   SQLite blob read, so twenty players at 8/s each would be 160 reads a second of tick
   time. Serving is round-robin from a rotating start so the global budget cannot be
   monopolised, and a 256-key queue per player drops the excess rather than growing.

   Three bugs worth remembering, all of them the same shape: a key parked in a state
   nothing ever clears, so terrain froze rather than degraded.

   - Keys held back by the in-flight cap were dropped from the pending set while still in
     `LodWorld.LoadsInFlight`, where the render scheduler skips them - stranded for the
     session, and transfer moved *nothing*. Only keys actually sent may be forgotten.
   - A declined key was cleared from the client's own in-flight set but not from
     `LoadsInFlight`, pinning its parent coarse forever.
   - **`HasDataSet` cannot answer "can local disk supply this?"** `RegisterInTree` walks
     *upward*, so registering any L0 key marks its L1–L6 ancestors as having data. Testing
     manifest keys against it therefore skipped coarse keys the server really held -
     whichever of a node or its descendants was enumerated first decided the other's fate.
     Those keys stayed out of `RemoteOnly`, routed to a store with no such row, returned
     null, and landed in `LoadFailed`, which is permanent. It showed as hard-edged regions
     drawn at L5 at any distance with the pipeline idle. The store's own key set is now
     tracked separately (`localKeys`) and that is what the manifest is tested against.

   Diagnosing the third took three wrong answers read off counters. What settled it in one
   shot was `.vhwhy`, which prints each coarse-drawn node's four children with their actual
   state (`no-data` / `not-resident` / `loading` / `load-failed` / `empty` / `meshing` /
   `no-mesh!` / `ok`). Instrument the decision, do not infer it.
5. ~~Admin config and defaults~~ **done**. `ModConfig/vintagehorizons-server.json`, written
   on first start so the options are discoverable without reading source:
   `EnableCapture`, `EnableServing`, `ServeRadiusBlocks` (default 8192), and both rate
   caps. Values are clamped on load, so a hand-edited file cannot wedge the server.
   `/vhserver` reports the settings in force plus what has actually been served.

   Serving defaults **on**, which is a deliberate departure from "conservative defaults":
   installing the mod on a server *is* the opt-in, and a mod that silently does nothing
   until a file is edited reads as broken. The conservatism lives in the radius instead -
   an admin who wants no sharing sets `EnableServing` false, and one who wants some gets a
   bounded amount rather than the whole world.

   The radius is checked when a section is about to be sent, against where the player is
   *then*, not when the request was queued - a request that waited must not be honoured for
   somewhere the player has since left. Distance is nearest-edge, not centre-to-centre: an
   L6 section spans 4096 blocks, so centre distance would refuse sections the player is
   standing inside.

   Measured, same world and empty client cache each time: default 8192 → 87 requested, 87
   received, 87 installed, 0 declined. Radius 512 → 92 requested, 13 received, 79 declined
   as out of radius, and the client remembered the refusals instead of re-asking.
   `EnableServing: false` → client reports "this server has a LOD cache but is not sharing
   it" and fetches nothing. 0 errors on either side throughout.

Singleplayer is **excluded**, and the earlier claim that it was the biggest early payoff
was wrong. The integrated server does load the server side and the channel does connect
in-process (§10.11) - but capture is driven by chunks loading, and in one process the
server loads exactly the chunks the client is already shown, so a second pipeline
duplicates the cache file, the work and the memory for nothing. Found live: two
"LOD cache:" lines naming one database and a manifest of 3851 keys the host already had.
Server capture now requires `api.Server.IsDedicated`, and the server cache carries a
`-server` filename suffix so the collision cannot recur silently.

What would genuinely pay in singleplayer is sweeping the savegame for chunks generated in
past sessions - terrain the client has no other way to see. That is unbuilt, and is the
real form of the payoff this section originally over-claimed.

Running real worldgen on demand is explicitly not in scope: it is the expensive half of
what the server-side mods do, and doing without it is what keeps the assist cheap enough
for an admin to leave on. §10.10 covers a cheaper way to reach the same terrain.

### 10.10 Predicted terrain (Algernon's Terrain Sampler)

`algernonsterrainsampler` reimplements GenTerra's noise pipeline so a caller can ask
"what is the surface height, climate, rainfall and forest density at (x, z)" for a chunk
that has never been generated or loaded. It is what Farseer's fast path uses, via
reflection on `TerrainSamplerMod.GetBlockColumnHeight` / `SampleColumn`.

This narrows the gap in §10.9: the ruled-out cost was *generating chunks*, and sampling
noise is not that. A server that also has the sampler could answer for land nobody has
been to, which is the last thing the competitors do that we would not.

It stays optional, and below capture, for reasons that are not incidental:

- **A sample is not a capture.** It yields height plus climate, so a column has to be
  *synthesised* - the surface block inferred from temperature and rainfall - instead of
  replaying blocks that are actually there. No structures, no player edits, no accurate
  block choice. That is precisely the look we currently beat. It belongs in a third tier
  below server capture, which is already below local capture (§10.5), and must be
  overwritten the moment real data for the same key arrives.
- **It forces a client download.** Its `modinfo.json` declares `RequiredOnClient: true`
  even though `ShouldLoad` restricts it to the server, so a server that installs it
  makes *every* player fetch it - including players not running VintageHorizons. An
  admin add-on that taxes uninvolved players is a real cost to state plainly in the
  docs, not a footnote.
- **It is only as right as the worldgen it models.** Accuracy degrades with terrain-gen
  mods; it delegates to Watersheds when present but otherwise predicts the pre-river
  landscape. Wrong-but-confident terrain is worse than absent terrain, so the off switch
  in §10.6 covers this too.

Licensing: the repository ships no LICENSE, so it is all rights reserved. Integration is
reflection-only - the documented path, needing no assembly reference and no copied code.
Same rule as Voxy (§9): read for understanding, copy nothing.

Client-side prediction is a dead end, recorded so it is not re-derived. `IWorldAccessor.Seed`
is documented "Accessible on the server and the client", which makes it look feasible -
but `AssetCategory.worldgen` is `EnumAppSide.Server`, so a client never loads the landform
or geologic-province definitions the noise is shaped by, and a modded server's worldgen is
not knowable from the client at all.

## 11. Known issues

**Wrong LOD colour for blocks whose block-colour texture did not resolve.** Reported by a
player running Conquest Reforged + Better Ruins: blocks look correct up close but wrong at
LOD distance. Partly fixed - `DescribePalette` now rejects an unusable atlas sub-id (out of
range, or an unassigned `Positions` entry) and the unknown.png average, and falls back to
any other baked texture the block owns, cached per block id.

Root cause is in vanilla, not in the mods: `Block.LoadTextureSubIdForBlockColor` tries the
`textureCodeForBlockColor` attribute, then `"up"`, then `Textures.First()` - and that last
step ends in `?? 0`, so a block whose first texture in dictionary order has no `Baked`
entry silently resolves to atlas sub-id 0. Other faces bake fine, which is exactly why the
block looks right up close and wrong only in LOD. Confirmed firing on vanilla
`game:fruitingbush-wild-blackberry-free`.

**Not confirmed to be the reported symptom.** The player described *purple*, and
`unknown.png` measures near-white (32×32, mostly white with a small red mark, average
255,249,249 - matching the `00FCFCFC` the atlas reports). So a magenta block is coming from
somewhere else, most likely `GetAverageColor` on a sub-id the atlas never assigned, which
the range/null guard now also catches. To close it properly, ask the reporter for the block
code, whether it looks right up close, and any "texture not found" lines in
`client-main.log`.

### 10.11 What stage 1 measured

Three installs, on the sandbox client and dedicated server (`scripts/test-*.sh`):

| client | server | result |
| --- | --- | --- |
| no mod | mod | joins normally, no missing-mods rejection |
| mod | mod | hello/welcome round trip, 0 errors |
| mod | no mod | 0 errors, capture and rendering unchanged |
| singleplayer (both sides in one process) | | `LodAssistServerSystem` starts, handshake completes in-process, 0 errors |

The middle row is the cheap one. The first is the constraint in §10.2 and the third is
where the design was wrong.

**`GetChannelState` is not the test to use.** Against a vanilla server it returned
`Connected` for a channel that was not, and `SendPacket` then threw *"Attempting to send
data to a not connected channel"* - from inside a `LevelFinalize` handler, which the
engine aborts on exception, so the optional extra took out the rest of the mod's own
startup. Guard on `IClientNetworkChannel.Connected`, which is what the engine's error
message names, and keep the handshake at the end of the handler behind a `try`. An
optional feature must never sit upstream of the work it is optional to.

**One cosmetic cost on vanilla servers.** The client logs
*"Client registered 1 network channels (vintagehorizons) the server does not know about"*
at startup. Unavoidable for an optional channel - registration has to precede the
connection handshake, so there is no point at which we could know to skip it - and it is
a log line, not a dialog.

**Do not ship `side: Universal` before stage 3.** It changes how the mod is categorised
(and filtered for) on ModDB while delivering nothing to a player yet. The switch belongs
in the release that actually serves terrain.

## 12. The test regimen

Every correctness claim above was originally established by a hand-executed sandbox run,
read off `.vhinfo` counters, and written into a commit message. Those runs were not
repeatable without a human driving them, so nothing re-checked them. `scripts/check.sh` is
the standing version: three tiers, cheapest first, stopping at the first failure.

**There can be no CI.** Building requires the Vintage Story assemblies from a local game
install and those are not redistributable, so no hosted runner can compile this repo. The
regimen is local-only by necessity, not by preference.

### 12.1 Tiers

| tier | time | proves |
| --- | --- | --- |
| `fast` | ~1 s | pure logic and cross-file invariants, no game process |
| `smoke` | ~5 min | the pipeline runs end to end, cold and then warm |
| `matrix` | ~20 min | install combinations and every admin control |

`fast` is a plain console assert harness in `tests/VintageHorizons.Checks`, run with
`dotnet run`. No test framework: this repo has no NuGet dependencies at all and none
cached locally, so a framework would mean the fast tier could not run without a network.
It is also sequential, which is not a limitation to work around - `LodWorld.DetailDistance`
is a mutable static that several checks set, so sequential is the only correct order.

The one thing that could have sunk the tier is assembly loading. The mod references the
game DLLs with `Private=false` so they never travel in the release zip, which also keeps
them out of its `deps.json` entirely - nothing puts them on the TPA list, and references
do not flow through `ProjectReference`. The test csproj restates them without that flag,
and `GameAssemblies` installs an `AssemblyLoadContext.Default.Resolving` handler from a
`[ModuleInitializer]` that probes the install directly. `ProbeChecks` runs first and does
nothing but force the two loads that can genuinely fail: `Block`, whose ~200-method vtable
reaches furthest, and `LodStore`, which pulls in `Microsoft.Data.Sqlite` merely by
existing.

### 12.2 What the fast tier found immediately

**The shader constant guard could not catch what it existed to catch.** `LodTerrainRenderer`
compared `LodTintRegistry.MaxSlots` against `LodTintRegistry.GlslTintSlots` - two C#
constants in the same file. `GlslTintSlots` was a hand-maintained mirror of a number that
actually lives in `lodterrain.vsh` and `.fsh`, so editing a shader and forgetting the
mirror left the guard passing while water decoded as opaque and thin plants decoded as
water, with no compile error. The compiler said so plainly: that comparison raised CS0162,
unreachable code, because both sides were compile-time constants with the same value.

Both the mirror and the dead guard are now gone, and three sources of truth are two. The
static suite reads the shader files, which is the only thing that can close it, and it
also catches the `.vsh` and `.fsh` disagreeing with each other - which nothing did before.

While there, `MaxSlots * 3 <= 256`: the mesher packs three tint bands into a byte alpha,
so raising `MaxSlots` past 85 wraps the thin band into the opaque band silently.

**A fresh install announced that it was discarding data it never had.** `PurgeOutdatedData`
compared the stored `FormatVersion` against the schema version, and a brand new database
has no such row - so `null != "6"` and every first-ever run logged *"LOD cache semantics
changed; discarding old cached data"*. Found by the smoke tier on its first execution,
because the assertion "this line must not appear" is unconditional. Now conditional on a
version actually having been present.

### 12.3 Testability changes

Four, kept as small as the job allowed. `LodServerPregen.SpiralAt` and
`LodAssistServerSystem.WithinServeRadius` became public (the latter keeping a private
overload that does the player deref, so the mid-join no-position case keeps its own
answer); the three `LodAssistClient` packet handlers became `internal` with
`InternalsVisibleTo`.

The fourth is `LodRemoteKeySet`, extracted from `LodPipeline`. That set logic is pure -
it needs only a `LodWorld`, which has no constructor and no API field - but it sat behind
a constructor that takes an `ICoreAPI` and starts five threads, so it could not be reached.
It holds the `localKeys`-versus-`HasDataSet` distinction, the most expensive bug in the
project's history, and it now has a regression test that fails on reintroduction.

### 12.4 A check that has never gone red is not a check

Each new assertion was confirmed to fail by mutating the code it guards, then reverting:

| mutation | caught by |
| --- | --- |
| `TINT_SLOTS` 64 → 32 in the `.vsh` | 1 assertion |
| a non-ASCII byte in a shader | the ASCII scan |
| `localKeys` → `HasDataSet` in `AddRemoteKeys` | 7 assertions |
| thin-mat offset measured down from the top | 3 assertions |
| mip majority 2 → 1 | 1 assertion |

The `localKeys` mutation is the one that matters: its first three failures are exactly the
symptom that took three wrong diagnoses to find the first time.

### 12.5 The serve radius, finally verified

The radius cap had been measured once and never watched. It is the map-revealing control -
sections come from wherever players have collectively been, so without it a new player
could pull a survey of the whole explored world without travelling - and it was the one
admin-facing setting with no verification at all.

The trap is that **a bare decline count proves nothing**. A section resident in RAM but not
yet flushed to disk is also declined, and an uncapped run was measured producing 55 of them.
Terrain missing at distance looks identical whether the server refused it or never had it.
So the check serves the *same pre-generated cache twice*, with the cap as the only
difference:

| | offered | installed | declined |
| --- | --- | --- | --- |
| uncapped (radius 0) | 806 | 633 | 55 |
| capped (radius 512) | 446 | 274 | 415 |

Same 861-key manifest both times. The cap cut delivered sections by more than half and
raised refusals by an order of magnitude, and because the control run had the identical
data available, that difference can only be the radius. A second independent run
reproduced it closely - 302 installed / 386 declined capped against 629 / 46 uncapped -
so the effect is the control, not one lucky sample.

**The visual half does not work, and the honest thing is to say so rather than dress it
up.** The check captures a screenshot pair from one vantage, capped and uncapped, and the
images are not reproducible run to run. Three attempts, same route, same configs:

- y=420 put the camera inside the cloud layer - two frames of white fog.
- y=260, 75s settle - the capped frame showed near terrain plus a patchwork of flat coarse
  plates, the uncapped frame showed continuous terrain. Tempting to read as the cap, but
  the capped client was screenshotted with 348 sections resident and only 20 meshed, so
  most of what distinguished the two frames was meshing progress.
- y=260, 180s settle - the capped frame came back **empty**, no terrain at all, while the
  uncapped frame showed a full landscape. A capped run cannot legitimately render less at
  180s than it did at 75s, so something other than the radius is driving the picture.

The counters, over the same three runs, were tight: 274/302/302 installed capped against
633/629/634 uncapped. So the cap is verified and the camera is not.

Two things work against a usable image and neither is cheap to fix: the game's atmospheric
fog swallows detail well before 512 blocks at any vantage that also clears the cloud layer,
and what the client has *fetched* is not what it has *drawn* - meshing, eviction and the
quadtree's own descent all sit in between. **The screenshot step is therefore informational
only.** It asserts that two captures were produced, never what is in them, and nothing
should be concluded from the pair without reading the counters beside it.

### 12.6 What tier 3 got wrong first

Every scenario failure on the first full run was the harness, not the mod - which is its
own lesson, since each one would have read as a product bug to anyone trusting the output:

- **`no-client-mod` waited for a log line that does not exist.** There is no "Loaded Game"
  in a Vintage Story client log. The marker also has to prove the join *completed*:
  "Connected to server" appears during the handshake and so would appear on a run about to
  be rejected. Receiving the block registry only happens after acceptance.
- **`deferral` could never have deferred.** Farseer is `requiredOnServer`, so against the
  vanilla server the scenario used, the client disabled it and `IsModEnabled` returned
  false - nothing to defer to. Installing it on both sides is also the real-world shape.
- **`deferral` waited for "Level finalized"**, which the deferring path returns before ever
  reaching.
- **Scenarios were not standalone.** `no-client-mod` relied on a server an earlier scenario
  had left running, so `--only no-client-mod` got "connection refused". Every scenario now
  starts its own.
- **The visual vantage point was inside the cloud layer.** y=420 rendered two identical
  frames of white fog. 260 with a -20 pitch is the same vantage as the existing
  `high-overlook` waypoint, which is known to render terrain well past the ring distance.
- **The server restart budget could not outlast TIME_WAIT.** `test-server.sh` retried a
  busy port four times at 10s, and Linux holds TIME_WAIT for about 60s. That was survivable
  for as long as every restart had a long client run in front of it; adding the uncapped
  control meant restarting twice in a row, and the bind failed for good. Now six at 15s.

## 13. Savegame sweeping

The LOD cache only ever held terrain that happened to stream past a player running this
mod. The savegame holds everything anyone has ever generated. On a test world that was
12,632 generated chunk columns against 620 captured sections - and a world played for weeks
skews far harder. All of it is already on disk, already paid for.

Sweeping loads those columns so capture sees them. It is the cheap half of pre-generation
and the half worth defaulting on: pregen *creates* terrain nobody has visited, costing
worldgen time and disk and revealing places no player has been, while a sweep creates
nothing and reveals nowhere new.

### 13.1 Keeping the "generates nothing" promise

`LoadChunkColumnPriority` generates a column that is not there, so the gate has to be
exact. `TestMapChunkExists` is the supported check and is documented as not loading chunk
data - the guarantee comes from the API contract rather than from any assumption about the
save format.

Gating the target column alone is not enough, and this is the part that had to be measured.
Worldgen runs in passes, and a column only reaches `Done` once its neighbours have reached
an earlier pass - which needs *their* neighbours. So loading a column near the frontier of
explored terrain makes the engine generate whatever is missing beside it. Sweeping one
world at each neighbourhood width, counting the chunk columns the savegame gained:

| neighbourhood required intact | columns generated |
| --- | --- |
| none (target only) | 1460 |
| radius 1 (3×3) | 714 |
| radius 2 (5×5) | 509 |
| radius 4 (9×9) | **0** |

Radius 3 was not tested, so 4 may be one wider than strictly needed. Erring wide is the
right direction: too narrow silently breaks the only promise the feature makes, while too
wide costs a slightly thicker border of terrain going uncaptured.

What identified the cause was sweeping a radius that fell entirely *inside* generated
terrain: 4,225 of 4,225 positions existed, all were loaded, and the savegame gained exactly
zero. That ruled out loading as the mechanism and left the frontier.

Because neighbour state has to be known before anything is loaded, the sweep is two passes:
probe every position (reaching one neighbourhood beyond the load area, so edge columns are
not skipped for want of information), then load only what has an intact surround.

### 13.2 How swept terrain reaches a singleplayer client

Only the server side can ask for columns the player is nowhere near, and only the client
side has a texture atlas. Each half holds exactly what the other lacks, so swept sections
travel the same road as sections fetched from a real server: captured with every palette
colour at 0, written to the `-server` cache, then read by the client, recoloured from their
block codes on install, and stored in its own cache. `LodRemoteKeySet` does not care whether
a blob arrived over a socket or off the disk beside it, which is why the client half is a
reader (`LodLocalOfferSource`) rather than a subsystem.

Verified end to end on a singleplayer world of 8,766 generated columns: the server side
swept 5,847 of them, the client finished with 670 sections resident having captured only
309 columns itself, the savegame row counts were unchanged, and sampling palette entries
showed the server cache **0% coloured** against the client cache **100% coloured** - which
is the recolour path working, since swept geometry starts with no colour at all.

Adoption is demand-driven: the client took 698 of the 2,158 sections the sweep produced,
because `RemoteWanted()` is fed by what the render path actually wants to draw.

The singleplayer guard in `LodServerCaptureSystem` is therefore conditional rather than
absolute. Running both sides in one process is still pointless for ordinary play - the
server loads exactly the chunks the client is shown - but a sweep deliberately loads
columns the client will never be shown, which is the one thing that side can do there that
the client cannot do for itself.

## 14. Chunk generation on request (/vhgen)

The last gap against the server-side mods: everything above can only capture terrain
that exists. `/vhgen start [radius] [x z]` closes it, on request. A person with the
controlserver privilege (any singleplayer host, or an admin on a dedicated server)
generates the LOD picture around themselves - or around explicit coordinates - without
adding one byte to the savegame.

Section 10.9 ruled worldgen-on-demand out because generation is the expensive half of
what Farseer and ChunkLOD do. That reasoning still holds for anything automatic, which
is why nothing here runs on its own. What changed is the discovery that the engine
ships `PeekChunkColumn`: real worldgen from the seed, to a configurable pass, that
neither reads nor writes the savegame ("it won't save it or load it into the loaded
map region list"). Farseer and TopoHorizon both build on it.

### The decision rule

The sweep's probe already sorts every position with `TestMapChunkExists`. The sweep
acts on the columns that exist; generation acts on the rest. One rule covers both,
extracted to `LodColumnMap.Classify` so the fast tier can test it:

  does not exist                      -> PEEK it (safe anywhere: touches nothing)
  exists, 9x9 neighbourhood intact    -> LOAD it (the sweep's measured rule)
  exists, on the frontier             -> touch nothing

The asymmetry carries the two promises. A peek of a column that EXISTS would
regenerate it from the seed and cache the terrain as it was before anyone built there,
so an existing column is never peeked. A load of a frontier column would make the
engine generate its missing neighbours, so the frontier is never loaded.

### What a run does

Probe the square (radius + 4 for edge information), then walk the same spiral acting
on the rule, then verify. Peeked columns come back as `IServerChunk[]` with the rain
height map populated (confirmed on this build - the spike measured heights 113-135,
zero sentinel values, at the Terrain pass), and go through `LodPipeline.CaptureColumn`
- the BlockAccessor cannot see a peeked column, so the ChunkColumnLoaded path does not
apply. Loaded columns queue through `OnLoaded` rather than the ChunkColumnLoaded
event, so generation works where nothing subscribes that event (singleplayer with
sweeping off).

Rate and memory are bounded twice: `GenerateColumnsPerSecond` (default 16) and
`GenerateMaxInFlight` (default 64) plus the capture backlog gate. The in-flight cap is
a memory ceiling, not only a contention control - each landed peek is a whole unpacked
chunk column (1-2 MB) held until the capture thread drains it. TopoHorizon measured
the contention side: unbounded peeking reached ~520 in flight and slowed every peek.

Peeks run to the TERRAIN pass, as a constant. Terrain includes the block layers, so
LOD colour is right; it excludes trees. The Vegetation pass would add them at 2-3x the
cost, a neighbour-generation cascade, and two NullReferenceExceptions inside vanilla
worldgen that TopoHorizon suppresses with a Harmony finalizer this mod does not ship.
Generated terrain is therefore bare of trees, and real capture overwrites it the first
time a player actually visits (InstallForeignBlob precedence, section 10.5, needs no
new bookkeeping for this).

A peek's callback sometimes never fires (TopoHorizon: raced ring chunks near the
sent-radius edge). A 300s timeout sweep gives up on those, counts them, and never
retries - a command-driven run must terminate. The same timeout is the only detector
for a worldgen handler that throws inside the engine's own thread.

### Verified

On this build, over the console: a radius-12 run into terrain 225-of-289 existing
loaded 49 columns, skipped 176 as frontier, peeked 400, built 212 sections, and left
`mapchunk`/`chunk`/`mapregion` at exactly 225/1800/16 rows before and after. The
matrix scenario `nondestructive` re-proves the strict form on every run: an all-peek
run far from spawn must leave the three terrain tables byte-identical - row key sets
and content hash, not counts.

### The promise is measured at runtime, not assumed

The matrix tier tests against vanilla worldgen on one machine. Every run therefore
ends by re-probing up to 256 sampled positions that did not exist before it, and the
finish line carries the result: "Verified 256/256 sampled absent positions still
absent". A regrown position logs a warning naming worldgen mods as the likely cause.
The sweep ends the same way, which also watches whether the measured SafeNeighbourhood
of 4 holds under modded worldgen - nothing checked that before. Positions within a
player's view radius are excluded from the sample: the engine generates terrain around
players as normal play, and counting that against the promise would train admins to
ignore the warning.

### What the mod writes

The full list, kept short deliberately:

| Surface | When | Notes |
|---|---|---|
| `ModData/vintagehorizons/<world>.db` | continuously | the client LOD cache |
| `ModData/vintagehorizons/<world>-server.db` | capture on | the server LOD cache |
| `ModConfig/vintagehorizons.json` | on settings change | client settings |
| `ModConfig/vintagehorizons-server.json` | on clean load only | never overwritten when it fails to parse |

The savegame is reached only through engine APIs, never as a file. One honest caveat:
a sweep or a generation run LOADS existing columns, and the game ticks a loaded column
exactly as when a player walks past - snow settles, grass grows. That is vanilla
simulation, not mod data, but the savegame's content can change because of it. This is
why the sweep's scenario asserts row counts while only generation's all-peek scenario
can assert byte equality.

## 15. What a peek loses, measured

Section 14 chose the Terrain pass and stated the cost as "no trees". That came from
reading the `EnumWorldGenPass` doc comments, which is the same method that
under-reported the sweep's neighbour dependency by three rings. `/vhgen diff` measures
it instead: peek a 5x5 block of columns, generate the same coordinates for real, and
diff the centre column block by block. The border exists because passes above Terrain
need neighbours, so a lone load would under-report exactly the content being measured.

Measured on one column of a 1.22.6 world (chunk 14687,14687, seed as generated by
`scripts/test-server.sh`):

| | Peek at Terrain | Full generation |
|---|---|---|
| Blocks | 128,832 | 129,762 |
| Distinct block types | 11 | 78 |

**67 block types exist only in the full generation. None exist only in the peek.**
That second half is the more useful claim, and it is stronger than the one section 14
made: a peek never invents anything. Generated LOD can be incomplete, never wrong.

What the 67 are, grouped:

| Category | Examples from the run | Pass |
|---|---|---|
| Snow | `snowlayer-1` x1024 - one per exposed position | 4 |
| Cave dressing | `crackedrock-*` x3,737, `glowworms-*`, `stalagsection-*` | 2 |
| Ores | `ore-quartz`, `ore-lapislazuli`, `ore-borax`, `ore-low-emerald`, `saltpeter-*` | 2 |
| A ruin | `agedstonebricks`, `chest-east`, `crate`, `clutteredbookshelf`, `planks-veryaged-*`, `torchholder-aged-empty-east`, `statictranslocator-broken-west` | 2 |
| Surface scatter | `looseboulders-*`, `loosestones-*`, `gravel-andesite`, `sand-andesite` | 2 |
| Small water | `water-n-6`, `water-n-7`, `water-ne-7`, `water-nw-7` | 2 |

Trees do not appear in this table because this particular column has none. The
vegetation pass is still absent, and a forested column would add `log-*` and
`leaves-*` to the list.

**Surface height: median +1, all 1024 positions differ, range -28..+1.** The ground
does not move. The +1 is the snow layer sitting on top of every exposed block, and the
-28 is the ruin excavating into the terrain. This is the detector for the assumption
the whole feature rests on - that the Terrain pass alone sets the ground - so the
scenario asserts a median of 0 or 1 and would go red if the terrain itself shifted.

### A peek cannot see an edit

`/vhgen edittest` is the evidence behind `LodColumnMap.Classify` never peeking a column
that exists. It places a marker, reads it back from the loaded world, then peeks the
same coordinate and looks for it. The read-back is not redundant: without it, an absent
marker in the peek could equally mean the placement failed.

Measured: `game:glass-plain` x4 placed at 512016,135,512016. The loaded world returns
`game:glass-plain` at that position. The peek of the same coordinate returns air.

That is `PeekChunkColumn`'s documented contract holding in practice - it generates
"from scratch without keeping it in the list of loaded chunks", so a peek and the
savegame are two independent generations rather than two views of one. Peeking a column
that exists would therefore cache the terrain as it was before anyone built there,
which is what the Peek/Load/SkipFrontier rule prevents.

**This command writes blocks**, so it is registered only when
`VINTAGEHORIZONS_DEVTOOLS=1`. Without that variable it does not exist, and the write
contract in section 14 holds exactly as written. It is the one deliberate exception,
and it is opt-in, admin-only, and reports the position it touched.
