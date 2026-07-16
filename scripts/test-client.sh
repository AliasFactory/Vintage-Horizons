#!/usr/bin/env bash
set -euo pipefail

# Sandboxed Vintage Story CLIENT for VintageHorizons testing.
#
# Isolation guarantees (all three matter — see docs/STATUS.md "test isolation"):
#  - Own dataPath (.testdata): never touches the user's real game data.
#  - Own TMPDIR: the game's single-instance pipe (CoreFxPipe_SingleInstance
#    VintageStoryWithUriScheme) lives in $TMPDIR. Without this, launching with
#    -c FORWARDS the connect request into whatever VS instance is already
#    running — including the user's personal game — and exits silently.
#  - PID from $! recorded in a pidfile: stop ONLY via scripts/test-stop.sh.
#    Never locate test instances with pgrep name/arg matching.
#
# Env knobs pass through: VINTAGEHORIZONS_AUTOUNPAUSE, VINTAGEHORIZONS_AUTOEXPLORE,
# VINTAGEHORIZONS_EXPLORE_HOP. Extra args (e.g. -c localhost:42425, -o world) are
# forwarded to the game. Console output: .testdata/launch.log

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA="$ROOT/.testdata"
PIDFILE="$DATA/test-instance.pid"
GAME="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.3}"

if [[ -f "$PIDFILE" ]] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    echo "Test client already running (pid $(cat "$PIDFILE")). Stop it with scripts/test-stop.sh first." >&2
    exit 1
fi

mkdir -p "$DATA/tmp"
export TMPDIR="$DATA/tmp"
export DOTNET_ROOT=/usr/share/dotnet

cd "$GAME"
# --addModPath: a relative 'Mods' entry in clientsettings modPaths resolves
# against the game install dir, NOT the dataPath — without this flag, mods
# placed in .testdata/Mods are silently ignored.
dotnet Vintagestory.dll --dataPath "$DATA" --addModPath "$DATA/Mods" "$@" > "$DATA/launch.log" 2>&1 &
echo $! > "$PIDFILE"
echo "Test client started: pid $(cat "$PIDFILE"), dataPath $DATA, TMPDIR $TMPDIR"
