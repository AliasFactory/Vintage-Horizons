# M4/M5 status notes

## M5 progress (2026-07-15 morning)

- **VRAM eviction + demand-driven re-meshing** (first M5 item): meshes the quadtree
  hasn't selected for ~60s are disposed; when the walk wants a missing mesh again it
  re-requests it via the render-dirty queue (the selection walk IS the load queue —
  Voxy's idea, CPU-side). No holes: a section only stops being selected when its
  parent renders instead, and re-requested nodes stay covered by the parent until
  their mesh uploads. Remaining M5 items: greedy quad merging, seasonal tint classes
  + snow line, config GUI/persisted settings, RAM-side section eviction, ModDB prep.

# M4 first pass — overnight status (branch `m4-blockdata`, merged to master)

## What this branch does

Replaces the M3 heightmap data model with the real Distant Horizons-style pipeline
(DESIGN.md §4, originally planned for M3 and deferred):

- **Block-data capture**: chunk columns are scanned block-by-block (FluidOrSolid
  layer) from the rain height down to y=1 on a **worker thread**, producing vertical
  RLE runs. Trees, overhangs, caves-from-outside, and player edits are all captured —
  no more worldgen-heightmap limitations.
- **`LodSection`**: 64×64 columns per section, 2-block columns at level 0 (finer than
  M3's 4-block), packed `ulong` runs (`paletteId | yTop | yBottom`) over a per-section
  palette. Palettes store block **codes** on disk (ids are savegame-local).
- **3D meshing** (worker thread): every run is a box — top faces at air gaps, bottom
  faces under overhangs, side walls where the neighbor column's runs don't cover the
  span (interval subtraction), cross-section culling via neighbor snapshots.
  Thread-safety by convention: section run arrays are immutable once created (writes
  swap whole arrays), so worker snapshots are race-free.
- **Mip pyramid**: 2×2 columns merge via y-boundary slice sweep, majority occupancy
  (≥2 of 4), most-common block per slice. Crash-safe ApplyToParent flags as before.
- **Storage v4**: `Section` table, blob = palette (codes+colors+flags) + run-count
  plane + packed runs + captured bitset, deflated. v3 caches are purged on open.
- **Dev auto-unpause**: `VINTAGEHORIZONS_AUTOUNPAUSE=1` keeps singleplayer ticking
  without window focus (renderer-driven, since tick callbacks stall while paused) —
  this is what made unattended overnight verification possible.

## Verified overnight (unattended, real survival world)

- Full pipeline flows without focus: capture → palette remap → apply → mip →
  worker meshing → GL upload. First 30s: 116 columns captured, 24 sections across
  all 6 levels, 15 meshes, zero exceptions, zero GL errors.
- "1 drawn" in early stats is the no-holes swap rule mid-buildup (root renders until
  its subtree is fully meshed), not a bug — per-level draw histograms in later stats
  lines should show the walk descending as meshes complete.
- (See git log on this branch for exact telemetry at each iteration.)

## Verified later in the night

- **v4 persistence round-trip**: 29 sections saved with block-code palettes reloaded
  cleanly on rejoin ("29 sections from cache", zero unreadable rows).
- **Quadtree draw histogram**: once the cached subtree was fully meshed, the walk
  descended to `16 drawn [L0:16]` — full leaf detail near the player. The earlier
  "1 drawn" was the no-holes swap rule during buildup, as suspected.
- **Water pass added** (commit b764052): water meshes into a separate buffer drawn
  alpha-blended (α=168) after the opaque pass, with phase-aware face culling —
  solid faces only culled by solid neighbors so lake/ocean floors render under
  translucent water. NEEDS EYEBALLING in-game.

## Known gaps / follow-ups (deliberate for a first pass)

1. **No greedy quad merging** — vertex counts are box-naive; fine at current scale.
2. **Cross-level seams**: section borders between different detail levels may show
   cracks (M3's skirts were removed with the heightmap mesher; box walls go deep so
   it's less severe — needs eyeballing).
3. **Initial-buildup coarseness**: until a subtree fully meshes, the coarse parent
   renders even close to the player (swap rule). Cosmetic; refine in M5.
4. **Mesh memory**: all levels stay in RAM and VRAM; eviction is M5 territory.
   Baseline RSS during soak: ~3.8GB (game itself is most of it; watch the trend,
   not the absolute).
5. Deep oceans: capture includes full water depth from the rain height — verify
   visuals and mesh sizes over large water bodies.
6. Water draws unsorted within the blended pass (fine for a single surface;
   revisit if stacked water layers show artifacts).

## How to run

```sh
scripts/dev-run.sh                        # normal
VINTAGEHORIZONS_AUTOUNPAUSE=1 scripts/dev-run.sh   # unattended testing
```

`.vhinfo` in chat for live stats; stats also log every 60s in auto-unpause mode.
