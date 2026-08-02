#!/usr/bin/env bash
set -euo pipefail

# A Vintage Story CLIENT in a sandbox, for tests of VintageHorizons.
#
# CAUTION: Each isolation rule below matters. Read "test isolation" in docs/STATUS.md.
#
#  - The client has its own dataPath, .testdata. It never touches the real game data of
#    the user.
#  - The client has its own TMPDIR. The single-instance pipe of the game,
#    CoreFxPipe_SingleInstanceVintageStoryWithUriScheme, is in $TMPDIR. Without a private
#    TMPDIR, a launch with -c SENDS the connect request into the Vintage Story instance
#    that already runs. That instance can be the personal game of the user. Then the new
#    process stops with no message.
#  - The PID comes from $! and goes into a pidfile. Each stop examines
#    /proc/<pid>/cmdline for the sandbox before it sends a signal. Stop an instance ONLY
#    with scripts/test-stop.sh. Never find a test instance by its process name, or by a
#    match on its arguments.
#
# These environment variables pass through: VINTAGEHORIZONS_AUTOUNPAUSE,
# VINTAGEHORIZONS_AUTOEXPLORE and VINTAGEHORIZONS_EXPLORE_HOP.
#
# This script sends the other arguments to the game. Two examples are -c localhost:42425
# and -o world. Give an ABSOLUTE path, because the game runs with its installation
# directory as its working directory.
#
# The console output goes to .testdata/launch.log. The previous run stays as
# launch.log.prev.

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

DATA="$VH_SANDBOX"
PIDFILE="$DATA/test-instance.pid"

vh_guard_not_running "Test client" "$PIDFILE"

# Mods/ must exist as a DIRECTORY before the launch. A command `cp zip .testdata/Mods`
# onto a path that is absent makes a file, and it gives no message. Then the game loads
# no mod at all.
mkdir -p "$DATA/tmp" "$DATA/Mods"
export TMPDIR="$DATA/tmp"

# Use --addModPath. A relative 'Mods' entry in the modPaths of clientsettings points at
# the installation directory of the game, and NOT at the dataPath. Without this flag, the
# game ignores each mod in .testdata/Mods, and it gives no message.
if ! vh_launch "Test client" "$PIDFILE" "$DATA/launch.log" \
    dotnet Vintagestory.dll --dataPath "$DATA" --addModPath "$DATA/Mods" "$@"; then
    echo "Test client died during startup. Last lines of launch.log:" >&2
    tail -n 20 "$DATA/launch.log" >&2 || true
    exit 1
fi

echo "  dataPath $DATA, TMPDIR $TMPDIR"
