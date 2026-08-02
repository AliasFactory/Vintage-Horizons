# Changelog

This file changes when a version is released, not when a commit is made. For the
procedure, read [docs/RELEASING.md](docs/RELEASING.md). The newest version is first.

## [Unreleased]

### Optional server-side assist

The mod is now Universal, and `requiredOnClient` and `requiredOnServer` are both false.
If you install it on your client only, it operates as before, on any server. A vanilla
server is included.

If you install it on the server also, the server builds its own LOD cache from the travels
of all players. Then it gives sections to each client that asks for them. As a result, a
new player or a new area can be far already. Before, a client showed only the terrain that
the same player explored.

An admin has these controls in `ModConfig/vintagehorizons-server.json`:

- capture on or off
- serving on or off
- a serve radius for each player
- two rate limits

The command `/vhserver` reports the status. Serving is on by default, with a radius of
8192 blocks for each player.

### Savegame sweeping, on by default

A server now loads the terrain that the world generated in earlier sessions. A dedicated
server does this, and the integrated server of a singleplayer world does it also. Then the
mod can build the cache from that terrain immediately. Before, the mod used only the
terrain that a player went past again.

The sweep generates no new terrain.

### Pre-generation, separate and off by default

`PregenRadiusChunks` generates a square of chunk columns around the spawn point. This
terrain is terrain that nobody visited. Thus a server can offer a horizon at the first
join, instead of a horizon that appears after weeks of play.

This is the only setting that makes the mod create terrain. For that reason it is off
until an admin asks for it. The radius also has a limit of 256 chunks, which is a radius
of 4096 blocks. The cost is worldgen time and disk space. At radius 64 the disk cost is
approximately a few hundred MB.

### Faster terrain fill-in

Meshing now runs on a thread pool. Before, one thread did capture and meshing together,
and capture prevented the mesher from running. Fill-in is now 2 to 3.5 times faster at the
same load.

### Fixed

- LOD regions stayed coarse permanently, with a hard edge between detailed terrain and
  blocky terrain. The check for "does the server hold this section" gave a wrong result for
  ancestor keys.
- LOD color was sometimes wrong for a block whose first texture has no baked color. This
  was confirmed on the vanilla block `fruitingbush-wild-blackberry`. A player reported it
  on a world with other mods.
- Each new installation recorded a message that said the mod discarded your cache. The
  cache was empty, and the mod discarded nothing.
- A singleplayer world ran two copies of the full pipeline in one process.
- Remote terrain now arrives nearest-first. Before, the order was arbitrary.

### Internal

The correctness of the mod now depends on a repeatable check suite, `scripts/check.sh`.
Before, it depended on sandbox runs that a person started by hand.

## [0.1.1] - 2026-07-25

Version 0.1.0 had no LICENSE file in the zip. A review found three defects after the
release:

- ground-cover mats that floated above mip-merged runs
- thin plants that hid the water behind them
- a scheduler loop with no limit, which can make one frame do a six-figure scan

## [0.1.0] - 2026-07-25

The first release. Unlimited render distance, which the vanilla view-distance slider does
not control. Real 3D terrain, not a heightmap: mountains, overhangs, cave mouths, forests
and player buildings all appear at a distance. Translucent water over the lake floors and
sea floors. Live seasonal color and a snow line. A cache for each world, which continues
to increase as you play. Fully client-side, and it works on any server.
