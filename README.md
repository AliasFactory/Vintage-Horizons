# Vintage Horizons

Distant Horizons-style extended render distance for [Vintage Story](https://www.vintagestory.at/) —
**fully client-side**, works on any server.

Unlike existing VS LOD mods (Farseer, ChunkLOD), Vintage Horizons requires nothing on the
server: it builds a persistent level-of-detail cache from chunk data your client already
receives while you play, and renders that cache far beyond the normal view distance.
Coverage grows as you explore and persists across sessions.

**Status: early development.** See [DESIGN.md](DESIGN.md) for the architecture and roadmap.

## Building

Requires the .NET 10 SDK and a Vintage Story 1.22.x install.

```sh
export VINTAGE_STORY="$HOME/Games/vintagestory1.22.3"   # your game path
dotnet build VintageHorizons
```

The build assembles a loadable mod folder at `VintageHorizons/bin/Debug/net10.0/Mods/vintagehorizons`.

Run the game with the dev build:

```sh
scripts/dev-run.sh              # opens/creates the "vhsurvival" test world
scripts/dev-run.sh myworld      # a different world
```

Development notes:

- **Shaders must be pure ASCII** (even comments). The engine's OpenTK marshaling
  truncates the GL source by the difference between UTF-8 bytes and char count,
  which silently cuts the end off the shader.
- Worlds created via `-o` default to the `creativebuilding` playstyle, which is
  **superflat** — the dev script passes `surviveandbuild` for real terrain.
- Singleplayer pauses while the game window is unfocused (ESC menu), which stops
  game ticks — and with them LOD ingestion and cache saves.
- In-game commands: `.vhinfo` (status), `.vhfar <blocks>` (cap the LOD render
  distance; `0` = unlimited, the default).
- The per-world LOD cache lives in
  `VintagestoryData/ModData/vintagehorizons/<savegame-id>.db`.

## Credits

- [Distant Horizons](https://gitlab.com/distant-horizons-team/distant-horizons) and
  Voxy (Minecraft) — architectural inspiration; no code is used from either.
- [Farseer](https://github.com/ViciousBadger/VSMod-Farseer) (MIT, © Badgerson) —
  Vintage Story rendering techniques; adapted code is credited where used.

## License

[MIT](LICENSE)
