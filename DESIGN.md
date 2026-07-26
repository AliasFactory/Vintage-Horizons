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
- **M7 — optional server assist**: same mod, installable server-side, feeding clients
  terrain they have never visited. See §10.

## 9. Licensing

VintageHorizons is **MIT**. DH (LGPL), Voxy (ARR) and Algernon's Terrain Sampler (no
LICENSE shipped, so ARR) inform concepts only — no code is copied from any of them;
`reference/` clones are gitignored and never redistributed. Farseer (MIT) code may be
adapted with attribution (will be credited in README and source headers where used).

## 10. Optional server assist (M7)

### 10.1 The problem it solves

The client-only design has exactly one weakness, and it is the one thing Farseer,
ChunkLOD and TopoHorizon genuinely do better: we can only draw terrain the server has
already sent us. A brand-new world shows nothing past the vanilla view distance until
the player travels, and the flanks of a flight path stay empty.

Those mods solve it by generating LOD server-side — and pay for it by being
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
server — dropping it inverts the problem instead of solving it.

One mod rather than a companion download also removes a compatibility matrix that
would rot: no pairing of client 0.1.1 against server 0.2.0 to reason about, one
version number, one zip for both audiences.

### 10.3 Architecture: a third implementation of an existing seam

The section source is already pluggable, and the async path added in the storage work
is the exact shape a network source needs — request by key, answer arrives later,
install on the main thread:

- `LodWorld.LoadFromStore` — `Func<long, LodSection?>`, synchronous, local disk
- `LodWorld.RequestAsyncLoad` — `Action<long>`, results land via `InstallLoaded`
- `LodStore.Serialize` / `Deserialize` — already a self-contained deflated `byte[]`

That last point matters more than it looks: **the stored blob is the wire format.**
There is no second serialisation to design, and a section that survives a round trip
through the network is byte-identical to one loaded from disk.

So the client change is small: when the channel is connected, a key that misses
locally is asked for over the network instead of returning empty.

### 10.4 What the framing hides

Three parts are real work, and none of them is transport:

**The server has no LOD database to serve.** It has to build one — running the same
capture over chunks it holds and keeping it current as the world changes.
`LodWorker.Capture` reads `IWorldChunk` and `LodStore` needs only an `ILogger`, so both
port as-is; the coordinator around them is the client `ModSystem` and does not.

**The server cannot colour a palette.** `RegisterPaletteEntry` calls
`Block.GetColorWithoutTint(ICoreClientAPI, BlockPos)`, which bottoms out in
`capi.BlockTextureAtlas.GetAverageColor` — a dedicated server has no texture atlas at
all, so the one field it physically cannot fill is the one every palette entry needs.
Sections must therefore travel **colour-unresolved**, with the client filling colour in
on receipt. That is less invasive than it sounds: `ResolvePendingPalette` already runs
client-side on every section that comes off disk, already re-resolves block ids from
codes, and already has the block in hand — it gains a colour pass.

**And it needs no schema change.** The first plan here was to persist an
"unresolved" marker in the blob, which meant bumping `LodStore.SchemaVersion` — and that
version is a cache-wipe: every existing player would lose the horizons they had explored,
to enable something not yet switched on. Unnecessary, because the *transport* knows where
a section came from. A section arriving over the channel gets the flag set in memory
before install; a section off local disk never needs it. Server-side rows hold colour 0
and only the server reads them, and it never renders.

**The client cannot ask for what it does not know exists.** Quadtree descent is driven
by `HasDataSet`, populated at join by `LoadAllKeys` scanning the local DB. Against a
remote source the client has no key set, so it can neither descend into remote areas
nor tell that a request is worth making. The handshake therefore has to carry a **key
manifest** — keys only, exactly what `LoadAllKeys` already yields, no blobs.

### 10.5 Precedence

When both sources hold a section, **local capture wins**; the server fills gaps only.
The client's own capture is what it actually observed, including player edits it
witnessed, whereas the server's copy may be an older snapshot. Letting the server
overwrite would let stale terrain replace fresher ground the player is standing on.

### 10.6 Revealing the map

Sending terrain a player has never visited hands them a survey of the world:
coastlines, structures, other players' bases. The competing mods have the same
property, but that is not a reason to ship it thoughtlessly — some admins will
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
  anyway — for latency and peak memory, not because a limit forces it.
- **Rate limit and bound requests.** A client must not be able to ask for unlimited
  area; the server decides what it is willing to send, not the client.
- **Protocol version in the handshake.** 0.1.1 clients already exist; a client must
  ignore anything it does not understand rather than misparse it.

### 10.8 Code layout

Going Universal means the assembly loads on servers for the first time. Split by side
rather than branching inside one system:

- `VintageHorizonsModSystem` — `ShouldLoad(side) => side == Client` (unchanged)
- a new server system — `ShouldLoad(side) => side == Server`

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
   part — the sections are, at a mean 45.9 KB each (median 44.2, p95 86.4, max 154.5).
   That is what stage 4 has to budget for: "send what the client lacks" for that world
   would be 262 MB. Measured at volume: 5665 keys in 3 chunks, announced count exact,
   0 errors. Welcome and manifest come from one main-thread snapshot, because answering
   from the message handler read a set the capture tick mutates and the announced count
   disagreed with what followed.
4. Section transfer for already-generated chunks, rate limited, radius capped.
5. Admin config and defaults.

Singleplayer is not a special case of this but it is the biggest early payoff:
the integrated server loads the server side and the channel connects in-process
(measured, §10.11), so once stage 2 lands a solo world gets every chunk it has ever
generated without any networking involved.

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
  *synthesised* — the surface block inferred from temperature and rainfall — instead of
  replaying blocks that are actually there. No structures, no player edits, no accurate
  block choice. That is precisely the look we currently beat. It belongs in a third tier
  below server capture, which is already below local capture (§10.5), and must be
  overwritten the moment real data for the same key arrives.
- **It forces a client download.** Its `modinfo.json` declares `RequiredOnClient: true`
  even though `ShouldLoad` restricts it to the server, so a server that installs it
  makes *every* player fetch it — including players not running VintageHorizons. An
  admin add-on that taxes uninvolved players is a real cost to state plainly in the
  docs, not a footnote.
- **It is only as right as the worldgen it models.** Accuracy degrades with terrain-gen
  mods; it delegates to Watersheds when present but otherwise predicts the pre-river
  landscape. Wrong-but-confident terrain is worse than absent terrain, so the off switch
  in §10.6 covers this too.

Licensing: the repository ships no LICENSE, so it is all rights reserved. Integration is
reflection-only — the documented path, needing no assembly reference and no copied code.
Same rule as Voxy (§9): read for understanding, copy nothing.

Client-side prediction is a dead end, recorded so it is not re-derived. `IWorldAccessor.Seed`
is documented "Accessible on the server and the client", which makes it look feasible —
but `AssetCategory.worldgen` is `EnumAppSide.Server`, so a client never loads the landform
or geologic-province definitions the noise is shaped by, and a modded server's worldgen is
not knowable from the client at all.

## 11. Known issues

**Wrong LOD colour for blocks whose block-colour texture did not resolve.** Reported by a
player running Conquest Reforged + Better Ruins: blocks look correct up close but wrong at
LOD distance. Partly fixed — `DescribePalette` now rejects an unusable atlas sub-id (out of
range, or an unassigned `Positions` entry) and the unknown.png average, and falls back to
any other baked texture the block owns, cached per block id.

Root cause is in vanilla, not in the mods: `Block.LoadTextureSubIdForBlockColor` tries the
`textureCodeForBlockColor` attribute, then `"up"`, then `Textures.First()` — and that last
step ends in `?? 0`, so a block whose first texture in dictionary order has no `Baked`
entry silently resolves to atlas sub-id 0. Other faces bake fine, which is exactly why the
block looks right up close and wrong only in LOD. Confirmed firing on vanilla
`game:fruitingbush-wild-blackberry-free`.

**Not confirmed to be the reported symptom.** The player described *purple*, and
`unknown.png` measures near-white (32×32, mostly white with a small red mark, average
255,249,249 — matching the `00FCFCFC` the atlas reports). So a magenta block is coming from
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
data to a not connected channel"* — from inside a `LevelFinalize` handler, which the
engine aborts on exception, so the optional extra took out the rest of the mod's own
startup. Guard on `IClientNetworkChannel.Connected`, which is what the engine's error
message names, and keep the handshake at the end of the handler behind a `try`. An
optional feature must never sit upstream of the work it is optional to.

**One cosmetic cost on vanilla servers.** The client logs
*"Client registered 1 network channels (vintagehorizons) the server does not know about"*
at startup. Unavoidable for an optional channel — registration has to precede the
connection handshake, so there is no point at which we could know to skip it — and it is
a log line, not a dialog.

**Do not ship `side: Universal` before stage 3.** It changes how the mod is categorised
(and filtered for) on ModDB while delivering nothing to a player yet. The switch belongs
in the release that actually serves terrain.
