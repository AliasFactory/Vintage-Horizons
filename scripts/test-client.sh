#!/usr/bin/env bash
set -euo pipefail

# Sandboxed Vintage Story CLIENT for VintageHorizons testing.
#
# Isolation guarantees (all of them matter - each one exists because its absence failed):
#  - Own dataPath (.testdata): never touches a real game data directory.
#  - Own TMPDIR: the game's single-instance pipe (CoreFxPipe_SingleInstance
#    VintageStoryWithUriScheme) lives in $TMPDIR. Without this, a launch with
#    -c FORWARDS the connect request into whatever VS instance is already
#    running - any instance, not only a sandbox one - and exits silently.
#  - PID from $! in a pidfile, and every stop verifies /proc/<pid>/cmdline names
#    the sandbox before signalling. Stop ONLY via scripts/test-stop.sh; never
#    locate test instances by process name or argument matching.
#
# Env knobs pass through: VINTAGEHORIZONS_AUTOUNPAUSE, VINTAGEHORIZONS_AUTOEXPLORE,
# VINTAGEHORIZONS_EXPLORE_HOP. Extra args (e.g. -c localhost:42425, -o world) are
# forwarded to the game - pass ABSOLUTE paths, since the game runs with its
# install dir as cwd. Console output: .testdata/launch.log (previous run kept as
# launch.log.prev).

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

DATA="$VH_SANDBOX"
PIDFILE="$DATA/test-instance.pid"

vh_guard_not_running "Test client" "$PIDFILE"

# Mods/ must exist as a DIRECTORY before launch: a `cp zip .testdata/Mods` onto a
# missing path silently creates a file, and the game then loads no mod at all.
mkdir -p "$DATA/tmp" "$DATA/Mods"
export TMPDIR="$DATA/tmp"

# --addModPath: a relative 'Mods' entry in clientsettings modPaths resolves
# against the game install dir, NOT the dataPath - without this flag, mods
# placed in .testdata/Mods are silently ignored.
if ! vh_launch "Test client" "$PIDFILE" "$DATA/launch.log" \
    dotnet Vintagestory.dll --dataPath "$DATA" --addModPath "$DATA/Mods" "$@"; then
    echo "Test client died during startup. Last lines of launch.log:" >&2
    tail -n 20 "$DATA/launch.log" >&2 || true
    exit 1
fi

echo "  dataPath $DATA, TMPDIR $TMPDIR"
