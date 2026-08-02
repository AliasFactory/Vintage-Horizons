# VintageHorizons - Design

This is a Distant Horizons-style LOD mod for Vintage Story that increases the render
distance. It is **fully client-side**. It operates on any server, vanilla or modded,
because it builds its LODs only from the chunk data that the client receives.

The supporting research is in `docs/research/`. Each document points at file paths.

- [`distant-horizons-architecture.md`](docs/research/distant-horizons-architecture.md) is
  the established design. It is LGPL-3.0, thus this project uses the concepts only and
  writes its own code.
- [`voxy-architecture.md`](docs/research/voxy-architecture.md) is the recent and fast
  design. All rights are reserved, thus this project uses **ideas only and copies no
  code**.
- [`vintage-story-api.md`](docs/research/vintage-story-api.md) gives all that the Vintage
  Story 1.22.x client API supplies, with citations.

## 1. Why this is possible

Nobody made this mod for Vintage Story before. These four facts make it possible.

A code mod with `"side": "Client"` can join a server that has no mods. Mod verification in
Vintage Story goes in one direction only. A server can have requirements for a client, but
a client has no requirements for a server.

For each loaded chunk column, the client receives the full block data, the `RainHeightMap`,
the `WorldGenTerrainHeightMap` and `YMax`. The chunks are 32x32x32 and the RAM holds them
with palette compression. The event `capi.Event.ChunkDirty` occurs when a chunk arrives or
changes, with the reason `NewlyLoaded` or `MarkedDirty`.

The other Vintage Story LOD mods (Farseer, ChunkLOD) make their LOD data on the
**server**. Thus they cover the full map immediately. The cost is that an admin must
install them on the server. This project accepts the trade that Distant Horizons makes
instead. Coverage increases as the player explores, and a cache on disk keeps it for each
server. As a result the mod operates everywhere.

The community gave one objection to a Distant Horizons port. In Vintage Story the terrain
changes with the seasons and the snow, thus a cached LOD becomes incorrect. The solution
is to keep the appearance out of the geometry. The mod stores the identity of each block
and finds the color at render time. The shader applies the tint for the season and the
snow. Read section 6. The geometry changes only when the blocks change, and the mod finds
that change in the same way as Distant Horizons.

## 2. Constraints

| Constraint | Consequence |
|---|---|
| VS 1.22.3, .NET 10, C# | This is a new implementation. The Java references give concepts only. |
| OpenGL **3.3 baseline** (macOS maximum is 4.1) | The core renderer uses VAOs and one draw for each section, as Distant Horizons does. The compute and MDI pipeline of Voxy is an *optional* fast path for GL 4.3 and later, which the mod selects at runtime. |
| A VS chunk is **32x32x32**. World height is configurable, 256 by default and up to 16k. | The arithmetic for sections and columns uses 32 as its base unit. The y fields hold values up to 16k. |
| On the client, `TopRockIdMap`, `SnowAccum` and `MapRegion` are null | The mod must read the surface material from the block data, with the heightmaps as a guide. |
| There is no chunk-unload event on the client | This has no effect. The mod makes a snapshot when a chunk arrives, and the cache continues after the chunk. |
| The Voxy license reserves all rights | Concepts only. Distant Horizons is LGPL, thus concepts only also. The user decided on a clean-room implementation with a permissive license. |
| Farseer is **MIT** | Its client renderer and shader are legally reusable with attribution. They give camera-relative rendering, the ZFar extension and GLSL that matches the fog. This is the start of the renderer. |

## 3. Architecture overview

```
capi.Event.ChunkDirty (NewlyLoaded / MarkedDirty)
  - snapshot chunk (Unpack_ReadOnly -> block ids, heightmaps; never touch live state after)
      - bounded player-centered priority queue, hash-gated (persisted per-chunk content hash)
          - ChunkToLod: 32x32 column scan -> palette + vertical-RLE columns (section 4)
              - LodStore: merge into leaf section, persist (SQLite), set ApplyToParent flag
                  - MipPropagator: DB-flag-driven child-to-parent downsample (crash-safe, section 5)
                  - dirty listeners -> QuadTree.queueReload(sectionKey)
                      - RenderSectionBuilder (worker): section + neighbor edge strips
                          -> resolve palette -> colors/tint-classes -> greedy quads -> vertex buffer
                          - render-thread task queue (frame-budgeted) -> GL upload
                              - LodRenderer (IRenderer @ Opaque, order 0.36):
                                 quadtree walk, frustum cull, near-to-far opaque / far-to-near water,
                                 seasonal tint uniforms, fog-matched shader, dithered near fade
```

The design takes three things from three projects.

From **Distant Horizons** it takes the pipeline shape: column RLE, a mip pyramid on disk, a
quadtree, and crash-safe dirty flags.

From **Voxy** it takes the ideas for encoding and scheduling. Those are packed single-word
keys and voxel ids, an early exit from mip propagation, one weighted worker pool, and the
serialization of the palette.

From **Farseer** it takes the Vintage Story rendering methods: the render order, ZFar, and
the shader for fog and curvature.

## 4. Data model

A **section** holds 64 x 64 *data columns* at each detail level. In Vintage Story a leaf
section is 2 x 2 chunk columns. At detail level D, each column covers 2^D x 2^D blocks.

A **section key** is one packed `long`: `detail(6) | x(29) | z(29)`, signed. One key
identifies a section in the cache, the database, the quadtree and the render map. This is
the "one identifier everywhere" idea of Voxy.

**Column data** is a vertical RLE, from the top down, with no gaps. The mod stores air runs
also, thus the light and the downsampling stay correct. This is a rule of Distant Horizons.
One run is one packed `ulong`:

```
palette id (20 bits) | yTop (14 bits) | yBottom (14 bits) | skyLight (4) | blockLight (4) | flags (8)
```

A 14-bit y value supports a world 16k blocks high. The flags hold space for the material
class in section 6 and for the water and lava markers. Air is palette id 0, thus a test
against zero finds it. This is a trick of Voxy.

A **palette** maps an id in one section to a Vintage Story block code, which is a
`domain:path` string. The mod serializes the palette with the section blob, as Distant
Horizons does. Thus a section is self-contained, and there is no global registry that can
become corrupt. The mod merges and remaps palettes when it merges sections, and it compacts
a palette when the palette becomes large.

**Chunk to leaf conversion** reads each of the 32 x 32 columns from the top down, from
`max(RainHeightMap[i], YMax)`. It writes a new run when the block code changes. It reads
the raw palette and data arrays through `IChunkBlocks`, not the accessors for each block.
Voxy showed that this difference decides between free ingestion and a frame-time problem.

**Downsampling** merges 2 x 2 columns into 1. The mod collects the y boundaries, sweeps the
slices, and takes the most common palette id for each slice. When there is a tie, it takes
the most opaque id. Then it averages the light and makes the RLE again. If a merge at one
level changed nothing, the mod stops and does not continue up the pyramid. This early exit
comes from Voxy.

## 5. Storage

The mod uses **SQLite** in WAL mode, with one database for each server and world. The path
is `VintagestoryData/ModData/vintagehorizons/<serverAddress>/<worldId>.db`. The `worldId`
comes from the world identity that the client can see. This is the seed and dimension when
they are available, which is a hash trick of Voxy. If they are not available, it is the
server address and the world name.

There are three tables. `Sections(detail, x, z, blob, palette, applyToParent, timestamps)`
has the primary key `(detail,x,z)`. `ChunkHash(cx, cy, cz, hash)` holds the hashes. `Meta`
holds the format version.

A blob holds a plane of palette indexes and a run list. The mod compresses it with ZSTD-1
or LZ4. A benchmark decides which one, because .NET has a good library for both.

The rows hold the **`applyToParent` dirty flags**, as Distant Horizons does. The mip
propagator reads `WHERE applyToParent=1 ORDER BY dist(player) LIMIT n`. Thus the pyramid
stays consistent after a crash, and no dependency tracking in memory is necessary.

**Chunk hash gating** comes from Distant Horizons. The mod makes a content hash for each
chunk from sparse samples, and stores it in the same transaction as its section. When the
same chunk arrives again, the cost is one hash comparison.

A write-merging cache holds the chunk updates for each section in memory. The mod writes
them after approximately 1 second with no new data. Chunks that arrive near each other
almost always go into the same section.

## 6. Rendering

### The MVP path (GL 3.3, operates everywhere)

The mod registers an `IRenderer` at `EnumRenderStage.Opaque` with `RenderOrder = 0.36`.
This is immediately before the real terrain, thus the real terrain hides the LOD terrain by
depth. Farseer proved this value.

The mod extends ZFar with `ClientMain.MainCamera.ZFar` and `Reset3DProjection()`. These are
internals of VintagestoryLib. This is standard practice, and Farseer does it also.

A **quadtree** of render sections has the player at its center. The expected detail is
log(distance). **A parent draws until all 4 children have uploaded their buffers.** This is
the no-holes rule of Distant Horizons. A buffer swap is atomic, and the root ring never
draws.

Workers build the meshes. A worker loads a section and the 4 **edge strips** of its
neighbours. The mod stores these column strips beside each section, which is a trick of
Distant Horizons that prevents the deserialization of a full neighbour. The worker writes
the visible faces of each run box, culled against the vertical neighbours and the adjacent
columns. Then it does a greedy merge for each face direction.

The vertex format is compact, at approximately 16 bytes for each vertex. It holds an int16
position relative to the section, an RGBA color, a normal index, the light and a tint-class
index.

GL uploads occur on the render thread only, through a task queue with a frame budget of
approximately half a frame. This budget comes from Distant Horizons. The model matrices are
relative to the camera, through `CameraMatrixOriginf`, for precision.

The shader is GLSL 330. It uses `#include` for `fogandlight.vsh` and `vertexwarp.vsh` from
the game. Thus the fog, the shadow and the curvature of the globe are the same as vanilla.
This is the approach of Farseer, which is MIT.

At the **seam with the real terrain**, the mod skips each LOD section that is fully inside
the approved view distance. It then fades the boundary ring with a Bayer-dithered discard,
as Distant Horizons does. It can also move the innermost LOD ring down a small number of
blocks, as Farseer does, to hide a difference in the silhouette.

### Seasons and snow

This is the problem that is specific to Vintage Story. The shader solves it.

The mod puts each palette entry into a **tint class** one time. The classes are `grass`,
`foliage-deciduous`, `foliage-conifer`, `water`, `ice`, `rock`, `soil`, `sand`, `manmade`
and `snow`.

The vertex color holds the *base* color of the block. The tint class is an index into a
small uniform array of the *current* seasonal multipliers. The mod calculates that array in
each frame from `capi.World.Calendar` and the climate. Thus the color of the full LOD world
changes continuously with the seasons, and **no vertex buffer changes**.

For the snow cover, the mod calculates the current snow line from the calendar and the
climate. The fragment shader then makes the up-facing fragments above that line more white.
This is an approximation, but at LOD distances a player cannot see the difference. It also
changes each day, as the real snow does.

A real change of a block, such as a player build or a tree that falls, arrives through
`ChunkDirty(MarkedDirty)`. Then a hash comparison occurs, and the normal update path
follows.

### The fast path

This path is optional and comes later. The mod finds GL 4.3 at runtime before it uses this
path. Voxy gave the ideas: packed quads of 8 bytes with vertex pulling, buckets for each
face direction, indirect multi-draw, and Hi-Z occlusion. This path is never necessary,
because the GL 3.3 path is complete.

## 7. Threading

There is **one worker pool** with n = cores/1.5 threads. A weighted random selection
schedules the services, proportional to `pending x weight`. This idea comes from Voxy. The
order of the weights is: ingest, then save, then mesh, then mip-propagate. C# expresses
this with a semaphore and dedicated threads. The pool balances itself, and no service needs
its own tuning.

All queues are **bounded and centered on the player**. The mod takes the nearest item and
removes the farthest, as Distant Horizons does. Thus the system loses work instead of
falling behind.

There is a backpressure valve at each stage boundary. The save queue has a soft limit, and
then the caller does the work. The mesh queue removes duplicates by section key. The upload
has a budget for each frame.

GL work occurs on the render thread only, through the task queue with the budget.

## 8. Milestones

- **M0, skeleton.** A client-only ModSystem, a log of the ChunkDirty subscription, a csproj
  that builds, and a launch configuration. *This was done in the first commit.*
- **M1, first pixels.** An in-memory heightmap LOD from the chunks that arrive, drawn as a
  colored heightmap mesh past the normal view distance. The visuals are the class of
  Farseer, but the client builds them. This proves the full loop: ingest, then build, then
  draw with an extended ZFar.
- **M2, persistence.** The SQLite store, keys for each server and world, chunk-hash gating,
  and a cache that reloads at join. Now a horizon continues between sessions.
- **M3, LOD pyramid.** The full column-RLE model, the mip levels with crash-safe
  propagation, and the quadtree detail selection with the parent-until-children rule.
- **M4, true 3D LODs.** Run-box meshing with neighbour culling and a greedy merge. This
  gives overhangs, cliffs and caves that are visible from outside. The visuals are the class
  of Distant Horizons.
- **M5, polish.** Seasonal tint classes with a snow line, the water surface, a config GUI,
  in-chat commands, and the ModDB release.
- **M6, fast path.** This is optional. A GL 4.3 renderer with vertex pulling and MDI, behind
  a runtime test of the capability.
- **M7, optional server assist.** The same mod, which an admin can install on a server, to
  give a client terrain that the player never visited. Read section 10.

## 9. Licensing

VintageHorizons is **MIT**.

Distant Horizons (LGPL), Voxy (all rights reserved) and Algernon's Terrain Sampler (no
LICENSE, thus all rights reserved) give concepts only. This project copies no code from any
of them. The clones in `reference/` are in `.gitignore`, and this project never
redistributes them.

This project can adapt Farseer code, which is MIT, with attribution. Each part that it
adapts gets a credit in the README and in the source.

## 10. Optional server assist (M7)

### 10.1 The problem that it solves

The client-only design has one weakness. It is the one thing that Farseer, ChunkLOD and
TopoHorizon do better. This mod can draw only the terrain that the server sent to it. A new
world shows nothing past the vanilla view distance until the player travels, and the sides
of a flight path stay empty.

Those mods make the LOD data on the server. They pay for this with `requiredOnClient`. Thus
a server with one of those mods makes each player install it, and a client with one cannot
join a server without it. The result is all or nothing, in both directions.

The assist removes the weakness of this mod, and it does not accept the weakness of those
mods. It **operates on each server, and it is better on a server that opts in**.

### 10.2 The constraint above all others

The client must never need the server side. If an installation on a server makes joining
players install the mod, this project has made Farseer again. Then the only reason for this
project is gone.

Vintage Story supports what is necessary here. The documentation of
`ModInfo.RequiredOnClient` says:

> If set to false and the mod is universal, clients don't need the mod to join.

Thus there is one mod, and this project ships it one time:

```json
"side": "Universal",
"requiredOnClient": false,
"requiredOnServer": false
```

Both flags are necessary, in opposite directions:

| installed on | result |
| --- | --- |
| client only | The behaviour of today, with no change, on any vanilla server |
| server only | The server gives data. A client without the mod is unaffected and still joins. |
| both | The channel connects. The mod fills in terrain that the player did not visit. |
| neither | Not applicable |

`requiredOnServer: false` keeps a client with the mod able to join a vanilla server. Without
that flag, the problem moves to the other side instead of going away.

One mod is better than a companion download, because a companion removes a compatibility
matrix that becomes incorrect. There is no pair of client 0.1.1 with server 0.2.0 to think
about. There is one version number and one zip for both groups of users.

### 10.3 Architecture: a third implementation of a seam that exists

The section source is already pluggable. The async path from the storage work has the
shape that a network source needs. Ask by key. Get the answer later. Install on the main
thread.

- `LodWorld.LoadFromStore` is a `Func<long, LodSection?>`. It is synchronous and reads the
  local disk.
- `LodWorld.RequestAsyncLoad` is an `Action<long>`. The results arrive through
  `InstallLoaded`.
- `LodStore.Serialize` and `LodStore.Deserialize` already make a self-contained deflated
  `byte[]`.

That last point is more important than it looks. **The stored blob is the wire format.**
There is no second serialization to design. A section that goes through the network is
identical, byte for byte, to a section from the disk.

Thus the change on the client is small. When the channel is connected, a key that the local
disk does not have goes to the network instead of returning nothing.

### 10.4 What the simple description hides

Three parts are real work, and none of them is the transport.

**The server has no LOD database to give.** It must build one. It runs the same capture over
the chunks that it holds, and it keeps that data current as the world changes.
`LodWorker.Capture` reads an `IWorldChunk`, and `LodStore` needs only an `ILogger`. Thus
both move to the server without a change. The coordinator around them is the client
`ModSystem`, which does not.

**The server cannot give a color to a palette.** `RegisterPaletteEntry` calls
`Block.GetColorWithoutTint(ICoreClientAPI, BlockPos)`, which ends in
`capi.BlockTextureAtlas.GetAverageColor`. A dedicated server has no texture atlas. Thus the
one field that it cannot fill is the field that each palette entry needs.

For that reason a section travels with **no color**, and the client adds the color when it
receives the section. This is a smaller change than it appears. `ResolvePendingPalette`
already runs on the client for each section that comes off the disk. It already finds the
block ids again from the codes, and it already holds the block. It gains a pass for the
color.

**No schema change is necessary.** The first plan was to store an "unresolved" marker in the
blob. That plan needed a larger `LodStore.SchemaVersion`, and a larger version empties the
cache. Each existing player loses the horizons that they explored, for a function that is
not yet on.

This is unnecessary, because the *transport* knows where a section came from. A section that
arrives over the channel gets the flag in memory before the install. A section from the local
disk never needs the flag. A row on the server holds color 0, only the server reads it, and
the server never draws.

**The client cannot ask for what it does not know about.** `HasDataSet` drives the descent
of the quadtree, and `LoadAllKeys` fills it at join by reading the local database. Against a
remote source the client has no key set. Thus it cannot descend into a remote area, and it
cannot know that a request is useful. For that reason the handshake carries a **key
manifest**. The manifest holds keys only, exactly what `LoadAllKeys` gives, and no blobs.

### 10.5 Precedence

When both sources hold a section, the **local capture wins**. The server fills gaps only.

The capture of the client is what the client observed, and it includes the player edits that
the client saw. The copy on the server can be an older snapshot. If the server overwrites,
old terrain replaces the newer ground below the player.

### 10.6 Revealing the map

Terrain that a player never visited is a survey of the world. It shows coastlines,
structures and the bases of other players. The competing mods have the same property. That
is not a reason to release this without thought. Some admins think that this is cheating,
and their opinion is correct for their server.

An admin must be able to configure this, and the default must be conservative. There are
three controls:

- a radius limit on the distance from a player at which the assist gives data
- a rule to use already-generated chunks only, and never to start worldgen for a request.
  This rule is also what makes the server-side mods expensive.
- a switch that stops the function fully

### 10.7 Transport

**The limit of 508 bytes does not apply here.** This was measured against the source, not
assumed. The warning is on the two `RegisterUdpChannel` overloads only, and never on
`RegisterChannel`. It is about NAT fragmentation of datagrams. The reliable channel has no
such limit. A section is still tens to hundreds of KB. Thus the mod divides it into parts, for
latency and for peak memory. No limit makes this necessary.

**The server limits the rate and the quantity of requests.** A client must not be able to
ask for an unlimited area. The server decides what it gives, not the client.

**The handshake carries a protocol version.** Clients of version 0.1.1 exist already. A
client must ignore what it does not understand. It must not parse it incorrectly.

### 10.8 Code layout

The mod is Universal, thus the assembly loads on a server for the first time. The code
divides by side. It does not use a branch inside one system.

- `VintageHorizonsModSystem` has `ShouldLoad(side) => side == Client`, with no change.
- A new server system has `ShouldLoad(side) => side == Server`.

The client system casts `capi.World` to `ClientMain`, compiles shaders and registers a
renderer. None of that can run on a server. The strong guarantee is that the code never runs
there. A branch is weaker, because one refactor can make it incorrect.

### 10.9 Staging

1. ~~Handshake only~~ **done.** The channel connects and the two sides exchange versions.
   Then `.vhinfo` reports whether it found a server with the assist. Section 10.11 gives
   what this stage proved.

2. ~~Server-side capture~~ **done.** This moved before the manifest, because a manifest
   lists what the server holds, and the server holds nothing until it captures.
   `LodPipeline` and `LodBlockPolicy` are the coordinator, extracted and independent of the
   side. `LodServerCaptureSystem` drives them from `ChunkColumnLoaded` and from the
   block-edit events. The edit events are necessary, because a live column never fires
   `ChunkColumnLoaded` again.

   Measured: a dedicated server built 85 sections with a complete pyramid. The counts by
   detail level 0 to 6 were 51, 19, 8, 4, 1, 1 and 1. After a clean stop there were 0
   unflushed mip flags and 0 errors on both sides.

3. ~~Key manifest~~ **done.** The mod sends the full manifest at the handshake. It does not
   use a spatial query. A real world of 5581 sections is 44 KB at 8 bytes for each key.
   Thus the manifest is not the expensive part. The sections are, at a mean of 45.9 KB each.
   The median is 44.2 KB, p95 is 86.4 KB and the maximum is 154.5 KB.

   Stage 4 must plan for that size. To "send what the client does not have" for that world
   is 262 MB.

   Measured at volume: 5665 keys in 3 parts, with an exact announced count and 0 errors. The
   welcome message and the manifest come from one snapshot on the main thread. Before this, an answer from the
   message handler read a set that the capture tick changes. Thus the announced count did
   not agree with what followed.

4. ~~Section transfer~~ **done.** The client asks only for a key that the manifest offered
   and that it has no local data for. The server answers with the stored blob, without a
   change, because the wire format *is* the storage format.

   The client installs each arrival and **stores it in its own cache**. Thus the server
   fills the cache of the client, and it does not stream to it. At the measured mean of
   45.9 KB for a section, a fetch in each session was never possible. A player who leaves
   the server also keeps what that player took.

   Measured end to end: 96 requested, 96 received, 96 installed, 0 declined and 0 errors.
   Afterward the client held 225 sections, of which it captured 129 and the server gave 96,
   with a complete pyramid and no unflushed mip flags.

   There are three limits. The two that matter are on the server, because a modified client
   ignores its own limits. `MaxSectionsInFlight` is 16, and it is a courtesy of the client.
   `MaxSectionsPerSecondPerPlayer` is 8, which is approximately 370 KB/s at the measured
   mean. `MaxSectionsPerSecondTotal` is 32.

   The last limit protects the server. Fairness for each player does not limit the *sum*.
   Each section that the server gives is a SQLite blob read on the main thread. Thus twenty
   players at 8 each second make 160 reads each second of the tick time. The server
   gives sections round-robin from a start point that rotates, thus no player can take the
   full budget. A queue of 256 keys for each player drops the excess instead of growing.

   Three defects are worth memory. All three have the same shape: a key stops in a state
   that nothing clears, thus the terrain froze instead of becoming coarse.

   - The in-flight limit held keys back. The mod removed them from the pending set, but they
     stayed in `LodWorld.LoadsInFlight`, where the render scheduler skips them. Thus they
     stopped for the session, and the transfer moved *nothing*. The mod can forget only the
     keys that it sent.
   - The mod removed a declined key from its own in-flight set, but not from `LoadsInFlight`.
     Thus the parent of that key stayed coarse forever.
   - **`HasDataSet` cannot answer the question "can the local disk supply this?"**
     `RegisterInTree` walks *up*. Thus the registration of any L0 key marks its L1 to L6
     ancestors as holders of data. A test of the manifest keys against that set therefore
     skipped coarse keys that the server really held. The first of a node or its descendants
     to be enumerated decided the result for the other one.

     Those keys stayed out of `RemoteOnly`. They went to a store with no such row, they
     returned null, and they arrived in `LoadFailed`, which is permanent. The symptom was a
     region with a hard edge, drawn at L5 at any distance, with an idle pipeline. The mod now
     tracks the key set of the store separately, as `localKeys`, and tests the manifest
     against that set.

   Three wrong answers came from the counters before the third defect was found. `.vhwhy`
   settled it in one attempt. It prints the four children of each coarse node with their real
   state: `no-data`, `not-resident`, `loading`, `load-failed`, `empty`, `meshing`, `no-mesh!`
   or `ok`. Instrument the decision. Do not infer it.

5. ~~Admin config and defaults~~ **done.** The mod writes
   `ModConfig/vintagehorizons-server.json` at the first start. Thus an admin finds the
   options without a read of the source. The options are `EnableCapture`, `EnableServing`,
   `ServeRadiusBlocks` with a default of 8192, and the two rate limits. The mod clamps each
   value at load. Thus a file that a person edited cannot stop the server.

   `/vhserver` reports the settings in force and what the server gave.

   Serving is **on** by default. This is a deliberate difference from "conservative
   defaults". The installation of the mod on a server *is* the decision to opt in. A mod that
   does nothing until a person edits a file appears to be broken. The conservatism is in the
   radius instead. An admin who wants no sharing sets `EnableServing` to false. An admin who
   wants some sharing gets a limited quantity, not the full world.

   The mod tests the radius when it is ready to send a section, against the position of the
   player at *that* time. It does not use the position from the time of the request. A
   request that waited must not be honoured for a place that the player left.

   The distance is to the nearest edge, not from center to center. An L6 section covers 4096
   blocks. Thus a center distance refuses a section that the player is inside.

   Measured on the same world, with an empty client cache each time. At the default of 8192:
   87 requested, 87 received, 87 installed and 0 declined. At radius 512: 92 requested, 13
   received and 79 declined as out of radius, and the client remembered the refusals instead
   of asking again. With `EnableServing: false` the client reported "this server has a LOD
   cache but is not sharing it" and fetched nothing. There were 0 errors on both sides
   throughout.

Singleplayer was **excluded** at this stage. The earlier claim that singleplayer was the
largest early gain was incorrect.

The integrated server does load the server side, and the channel does connect in the same
process. Read section 10.11. But the chunks that load drive the capture, and in one process
the server loads exactly the chunks that the client already shows. Thus a second pipeline
duplicates the cache file, the work and the memory, for no gain.

This was found in a live session: two "LOD cache:" lines named one database, and a manifest
held 3851 keys that the host already had. Server capture then required
`api.Server.IsDedicated`, and the server cache took a `-server` filename suffix. Thus the
collision cannot occur again without a message.

NOTE: Section 13 replaces part of this exclusion. A sweep of the savegame is the genuine
singleplayer gain that this section over-claimed, and it is now built.

Real worldgen on demand is not in scope. It is the expensive half of what the server-side
mods do. The mod stays cheap enough for an admin to leave on because it does not do this.
Section 10.10 gives a cheaper way to reach the same terrain.

### 10.10 Predicted terrain (Algernon's Terrain Sampler)

`algernonsterrainsampler` implements the noise pipeline of GenTerra again. Thus a caller can
ask for the surface height, climate, rainfall and forest density at (x, z), for a chunk that
nobody generated or loaded. The fast path of Farseer uses it, through reflection on
`TerrainSamplerMod.GetBlockColumnHeight` and `SampleColumn`.

This makes the gap in section 10.9 smaller. The cost that section 10.9 refused is the
*generation of chunks*, and a sample of noise is not that. A server that also has the sampler
can answer for land that nobody visited. That is the last thing that the competitors do and
this mod does not.

The sampler stays optional, and below capture. The three reasons are not incidental.

**A sample is not a capture.** It gives a height and a climate. Thus the mod must
*synthesize* a column, and it must infer the surface block from the temperature and the
rainfall. It cannot replay the blocks that are there. There are no structures, no player
edits and no accurate choice of block. That result is exactly the appearance that this mod
is better than today. It belongs in a third tier, below the server capture, which is already
below the local capture in section 10.5. Real data for the same key must overwrite it
immediately.

**It makes each client download it.** Its `modinfo.json` declares `RequiredOnClient: true`,
even though `ShouldLoad` limits it to the server. Thus a server that installs it makes
*each* player fetch it, and this includes a player who does not run VintageHorizons. An
add-on for admins that has a cost for uninvolved players is a real cost. The documentation
must give it clearly, not in a footnote.

**It is only as correct as the worldgen that it models.** Its accuracy decreases with
terrain-generation mods. It gives the work to Watersheds when Watersheds is present.
Otherwise it predicts the landscape before the rivers. Terrain that is incorrect and
confident is worse than terrain that is absent. Thus the switch in section 10.6 covers this
also.

Licensing: the repository has no LICENSE, thus all rights are reserved. The integration uses
reflection only. This is the documented path, and it needs no assembly reference and no
copied code. The rule is the same as the rule for Voxy in section 9: read it to understand
it, and copy nothing.

Prediction on the client is not possible. This is recorded so that nobody derives it again.
`IWorldAccessor.Seed` has the documentation "Accessible on the server and the client", which
makes the idea look possible. But `AssetCategory.worldgen` is `EnumAppSide.Server`. Thus a
client never loads the landform definitions or the geologic-province definitions that shape
the noise. The worldgen of a modded server is not knowable from the client at all.

### 10.11 What stage 1 measured

There were three installations, on the sandbox client and the dedicated server, with
`scripts/test-*.sh`:

| client | server | result |
| --- | --- | --- |
| no mod | mod | Joins normally, with no rejection for missing mods |
| mod | mod | The hello and welcome messages complete, with 0 errors |
| mod | no mod | 0 errors. Capture and rendering do not change. |
| singleplayer, both sides in one process | | `LodAssistServerSystem` starts. The handshake completes in the process, with 0 errors. |

The second row is the cheap one. The first row is the constraint in section 10.2. The third
row is where the design was incorrect.

**Do not use `GetChannelState` as the test.** Against a vanilla server it returned
`Connected` for a channel that was not connected. Then `SendPacket` threw *"Attempting to
send data to a not connected channel"*. This happened inside a `LevelFinalize` handler, and
the engine stops that handler at an exception. Thus an optional extra stopped the remainder
of the startup of this mod.

Use `IClientNetworkChannel.Connected` as the guard, because the error message of the engine
names it. Keep the handshake at the end of the handler, inside a `try`. An optional function
must never come before the work that it is optional to.

**There is one cosmetic cost on a vanilla server.** The client records *"Client registered 1
network channels (vintagehorizons) the server does not know about"* at startup. This cannot
be prevented for an optional channel, because the registration must come before the
connection handshake. Thus there is no point at which the mod can know to skip it. It is a
log line, not a dialog.

CAUTION: Do not release `side: Universal` before stage 3. It changes the category of the mod
on ModDB, and the filters that find it, but it gives a player nothing yet. Make this change
in the release that gives terrain to a client.

## 11. Known issues

**The LOD color is incorrect for a block whose block-color texture did not resolve.** A
player who runs Conquest Reforged and Better Ruins reported this. The blocks are correct
near the player and incorrect at LOD distance.

This is partly corrected. `DescribePalette` now refuses an atlas sub-id that it cannot use.
An id is unusable when it is out of range, or when its `Positions` entry has no assignment.
`DescribePalette` also refuses the average of `unknown.png`. It then uses any other baked
texture that the block owns. The result is cached for each block id.

The root cause is in vanilla, not in those mods.
`Block.LoadTextureSubIdForBlockColor` tries the attribute `textureCodeForBlockColor`, then
`"up"`, then `Textures.First()`. That last step ends in `?? 0`. Thus a block whose first
texture in dictionary order has no `Baked` entry resolves to atlas sub-id 0, with no message.
The other faces bake correctly. That is the reason why the block looks correct near the
player and incorrect at LOD distance only. This was confirmed on the vanilla block
`game:fruitingbush-wild-blackberry-free`.

**This is not confirmed to be the reported symptom.** The player described a *purple* color.
`unknown.png` measures near-white. It is 32 x 32, mostly white with a small red mark, and its
average is 255,249,249. This agrees with the value `00FCFCFC` that the atlas reports. Thus a
magenta block comes from somewhere else. The most probable cause is `GetAverageColor` on a
sub-id that the atlas never assigned, which the guard for range and null now also catches.

To close this issue, ask the reporter for three things:

- the block code
- whether the block looks correct near the player
- any "texture not found" lines in `client-main.log`

## 12. The test regimen

A person established each correctness claim above by hand. That person ran a sandbox, read
the `.vhinfo` counters, and wrote the result into a commit message. Nobody can repeat those
runs without a person to drive them. Thus nothing examined those claims again.

`scripts/check.sh` is the permanent version. It has three tiers. It runs the cheapest tier
first, and it stops at the first failure.

**There can be no CI.** A build needs the Vintage Story assemblies from a local game
installation, and Anego Studios does not permit redistribution of them. Thus no hosted runner
can compile this repository. The regimen is local because it must be, not because this is
better.

### 12.1 Tiers

| tier | time | proves |
| --- | --- | --- |
| `fast` | ~1 s | Pure logic and the rules that span files, with no game process |
| `smoke` | ~5 min | The pipeline operates end to end, cold and then warm |
| `matrix` | ~20 min | The installation combinations and each admin control |

`fast` is a plain console harness in `tests/VintageHorizons.Checks`, which `dotnet run`
starts. It uses no test framework. This repository has no NuGet dependencies, and none are in
the local cache. Thus a framework prevents the fast tier from running without a network.

The fast tier is also sequential. This is not a limitation to remove.
`LodWorld.DetailDistance` is a mutable static that more than one check sets. Thus sequential
is the only correct order.

Assembly loading was the one thing that can stop this tier. The mod references the game DLLs
with `Private=false`, thus they never go into the release zip. This also keeps them out of
its `deps.json`. Nothing puts them on the TPA list, and references do not pass through a
`ProjectReference`.

The test csproj states the references again without that flag. `GameAssemblies` installs an
`AssemblyLoadContext.Default.Resolving` handler from a `[ModuleInitializer]`, which reads the
installation directly. `ProbeChecks` runs first, and it does nothing except force the two
loads that can genuinely fail. `Block` has a vtable of approximately 200 methods, which
reaches furthest. `LodStore` pulls in `Microsoft.Data.Sqlite` by its existence.

### 12.2 What the fast tier found immediately

**The guard on the shader constant cannot catch what it exists to catch.**
`LodTerrainRenderer` compared `LodTintRegistry.MaxSlots` against
`LodTintRegistry.GlslTintSlots`. Both are C# constants in the same file.

`GlslTintSlots` was a copy, maintained by hand, of a number that is in `lodterrain.vsh` and
`lodterrain.fsh`. Thus an edit to a shader that forgot the copy left the guard passing. Water
then decoded as opaque, and thin plants decoded as water, with no compile error.

The compiler said this clearly. That comparison raised CS0162, unreachable code, because both
sides were compile-time constants with the same value.

The copy and the dead guard are now gone, and three sources of truth are two. The static
suite reads the shader files, which is the only thing that can close this hole. It also
catches a disagreement between the `.vsh` and the `.fsh`, which nothing did before.

The suite also tests that `MaxSlots * 3 <= 256`. The mesher packs three tint bands into a
byte alpha. Thus a `MaxSlots` value above 85 moves the thin band into the opaque band, with
no message.

**A new installation announced that it discarded data that it never held.**
`PurgeOutdatedData` compared the stored `FormatVersion` against the schema version. A new
database has no such row. Thus `null != "6"`, and each first run recorded *"LOD cache
semantics changed; discarding old cached data"*.

The smoke tier found this at its first execution, because the assertion "this line must not
appear" has no condition. The message now depends on a version that was present.

### 12.3 Changes for testability

There are four changes, and each one is as small as the work permitted.
`LodServerPregen.SpiralAt` and `LodAssistServerSystem.WithinServeRadius` became public. The
second one keeps a private overload that reads the player position. Thus the case of a
mid-join player with no position keeps its own answer. The three `LodAssistClient` packet
handlers became `internal`, with `InternalsVisibleTo`.

The fourth change is `LodRemoteKeySet`, extracted from `LodPipeline`. That set logic is pure.
It needs only a `LodWorld`, which has no constructor and no API field. But it was behind a
constructor that takes an `ICoreAPI` and starts five threads, thus no test can reach it. It
holds the difference between `localKeys` and `HasDataSet`, which is the most expensive defect
in the history of this project. It now has a regression test that fails if the defect returns.

### 12.4 A check that never failed is not a check

Each new assertion was confirmed to fail. The method was to change the code that it guards,
see the failure, and then undo the change.

| change | caught by |
| --- | --- |
| `TINT_SLOTS` from 64 to 32 in the `.vsh` | 1 assertion |
| a non-ASCII byte in a shader | the ASCII scan |
| `localKeys` to `HasDataSet` in `AddRemoteKeys` | 7 assertions |
| the thin-mat offset measured down from the top | 3 assertions |
| the mip majority from 2 to 1 | 1 assertion |

The `localKeys` change is the one that matters. Its first three failures are exactly the
symptom that took three wrong diagnoses the first time.

### 12.5 The serve radius, verified

A person measured the radius limit one time and never watched it. It is the control for the
map-revealing problem. Sections come from wherever the players went together. Thus without
the limit a new player can take a survey of the full explored world without travel. It was
the one admin-facing setting with no verification at all.

The trap is that **a count of declines alone proves nothing**. A section that is resident in
RAM but not yet written to disk is also declined, and an uncapped run produced 55 of them.
Terrain that is absent at a distance looks the same, whether the server refused it or never
held it.

Thus the check gives the *same pre-generated cache two times*, and the limit is the only
difference:

| | offered | installed | declined |
| --- | --- | --- | --- |
| uncapped (radius 0) | 806 | 633 | 55 |
| capped (radius 512) | 446 | 274 | 415 |

The manifest held 861 keys both times. The limit decreased the delivered sections by more
than half, and it increased the refusals by a factor of ten. The control run had the same
data available. Thus the radius is the only possible cause of that difference.

A second independent run gave a close result: 302 installed and 386 declined with the limit,
against 629 and 46 without it. Thus the effect is the control, not one fortunate sample.

**The visual half does not work.** The honest action is to say so. The check captures a pair
of screenshots from one position, with the limit and without it. The images are not
repeatable between runs. There were three attempts, with the same route and the same
settings:

- At y=420 the camera was inside the cloud layer. The result was two frames of white fog.
- At y=260 with a settle time of 75 s, the capped frame showed near terrain and a group of
  flat coarse plates. The uncapped frame showed continuous terrain. This looks like the
  effect of the limit. But the capped client had 348 sections resident and only 20 meshed at
  the moment of the screenshot. Thus meshing progress caused most of the difference.
- At y=260 with a settle time of 180 s, the capped frame was **empty**, with no terrain at
  all. The uncapped frame showed a full landscape. A capped run cannot legitimately draw less
  at 180 s than at 75 s. Thus something other than the radius decides the picture.

The counters over the same three runs were close together: 274, 302 and 302 installed with
the limit, against 633, 629 and 634 without it. Thus the limit is verified and the camera is
not.

Two things prevent a usable image, and neither one is cheap to correct. The atmospheric fog
of the game hides the detail well before 512 blocks, at any position that is also above the
cloud layer. And what the client *fetched* is not what it *drew*, because meshing, eviction
and the descent of the quadtree are between the two.

**Thus the screenshot step gives information only.** It asserts that the run produced two
captures. It never asserts what is in them. Do not make a conclusion from the pair without a
read of the counters beside it.

### 12.6 What tier 3 got wrong first

Each scenario failure on the first full run was in the harness, not in the mod. That is its
own lesson. A person who trusts the output reads each one as a defect in the product.

- **`no-client-mod` waited for a log line that does not exist.** There is no "Loaded Game" in
  a Vintage Story client log. The marker must also prove that the join *completed*.
  "Connected to server" appears during the handshake, thus it also appears on a run that the
  server is about to reject. The block registry arrives only after acceptance.
- **`deferral` can never defer.** Farseer is `requiredOnServer`. Thus against the vanilla
  server that the scenario used, the client disabled it and `IsModEnabled` returned false.
  There was nothing to defer to. An installation on both sides is also the real-world shape.
- **`deferral` waited for "Level finalized"**, which the deferring path returns before it
  reaches.
- **The scenarios were not standalone.** `no-client-mod` used a server that an earlier
  scenario left running. Thus `--only no-client-mod` got "connection refused". Each scenario
  now starts its own server.
- **The visual position was inside the cloud layer.** y=420 drew two identical frames of
  white fog. A height of 260 with a pitch of -20 is the same position as the
  existing `high-overlook` waypoint. That waypoint draws terrain well past the ring
  distance.
- **The retry budget for the server cannot outlast TIME_WAIT.** `test-server.sh` retried a
  busy port four times at 10 s, and Linux holds TIME_WAIT for approximately 60 s. This was
  survivable while each restart had a long client run before it. The uncapped control added a
  second restart immediately after the first, and then the bind failed permanently. The
  budget is now six attempts at 15 s.

## 13. Savegame sweeping

The LOD cache held only the terrain that streamed past a player who runs this mod. The
savegame holds each column that anyone generated. On a test world that was 12,632 generated
chunk columns against 620 captured sections. A world that people played for weeks gives a
larger difference. All of that data is on the disk already, and somebody paid for it already.

A sweep loads those columns, and then the capture sees them. It is the cheap half of
pre-generation, and it is the half that is correct to have on by default. Pre-generation
*creates* terrain that nobody visited. It costs worldgen time and disk space, and it reveals
places where no player went. A sweep creates nothing and reveals nothing new.

### 13.1 Keeping the promise to generate nothing

`LoadChunkColumnPriority` generates a column that is absent. Thus the gate must be exact.
`TestMapChunkExists` is the supported check, and its documentation says that it does not load
chunk data. The guarantee comes from the API contract, not from an assumption about the save
format.

A gate on the target column alone is not sufficient. This part needed measurement.

Worldgen runs in passes. A column reaches `Done` only after its neighbours reach an earlier
pass, and that needs *their* neighbours. Thus a load of a column near the frontier of the
explored terrain makes the engine generate what is absent beside it.

One world was swept at each neighbourhood width. The table gives the chunk columns that the
savegame gained:

| neighbourhood that must be intact | columns generated |
| --- | --- |
| none (target only) | 1460 |
| radius 1 (3x3) | 714 |
| radius 2 (5x5) | 509 |
| radius 4 (9x9) | **0** |

Radius 3 was not tested. Thus radius 4 can be one step wider than necessary. A value that is
too wide is the correct direction for an error. A value that is too narrow breaks the only
promise that this function makes, and it gives no message. A value that is too wide costs a
slightly thicker border of terrain that the mod does not capture.

The cause was identified by a sweep of a radius that was fully *inside* the generated
terrain. All 4,225 of 4,225 positions existed, the sweep loaded all of them, and the savegame
gained exactly zero columns. Thus the load is not the mechanism, and the frontier is.

The neighbour state must be known before any load. Thus the sweep has two passes. First it probes
each position. The probe reaches one neighbourhood past the load area. Thus no edge column
is skipped for a lack of information. Then it loads only the columns that have an
intact surround.

### 13.2 How swept terrain reaches a singleplayer client

Only the server side can ask for a column that the player is far from. Only the client side
has a texture atlas. Each half holds exactly what the other half does not have.

Thus a swept section travels the same road as a section from a real server. The mod captures
it with each palette color at 0 and writes it to the `-server` cache. Then the client reads
it, gives it a color again from the block codes at install, and stores it in its own cache.

`LodRemoteKeySet` does not care whether a blob arrived over a socket or from the disk beside
it. That is the reason why the client half is a reader, `LodLocalOfferSource`, and not a
subsystem.

Verified end to end on a singleplayer world of 8,766 generated columns. The server side swept
5,847 of them. The client finished with 670 sections resident, and it captured only 309
columns itself. The row counts of the savegame did not change. A sample of the palette
entries showed the server cache at **0% colored** against the client cache at **100%
colored**. This is the recolor path in operation, because swept geometry starts with no color
at all.

Adoption depends on demand. The client took 698 of the 2,158 sections that the sweep
produced, because the render path feeds `RemoteWanted()` with what it wants to draw.

Thus the singleplayer guard in `LodServerCaptureSystem` has a condition, and it is not
absolute. Two sides in one process is still of no use for normal play, because the server
loads exactly the chunks that the client shows. But a sweep deliberately loads columns that
the client never shows. That is the one thing that the server side can do there, and the
client cannot do it for itself.
