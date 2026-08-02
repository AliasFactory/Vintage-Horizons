#!/usr/bin/env bash
# Start Vintage Story with the development build of VintageHorizons.
#
# Usage: scripts/dev-run.sh [worldname] [playstyle]
#
# The default worldname is "vhsurvival".
#
# The game uses playstyle only when it creates the world. That value must be a playstyle
# LANG code. The game default is "creativebuilding", and it makes a superflat world,
# which no LOD test can use.
#
# For real terrain, give "preset-surviveandbuild". Note the prefix. The plain code
# "surviveandbuild" does not match, it gives no message, and the game makes a superflat
# world.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.5}"
WORLD="${1:-vhsurvival}"
PLAYSTYLE="${2:-preset-surviveandbuild}"

MOD_PATH="$REPO_DIR/VintageHorizons/bin/Debug/net10.0/Mods"
[ -d "$MOD_PATH/vintagehorizons" ] || { echo "No build output at $MOD_PATH - run: dotnet build VintageHorizons"; exit 1; }

# The DOTNET_ROOT of the desktop launcher, at ~/.dotnet, is old on this machine. The SDK
# for the full system is at /usr/share/dotnet.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

cd "$GAME_DIR"
exec ./Vintagestory --tracelog \
  --addModPath "$MOD_PATH" \
  -o "$WORLD" -p "$PLAYSTYLE"
