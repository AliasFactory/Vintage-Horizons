# Changelog

Written when a version is released, not when a commit lands - see
[docs/RELEASING.md](docs/RELEASING.md). Newest first.

## [Unreleased]

## [0.2.0] - 2026-08-02

**Chunk generation on request** (`/vhgen start [radius] [x z]`). Generates the LOD
picture around a player, or around given coordinates, for terrain nobody has visited -
without writing anything to the savegame. Real worldgen runs transiently from the seed
(the engine's `PeekChunkColumn`); existing columns load normally instead, so player
builds stay correct. Needs the controlserver privilege, which every singleplayer host
has. Bounded by config ceilings and rate caps; generated terrain has no trees until a
real visit replaces it.

**The non-destructive promise is now measured, twice.** Every sweep and every
generation run ends by re-probing sampled positions that did not exist before it, and
prints the result ("Verified 256/256 sampled absent positions still absent") - so a
worldgen mod that breaks the promise is detected on the server where it happens, not
only in this repo's test matrix. The check regimen additionally asserts, byte for
byte, that an all-peek run leaves the savegame's terrain tables identical.

**Fixed: a client could stop receiving terrain for the rest of a session.** A server
dropped queued section requests without answering them, in two places: when its cache
was not open yet, and when a client asked for more than the queue holds. A client marks
a key in flight when it asks and only forgets it when a reply arrives, so a dropped key
was stranded, and sixteen of them filled the in-flight cap and blocked every later
request. The server now refuses out loud in both cases, and `/vhserver` counts the
refusals. This fits an intermittent stall seen in testing, but was never caught with
logging in place, so treat it as a defect fixed rather than a diagnosis confirmed.

**Fixed:** a config file that failed to parse was overwritten with defaults, deleting
every hand-edited setting over one bad comma. Now the file is left untouched, the
error names the line, and defaults apply for the session only. Also: the client now
notices a server-side cache that appears mid-session (a sweep after a slow start, or a
/vhgen run), where before it looked once at join and never again; and the cache-format
purge reports how many sections it discards instead of deleting silently.

**Optional server-side assist.** The mod is now Universal with both
`requiredOnClient`/`requiredOnServer` false: install it only on your client and it works
exactly as before, on any server, vanilla included. Install it on the server too and it
builds its own LOD cache from everyone's travels, then shares it with connecting clients
on request, so a fresh join or a fresh area can already be far instead of only ever
showing what that one player has personally explored. Server admins get
`ModConfig/vintagehorizons-server.json` (capture on/off, serving on/off, a serve radius
per player, rate caps) and a `/vhserver` status command. Serving defaults on at an
8192-block radius per player.

**Savegame sweeping**, on by default. A server (dedicated, or singleplayer's own
integrated server) now loads terrain the world has already generated in past sessions so
the cache can be built from it immediately, instead of only from terrain a player walks
past again. It never generates new terrain to do this.

**Pre-generation**, separate and off by default. `PregenRadiusChunks` builds the cache
around spawn at startup - terrain nobody has visited yet - so a server can offer a
horizon on the first join rather than one that appears over weeks of play. It now runs
the same transient generation `/vhgen` does, so it costs worldgen time but **no disk**:
the terrain is captured and thrown away rather than written to your savegame. Earlier in
this release it loaded columns instead, which cost a few hundred MB at radius 64. It
stays opt-in because it still reveals map nobody has explored.

**Faster terrain fill-in.** Meshing runs on a thread pool instead of one thread doing
capture and meshing in lockstep, so exploring new terrain no longer starves the mesher -
measured 2-3.5x faster fill-in at the same load.

**Fixed:**
- LOD regions could get permanently stuck coarse with a hard, unmoving edge between
  detailed and blocky terrain (a wrong "does the server have this" check misfired on
  ancestor keys).
- LOD colour occasionally resolved to the wrong texture on blocks whose first texture has
  no baked colour (confirmed on vanilla `fruitingbush-wild-blackberry`, reported against a
  modded world).
- A false "discarding your cache" message logged on every brand-new install.
- Singleplayer briefly ran two redundant copies of the whole pipeline in one process.
- Remote terrain now arrives nearest-to-you first instead of in arbitrary order.

**Under the hood:** correctness is now backed by a repeatable check suite
(`scripts/check.sh`) instead of one-off hand-run sandbox sessions.

## [0.1.1] - 2026-07-25

0.1.0 shipped without the LICENSE in the zip and with three defects found by review
afterwards: floating ground-cover mats on mip-merged runs, thin plants occluding water so
shorelines showed through, and an unbounded scheduler loop that could turn one frame into
a six-figure scan.

## [0.1.0] - 2026-07-25

Initial release. Unlimited render distance, decoupled from the vanilla view-distance
slider. Real 3D terrain, not a heightmap: mountains, overhangs, cave mouths, forests, and
player builds all appear at distance. Translucent water over lake and sea floors. Live
seasonal colour and a derived snow line. Persistent per-world cache that keeps growing as
you play. Fully client-side, works on any server.
