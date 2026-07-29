#!/usr/bin/env bash
# Launch Vintage Story with the dev build of VintageHorizons loaded.
# Usage: scripts/dev-run.sh [worldname] [playstyle]
#   worldname defaults to "vhsurvival". playstyle (only used when the world is
#   created fresh) must be a playstyle LANG code: the game default
#   "creativebuilding" generates a superflat world useless for LOD testing;
#   real terrain needs "preset-surviveandbuild" (note the prefix - the plain
#   code "surviveandbuild" silently mismatches and falls back to superflat).
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.5}"
WORLD="${1:-vhsurvival}"
PLAYSTYLE="${2:-preset-surviveandbuild}"

MOD_PATH="$REPO_DIR/VintageHorizons/bin/Debug/net10.0/Mods"
[ -d "$MOD_PATH/vintagehorizons" ] || { echo "No build output at $MOD_PATH - run: dotnet build VintageHorizons"; exit 1; }

# The desktop launcher's DOTNET_ROOT (~/.dotnet) is stale on this machine;
# the system-wide SDK lives in /usr/share/dotnet.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

cd "$GAME_DIR"
exec ./Vintagestory --tracelog \
  --addModPath "$MOD_PATH" \
  -o "$WORLD" -p "$PLAYSTYLE"
