# M4 and M5 status notes

## Performance work (2026-07-24)

There were three changes. Each one was measured, not assumed.

**Frustum culling.** The quadtree walk selects sections in all directions. Thus sections
behind the camera still made draw calls. The planes come from the same projection and view
matrices that the LOD shader gets, and the test occurs at draw time only.

The test cannot occur in the selection walk. A walk that culls removes the meshes that are
off the screen. Then it builds them again when the player turns around.

The part of the sections that the mod culls is between 12% and 59%. The value depends on
the quantity of captured data around the camera.

The frustum suite in `scripts/check.sh fast` covers the plane arithmetic. It rejects
sections that are behind, left, right, above, or past the far plane. It accepts sections
that are in view. The suite builds the matrices with `Mat4f` from the game. Thus it tests
the column-major assumption of the extraction.

**Writes are no longer on the render thread.** A save batch used 10 ms to 22 ms on
average, and 49 ms at the peak, on the main thread. One tick is 50 ms. The cost came from
deflate of 100 KB to 300 KB in the same call.

Now the main thread copies each section. The copy is necessary, because the live section
continues to change, and `SetColumn` edits `Runs` in place. Then a storage thread
compresses the copy and writes it, with the deflate outside the database lock. After the
change, an equal batch used 0.3 ms on average and approximately 3 ms at the peak. This is
approximately 32 times faster.

**Reads are no longer on the render thread.** At join there were 302 loads in the same
call, at 4.3 ms on average and 29.5 ms at the peak. Now there are 47, and the background
serves 600.

Capture and mip propagation stay synchronous on purpose. They must merge into the stored
data before they make a section. When they did not, stored rows were hidden and then
overwritten.

Two hazards were found and corrected during this work:

- The storage thread must not read the block registry, because `GetBlock(int)` changes a
  dictionary. Thus a load on that thread keeps the palette **codes**, and the main thread
  finds the ids at install time.
- The mod must remember a reload that returns nothing. Without this, the walk requests
  that key again in each frame, forever.

The counters are not the only verification. A restart loaded 958 sections again, with no
unreadable rows. New blobs were decompressed, and they had 0 empty block codes at levels
L0, L1 and L2. A failure to find an id writes empty codes.

One known cost remains. The capture path and the propagation path still do 47 loads in the
same call for each join. That is 8 ms on average, and 33 ms at the peak. The next step is
to make the propagation defer these loads safely.

## Multiplayer verified (2026-07-16, evening)

The primary claim is that a client-side-only installation operates on a server without
mods. This claim now has a test. The test used a vanilla dedicated server
(`scripts/test-server.sh`, a new dataPath, no mods) and the release zip as the only mod of
the client.

The full pipeline operated from the chunks that the server streamed. It captured 3,183
columns and made 311 sections at all 7 levels. Meshing, drawing and persistence all
operated. The cache database for each world uses the SavegameIdentifier as its key, which
operates in multiplayer also. The mod stored 343 sections.

Capture errors increase faster in multiplayer than in singleplayer. There were 50 errors
in approximately 5 minutes. The probable cause is a chunk that the engine removes during a
read, after a teleport. The worker now records the first exception that it caught, and
gives it with the next statistics line.

### Test isolation

CAUTION: Obey these rules. A violation stopped the game of the user one time.

- The Vintage Story client permits one instance only. It uses a global named pipe in
  `$TMPDIR`, `CoreFxPipe_SingleInstanceVintageStoryWithUriScheme`. A launch with
  `-c host:port` sends the connect request into any instance that already runs. The flag
  `--dataPath` does not prevent this. The new process then stops with no message. The
  script `scripts/test-client.sh` prevents this with a private TMPDIR for the sandbox.
- Start a test instance only with `scripts/test-client.sh` or `scripts/test-server.sh`.
  Stop it only with `scripts/test-stop.sh`, which uses pidfiles from `$!`.
- Do not find game processes by their name or their arguments. The user plays at the same
  time.
- Put sandbox mods in `.testdata/Mods` and load them with `--addModPath`. A relative
  `Mods` entry in clientsettings points to the installation directory of the game.

## M5 progress (2026-07-15, morning)

**VRAM eviction and demand-driven meshing.** This was the first M5 item. The mod removes a
mesh that the quadtree did not select for approximately 60 seconds. When the walk needs
that mesh again, it asks for it through the render-dirty queue. Thus the selection walk is
also the load queue. This idea comes from Voxy, but this code does it on the CPU.

There are no holes. A section stops being selected only when its parent draws in its place.
A node that the mod requests again stays behind the parent until its mesh uploads.

These M5 items remain:

- greedy quad merging
- seasonal tint classes with a snow line
- a config GUI with settings that persist
- section eviction from RAM
- preparation for ModDB

# M4 first pass (branch `m4-blockdata`, merged to master)

## What this branch does

This branch replaces the M3 heightmap data model with the Distant Horizons-style pipeline
in DESIGN.md section 4. The plan put this work in M3, but M3 deferred it.

**Block-data capture.** A worker thread reads each chunk column block by block, on the
FluidOrSolid layer, from the rain height down to y=1. The result is vertical RLE runs.
This captures trees, overhangs, caves that are visible from outside, and player edits. The
limits of the worldgen heightmap no longer apply.

**`LodSection`.** Each section holds 64 x 64 columns. At level 0 a column is 2 blocks,
which is finer than the 4 blocks of M3. A run is a packed `ulong` with the fields
`paletteId`, `yTop` and `yBottom`, over a palette for each section. On disk a palette holds
block **codes**, because ids belong to one savegame.

**3D meshing,** on a worker thread. Each run is a box. The mesher makes a top face at an
air gap, and a bottom face below an overhang. It makes a side wall where the runs of the
neighbour column do not cover the span. It finds that wall by interval subtraction. It
culls across sections with snapshots of the neighbours.

Thread safety here is a convention: a run array of a section does not change after
creation, because a write replaces the full array. Thus a snapshot in the worker has no
race.

**Mip pyramid.** The mod merges 2 x 2 columns with a slice sweep on the y boundaries. A
slice is occupied when 2 or more of the 4 sources are occupied, and it takes the most
common block. The ApplyToParent flags are crash-safe, as before.

**Storage v4.** There is a `Section` table. A blob holds the palette (codes, colors and
flags), the run-count plane, the packed runs and the captured bitset, and then deflate
compresses it. The mod discards a v3 cache when it opens the database.

**Auto-unpause for development.** `VINTAGEHORIZONS_AUTOUNPAUSE=1` keeps a singleplayer
world ticking when the window has no focus. The renderer drives it, because tick callbacks
stop during a pause. This made the unattended overnight verification possible.

## Verified overnight, on a real survival world

The full pipeline operates without window focus: capture, then palette remap, then apply,
then mip, then meshing in the worker, then GL upload. In the first 30 seconds it captured
116 columns. It made 24 sections at all 6 levels, and 15 meshes. There were no exceptions
and no GL errors.

The early statistics show "1 drawn". This is the swap rule that prevents holes during the
initial build. The root draws until its subtree has all its meshes. It is not a defect. The
draw histogram for each level in a later statistics line shows the walk go deeper as the
meshes complete.

For the exact telemetry at each step, read the git log of this branch.

## Verified later in the night

**Persistence round-trip for v4.** The mod saved 29 sections with block-code palettes.
After a rejoin it read all of them, with no unreadable rows. The log gave "29 sections from
cache".

**Quadtree draw histogram.** After the cached subtree had all its meshes, the walk went
down to `16 drawn [L0:16]`. This is full leaf detail near the player. The earlier "1 drawn"
was the swap rule during the initial build, as expected.

**Water pass** (commit b764052). Water goes into a separate buffer, which the mod draws
with alpha blending at alpha 168, after the opaque pass. Face culling is phase-aware: a
solid neighbour culls a solid face, but water does not. Thus a lake floor or a sea floor
draws below the translucent water. A person must look at this in the game.

## Known gaps

These gaps are deliberate for a first pass.

1. **There is no greedy quad merging.** Each box makes its own vertices. This is
   acceptable at the current scale.
2. **Seams between levels.** A border between two sections at different detail levels can
   show a crack. M3 had skirts, but they went away with the heightmap mesher. The box walls
   go deep, thus the effect is smaller. A person must look at this.
3. **Coarse terrain during the initial build.** The coarse parent draws even near the
   player until its subtree has all its meshes. This is the swap rule. The effect is
   cosmetic, and M5 improves it.
4. **Mesh memory.** All levels stay in RAM and in VRAM. Eviction is M5 work. The RSS
   during a soak test was approximately 3.8 GB. The game itself uses most of this. Look at
   the trend, not at the absolute value.
5. **Deep oceans.** Capture includes the full water depth from the rain height. A person
   must look at the visual result and the mesh sizes above large bodies of water.
6. **Water draw order.** The mod does not sort the water in the blended pass. This is
   acceptable for one surface. Examine it again if water layers above each other show
   defects.

## How to run

```sh
scripts/check.sh                          # the full regimen, before you commit
scripts/check.sh fast                     # pure logic and static assets only (~30 s)
scripts/dev-run.sh                        # normal
VINTAGEHORIZONS_AUTOUNPAUSE=1 scripts/dev-run.sh   # unattended testing
```

In the chat, `.vhinfo` gives live statistics and `.vhwhy` gives the reason for coarse
terrain. The mod also records statistics every 15 seconds when
`VINTAGEHORIZONS_AUTOUNPAUSE=1` or `VINTAGEHORIZONS_STATS=1` is set. It records them one
time always, 30 seconds after the level finalizes.
