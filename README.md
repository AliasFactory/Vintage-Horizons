# Vintage Horizons

Distant Horizons-style extended render distance for [Vintage Story](https://www.vintagestory.at/).
It works on any server, and that server needs nothing installed.

Other VS LOD mods (Farseer, ChunkLOD) must be installed on the server. This one does not.
It builds a persistent level-of-detail cache from the chunk data your client already
receives as you play. It then draws that cache far past the normal view distance.
Coverage grows as you explore, and it persists across sessions.

You can also install it on your server. Then it shares its own cache with players and an
admin can build the horizon on request. Neither is necessary, and players without the mod
are unaffected.

## What it does

- **Unlimited render distance**, decoupled from the vanilla view-distance slider.
- **Real 3D terrain**, not a heightmap: mountains, overhangs, cave mouths, forests, and
  anything you build all appear at distance, at 1-block resolution near the player.
- **Translucent water**, drawn over the lake and sea floors beneath it.
- **Live seasonal colour**. Grass and foliage follow the game's own climate and season
  maps. The mod derives a snow line from the local temperature lapse rate. So the far
  terrain changes with the seasons instead of freezing at capture time.
- **Persistent per-world cache** that keeps growing as you play, with join time and
  memory use independent of how much you have explored.

## What it cannot do

A client-side mod knows only the terrain the server sent it. Land you have never been
near never reached your client. It is not in the cache, so the mod cannot draw it. A new
world therefore shows nothing past the vanilla view distance until you travel.

Server-side generators (Farseer, ChunkLOD) ask the world generator directly and do not
have this limit. The trade is that they must be installed on the server.

Here, the edge of the explored area fades into the horizon instead of ending in a cliff.
The picture fills in the more you play. If you run the server, `/vhgen` fills it in
ahead of time.

## In-game commands

| Command | Purpose |
| --- | --- |
| `.vhinfo` | Status: cached/resident sections, meshes, current far edge, settings |
| `.vhdetail [blocks]` | Distance before detail starts to halve (default 512). A higher value gives sharper far terrain and costs more VRAM and CPU. Try 1024. No argument reports the current value. |
| `.vhfar <blocks>` | Cap the LOD render distance. `0` means unlimited, which is the default. |

Two server commands exist as well (`/`, not `.`). They need the controlserver
privilege, which every singleplayer host has:

| Command | Purpose |
| --- | --- |
| `/vhserver` | Server assist status: settings in force, cache size, transfer counters |
| `/vhgen start [radius] [x z]` | Build the LOD cache around you (or around `x z`), generating terrain nobody has visited. Also `stop` and `status`. See below. |

Both settings persist in `VintagestoryData/ModConfig/vintagehorizons.json`.
The per-world cache lives in `VintagestoryData/ModData/vintagehorizons/<savegame-id>.db`.
The mod discards that cache when an update changes what the stored data means. A stale
cache can therefore never degrade a newer version.

## Building

Requires the .NET 10 SDK and a Vintage Story 1.22.5 or 1.22.6 install. Those are the two
versions the check suite runs on. The mod declares 1.22.5 as its minimum, so it will not
load on anything older.

```sh
export VINTAGE_STORY="$HOME/Games/vintagestory1.22.5"   # your game path
dotnet build VintageHorizons
```

The build assembles a loadable mod folder at `VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons`.
`scripts/package.sh` produces a ModDB-ready zip in `dist/`.

```sh
scripts/dev-run.sh              # opens/creates the "vhsurvival" test world
scripts/dev-run.sh myworld      # a different world
```

### Savegame sweeping

A savegame holds every chunk column anyone has ever generated. The LOD cache saw only the
part that streamed past a player who ran this mod. Sweeping loads those columns so capture
sees them. You get a horizon over everywhere you have already been, without flying back
over it.

Sweeping is on by default, in singleplayer too. The settings are `SweepSavegame`,
`SweepRadiusChunks` and `SweepColumnsPerSecond`, in
`ModConfig/vintagehorizons-server.json`.

It is safe to default on because it **generates nothing**. The sweep skips a position
that nobody has visited. It also skips a border around the explored terrain, because a
load there makes the engine generate the missing neighbours.

`PregenRadiusChunks` takes the opposite trade. It creates terrain nobody has visited, so
it stays off unless an admin asks for it. That setting now uses the same transient
generation `/vhgen` uses, so it too writes nothing to the savegame.

### Generating the horizon (/vhgen)

Sweeping and capture cover only terrain that exists. `/vhgen start [radius] [x z]` covers
the rest. It builds the LOD picture around you, or around coordinates you give, for land
nobody has visited. It writes **nothing** to the savegame.

The engine's `PeekChunkColumn` runs real worldgen from the seed and returns the column
transiently. The mod captures it and throws the terrain away. A column that already
exists loads normally instead, so player builds stay correct.

Every run then re-probes a sample of the positions it generated. It reports the result on
its finish line, as "Verified 256/256 sampled absent positions still absent". The check
regimen also asserts the savegame promise byte for byte, but only against vanilla
worldgen. The runtime check is what watches it on a modded server.

**Generated terrain is bare.** It carries the landform, the rock strata, caves, rivers,
and the soil and sand on top. Nothing else. Measured against a full generation of the same
column, a peek is missing 67 block types: snow, ore, cave dressing, surface boulders,
small water, worldgen ruins with their contents, and trees in a forested column.

What a peek does produce is never wrong. Nothing appears in a peek that a real generation
does not also make. So generated terrain reads as a correct but plain version of itself,
and real capture fills in the rest the first time a player visits. `/vhgen diff` prints
the measurement for your own world.

It stops there because the pass that adds trees crashes vanilla worldgen when it runs
this way. The fix is a patch this mod does not ship.

A client that is already connected learns about new sections at its next join, not live.

The command needs the controlserver privilege. Five settings bound it in the server
config: `EnableGenerateCommand`, `GenerateMaxRadiusChunks` (the ceiling, default 128),
`GenerateDefaultRadiusChunks`, `GenerateColumnsPerSecond` and `GenerateMaxInFlight`.

### Running the checks

```sh
scripts/check.sh              # all three tiers, in order (~25 min)
scripts/check.sh fast         # pure logic and static assets, no game (~30 s)
scripts/check.sh smoke        # one end-to-end sandbox run (~5 min)
scripts/check.sh matrix       # install combinations and admin controls (~20 min)
```

Run `fast` constantly. It needs no game process and finishes in under a second once built.
Run the whole thing before you commit.

The three tiers answer different questions.

`fast` covers the pure logic: key packing, the RLE column store, mip downsampling, the
mesher's greedy merge and coverage rules, the blob format, frustum planes and config
clamps. It also covers the invariants that span files, which no compiler can catch. One
example is the shader's `TINT_SLOTS` matching `LodTintRegistry.MaxSlots`.

`smoke` starts a vanilla dedicated server and a sandboxed client, then asserts on what the
run logged. It includes a second pass against the warm cache. That pass is the only way to
know that what was written can be read back.

`matrix` covers the configurations other people put the mod in: a vanilla server, a modded
one, a client without the mod at all, each admin switch, and deferral to a competing LOD
mod.

**There is no CI, and there cannot be.** Building this repo requires the Vintage Story
assemblies from a local game install, and those are not redistributable, so no hosted
runner can compile it. `scripts/check.sh` is the entire safety net.

### Testing without touching your own game

Test instances run in a `.testdata` sandbox and must be started and stopped only through
these scripts:

```sh
scripts/test-server.sh                              # vanilla dedicated server, port 42425
scripts/test-client.sh -c localhost:42425           # sandboxed client
scripts/test-stop.sh [client|server|all]            # stop via pidfiles
```

CAUTION: Start and stop test instances only with these scripts. The VS client allows one
instance, through a named pipe in `$TMPDIR`. A launch with `-c` **forwards its connect
request into whatever instance is already running**, including your own game, and then
exits without a message.

`--dataPath` does not protect you. A private `TMPDIR` does, and these scripts set one up.
They also record the child PID, and they make sure that `/proc/<pid>/cmdline` names the
sandbox before they signal anything. Without that step, a stale pidfile and a reused PID
together kill an unrelated process.

Useful env knobs for unattended runs: `VINTAGEHORIZONS_AUTOUNPAUSE=1` (keep ticking without
window focus), `VINTAGEHORIZONS_AUTOEXPLORE=1` and `VINTAGEHORIZONS_EXPLORE_HOP=<blocks>`
(teleport along a spiral so fresh chunks keep streaming).

### Development notes

- **Shaders must be pure ASCII** (even comments). The engine's OpenTK marshaling
  truncates the GL source by the difference between UTF-8 bytes and char count,
  which silently cuts the end off the shader.
- Worlds created via `-o` default to the `creativebuilding` playstyle, which is
  **superflat** - the dev script passes the `preset-surviveandbuild` lang code for real
  terrain.
- Singleplayer pauses while the game window is unfocused, which stops game ticks - and
  with them LOD ingestion and cache saves.
- The game's block registry must not be read off the main thread (`GetBlock(int)` lazily
  mutates a dictionary). Sections deserialized on the storage thread keep their palette
  block *codes* and have ids resolved at install time on the main thread.

See [DESIGN.md](DESIGN.md) for the architecture. See [docs/STATUS.md](docs/STATUS.md) for
current status, measurements, and known gaps. See [CHANGELOG.md](CHANGELOG.md) for what
shipped in each version, and [docs/RELEASING.md](docs/RELEASING.md) for the release
procedure.

## Credits

- [Distant Horizons](https://gitlab.com/distant-horizons-team/distant-horizons) and
  Voxy (Minecraft) - architectural inspiration. No code is used from either.
- [Farseer](https://github.com/ViciousBadger/VSMod-Farseer) (MIT, © Badgerson) -
  Vintage Story rendering techniques. Adapted code is credited where it is used.

## License

[MIT](LICENSE)
