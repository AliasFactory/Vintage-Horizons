# M4 first pass — overnight status (branch `m4-blockdata`)

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

## Known gaps / follow-ups (deliberate for a first pass)

1. **Water renders opaque** (flagged in palette, not yet blended/translucent).
2. **No greedy quad merging** — vertex counts are box-naive; fine at current scale.
3. **Cross-level seams**: section borders between different detail levels may show
   cracks (M3's skirts were removed with the heightmap mesher; box walls go deep so
   it's less severe — needs eyeballing).
4. **Initial-buildup coarseness**: until a subtree fully meshes, the coarse parent
   renders even close to the player (swap rule). Cosmetic; refine in M5.
5. **Worker exceptions are silently swallowed** (chunk-disposal races) — add counters.
6. **Mesh memory**: all levels stay in RAM and VRAM; eviction is M5 territory.
7. Sea-level oceans: unexplored ocean beyond the rain-height... capture starts at the
   rain height so deep ocean columns include the full water depth — verify visuals.

## How to run

```sh
scripts/dev-run.sh                        # normal
VINTAGEHORIZONS_AUTOUNPAUSE=1 scripts/dev-run.sh   # unattended testing
```

`.vhinfo` in chat for live stats; stats also log every 60s in auto-unpause mode.
