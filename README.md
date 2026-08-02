# Vintage Horizons

Distant Horizons-style extended render distance for [Vintage Story](https://www.vintagestory.at/).
The mod is **fully client-side** and works on any server.

Other Vintage Story LOD mods (Farseer, ChunkLOD) must be installed on the server. Vintage
Horizons does not. It builds a level-of-detail cache from the chunk data that your client
already receives while you play. Then it draws that cache far past the normal view
distance. The coverage increases as you explore, and it stays between sessions.

## What it does

- **Unlimited render distance.** The vanilla view-distance slider does not control it.
- **Real 3D terrain, not a heightmap.** Mountains, overhangs, cave mouths, forests and
  your own buildings all appear at a distance. Resolution is 1 block near the player.
- **Translucent water,** drawn over the lake floors and sea floors below it.
- **Live seasonal color.** Grass and leaves follow the climate map and season map of the
  game. A snow line comes from the local temperature lapse rate. Thus the far terrain
  changes with the seasons. It is not frozen at capture time.
- **A cache for each world,** which continues to increase as you play. Join time and
  memory use do not increase with the quantity of terrain that you explored.

## What it cannot do

A client-side mod knows only the terrain that the server sent to it. The server never sent
land that you did not go near. That land is not in the cache, and the mod cannot draw it.
Thus a new world shows nothing past the vanilla view distance until you travel.

Server-side generators (Farseer, ChunkLOD) do not have this limit, because they can ask the
world generator directly. But you must install them on the server. Vintage Horizons fades
the edges of the explored area into the horizon. It does not leave them as cliffs. The
picture fills in more as you play.

## In-game commands

| Command | Purpose |
| --- | --- |
| `.vhinfo` | Status: cached and resident sections, meshes, current far edge, settings |
| `.vhdetail [blocks]` | Distance before the detail starts to decrease (default 512). A larger value gives sharper far terrain, but it uses more VRAM and CPU. Try 1024. Without an argument, the command reports the current value. |
| `.vhfar <blocks>` | Limit the LOD render distance. `0` is unlimited, and is the default. |

Both settings stay in `VintagestoryData/ModConfig/vintagehorizons.json`.

The cache for each world is at
`VintagestoryData/ModData/vintagehorizons/<savegame-id>.db`. When a mod update changes the
meaning of the stored data, the mod discards the cache automatically. Thus an old cache
cannot decrease the performance of a newer version.

## Building

You must have the .NET 10 SDK and a Vintage Story 1.22.x installation.

```sh
export VINTAGE_STORY="$HOME/Games/vintagestory1.22.5"   # your game path
dotnet build VintageHorizons
```

The build makes a mod folder at `VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons`.
To make a zip file for ModDB in `dist/`, run `scripts/package.sh`.

```sh
scripts/dev-run.sh              # opens or creates the "vhsurvival" test world
scripts/dev-run.sh myworld      # a different world
```

### Savegame sweeping

The savegame of a world holds each chunk column that anyone generated. But the LOD cache
saw only the part that streamed past a player who runs this mod. A sweep loads those
columns, and then the mod captures them. As a result you get a horizon of all the terrain
that you went to before, and you do not fly over it again.

The sweep is on by default, in singleplayer also. The settings are `SweepSavegame`,
`SweepRadiusChunks` and `SweepColumnsPerSecond`, in
`ModConfig/vintagehorizons-server.json`.

It is safe to keep the sweep on, because it **generates no terrain**. The sweep skips each
position that has no terrain. It also skips a border around the explored terrain. If the
sweep loads a column whose neighbours are absent, the engine generates those neighbours.

`PregenRadiusChunks` makes the opposite trade. It creates new terrain on purpose, thus it
stays off until an admin asks for it.

### Running the checks

```sh
scripts/check.sh              # all three tiers, in order (~25 min)
scripts/check.sh fast         # pure logic and static assets, no game (~30 s)
scripts/check.sh smoke        # one end-to-end sandbox run (~5 min)
scripts/check.sh matrix       # install combinations and admin controls (~20 min)
```

Run `fast` frequently. It needs no game process, and after the first build it completes in
less than one second. Run all three tiers before you commit.

Each tier answers a different question.

`fast` covers the pure logic:

- key packing
- the RLE column store
- mip downsampling
- the greedy merge and the coverage rules of the mesher
- the blob format
- frustum planes
- config clamps

It also covers the rules that apply across more than one file, which no compiler can
find. One example is the shader constant `TINT_SLOTS`, which must agree with
`LodTintRegistry.MaxSlots`.

`smoke` starts a vanilla dedicated server and a sandbox client, then examines what the run
recorded in the log. It includes a second run against the warm cache. This second run is
the only way to know that the mod can read back what it wrote.

`matrix` covers the configurations that other people use:

- a vanilla server
- a server that has the mod
- a client with no mod
- each admin switch
- deferral to a different LOD mod

Each scenario starts its own server.

**There is no CI, and there cannot be one.** To build this repository you must have the
Vintage Story assemblies from a local game installation. Anego Studios does not permit
redistribution of those assemblies, thus no hosted runner can compile this code.
`scripts/check.sh` is the only safety net.

### Testing without an effect on your own game

Test instances run in a `.testdata` sandbox. Start them and stop them only with these
scripts:

```sh
scripts/test-server.sh                              # vanilla dedicated server, port 42425
scripts/test-client.sh -c localhost:42425           # sandboxed client
scripts/test-stop.sh [client|server|all]            # stop with the pidfiles
```

CAUTION: Do not start a test client by hand. The Vintage Story client permits only one
instance, through a named pipe in `$TMPDIR`. A launch with `-c` sends its connect request
into the instance that already runs, which can be your own game. Then it stops without a
message. The flag `--dataPath` does not prevent this. A private `TMPDIR` prevents it, and
these scripts make one.

The scripts also record the PID of the child process. Before they send a signal, they
examine `/proc/<pid>/cmdline` to make sure that the PID is the sandbox. Without this
check, an old pidfile and a reused PID together can stop an unrelated process.

These environment variables help with unattended runs:

- `VINTAGEHORIZONS_AUTOUNPAUSE=1` keeps the game ticking when the window has no focus.
- `VINTAGEHORIZONS_AUTOEXPLORE=1` and `VINTAGEHORIZONS_EXPLORE_HOP=<blocks>` teleport the
  player along a spiral, thus new chunks continue to stream.

### Development notes

- **Shaders must contain only ASCII characters,** in the comments also. The OpenTK
  marshaling of the engine cuts the GL source by the difference between the UTF-8 byte
  count and the character count. As a result, the end of the shader is lost, and no error
  is given.
- A world that you create with `-o` uses the `creativebuilding` playstyle, which is
  **superflat**. For real terrain, the development script gives the lang code
  `preset-surviveandbuild`.
- Singleplayer pauses when the game window has no focus. The pause stops the game ticks,
  and thus it also stops LOD capture and cache saves.
- Do not read the block registry of the game on a thread other than the main thread,
  because `GetBlock(int)` changes a dictionary. A section that the storage thread reads
  keeps the block **codes** of its palette. The main thread finds the ids at install time.

For the architecture, read [DESIGN.md](DESIGN.md). For the current status, the
measurements and the known gaps, read [docs/STATUS.md](docs/STATUS.md). For the content of
each version, read [CHANGELOG.md](CHANGELOG.md). For the release procedure, read
[docs/RELEASING.md](docs/RELEASING.md).

## Credits

- [Distant Horizons](https://gitlab.com/distant-horizons-team/distant-horizons) and
  Voxy (Minecraft) gave architectural ideas. This project uses no code from either one.
- [Farseer](https://github.com/ViciousBadger/VSMod-Farseer) (MIT, (c) Badgerson) gave
  Vintage Story rendering methods. Each part that this project adapts has a credit in the
  source.

## License

[MIT](LICENSE)
