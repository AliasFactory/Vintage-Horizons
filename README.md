# Vintage Horizons

Distant Horizons-style extended render distance for [Vintage Story](https://www.vintagestory.at/) —
**fully client-side**, works on any server.

Unlike existing VS LOD mods (Farseer, ChunkLOD), Vintage Horizons requires nothing on the
server: it builds a persistent level-of-detail cache from chunk data your client already
receives while you play, and renders that cache far beyond the normal view distance.
Coverage grows as you explore and persists across sessions.

## What you get

- **Unlimited render distance**, decoupled from the vanilla view-distance slider.
- **Real 3D terrain**, not a heightmap: mountains, overhangs, cave mouths, forests, and
  anything you build all appear at distance, at 1-block resolution near the player.
- **Translucent water**, drawn over the lake and sea floors beneath it.
- **Live seasonal colour**: grass and foliage follow the game's own climate and season
  maps, and a snow line is derived from the local temperature lapse rate — so the far
  terrain changes with the seasons instead of being frozen at capture time.
- **Persistent per-world cache** that keeps growing as you play, with join time and
  memory use independent of how much you have explored.

## What it cannot do

A client-side mod only knows the terrain the server has actually sent it. Land you have
never been near has never been streamed to your client, so it is not in the cache and
cannot be drawn — a brand new world shows nothing past the vanilla view distance until
you travel. Server-side generators (Farseer, ChunkLOD) can ask the world generator
directly and do not have this limitation; the trade is that they must be installed on
the server. Here, the edges of explored area are faded into the horizon rather than left
as cliffs, and the picture fills in the more you play.

## In-game commands

| Command | Purpose |
| --- | --- |
| `.vhinfo` | Status: cached/resident sections, meshes, current far edge, settings |
| `.vhdetail [blocks]` | Distance before detail starts halving (default 512). Higher is sharper far terrain for more VRAM and CPU; try 1024. No argument reports the current value. |
| `.vhfar <blocks>` | Cap the LOD render distance; `0` = unlimited (the default) |

Both settings persist in `VintagestoryData/ModConfig/vintagehorizons.json`.
The per-world cache lives in `VintagestoryData/ModData/vintagehorizons/<savegame-id>.db`
and is discarded automatically when a mod update changes what the stored data means, so a
stale cache can never degrade a newer version.

## Building

Requires the .NET 10 SDK and a Vintage Story 1.22.x install.

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

### Testing without touching your own game

Test instances run in a `.testdata` sandbox and must be started and stopped only through
these scripts:

```sh
scripts/test-server.sh                              # vanilla dedicated server, port 42425
scripts/test-client.sh -c localhost:42425           # sandboxed client
scripts/test-stop.sh [client|server|all]            # stop via pidfiles
```

This matters more than it looks. The VS client is single-instance through a named pipe in
`$TMPDIR`, and a launch with `-c` **forwards its connect request into whatever instance is
already running** — including your own game — then exits silently. `--dataPath` does not
protect you; a private `TMPDIR` does, which is what these scripts set up. They also record
the child PID and verify `/proc/<pid>/cmdline` names the sandbox before signalling
anything, because a stale pidfile plus PID reuse otherwise means killing an unrelated
process.

Useful env knobs for unattended runs: `VINTAGEHORIZONS_AUTOUNPAUSE=1` (keep ticking without
window focus), `VINTAGEHORIZONS_AUTOEXPLORE=1` and `VINTAGEHORIZONS_EXPLORE_HOP=<blocks>`
(teleport along a spiral so fresh chunks keep streaming).

### Development notes

- **Shaders must be pure ASCII** (even comments). The engine's OpenTK marshaling
  truncates the GL source by the difference between UTF-8 bytes and char count,
  which silently cuts the end off the shader.
- Worlds created via `-o` default to the `creativebuilding` playstyle, which is
  **superflat** — the dev script passes the `preset-surviveandbuild` lang code for real
  terrain.
- Singleplayer pauses while the game window is unfocused, which stops game ticks — and
  with them LOD ingestion and cache saves.
- The game's block registry must not be read off the main thread (`GetBlock(int)` lazily
  mutates a dictionary). Sections deserialized on the storage thread keep their palette
  block *codes* and have ids resolved at install time on the main thread.

See [DESIGN.md](DESIGN.md) for the architecture and [docs/STATUS.md](docs/STATUS.md) for
current status, measurements, and known gaps.

## Credits

- [Distant Horizons](https://gitlab.com/distant-horizons-team/distant-horizons) and
  Voxy (Minecraft) — architectural inspiration; no code is used from either.
- [Farseer](https://github.com/ViciousBadger/VSMod-Farseer) (MIT, © Badgerson) —
  Vintage Story rendering techniques; adapted code is credited where used.

## License

[MIT](LICENSE)
