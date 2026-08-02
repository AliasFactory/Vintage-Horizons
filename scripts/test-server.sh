#!/usr/bin/env bash
set -euo pipefail

# A Vintage Story DEDICATED SERVER in a sandbox, for multiplayer tests of
# VintageHorizons.
#
# The server is vanilla, with no mod on the server side. That is the purpose. The mod
# must operate with an installation on the client only.
#
# The isolation rules are the rules of test-client.sh.
#
# The configuration is: port 42425 by default, which VH_TEST_PORT changes; no
# authentication, because the server is local; no advertisement; and the admin role for
# each player that joins, thus the teleports of the automatic exploration operate.
#
# The CLI of the game takes one --withconfig only. Thus extra configuration must go
# through the variables here, and not through a second flag.
#
# The console output goes to .testdata/server/console.log. The previous run stays as
# console.log.prev.

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

DATA="$VH_SANDBOX/server"
PIDFILE="$DATA/server.pid"
PORT="${VH_TEST_PORT:-42425}"

vh_guard_not_running "Test server" "$PIDFILE"

mkdir -p "$DATA/tmp" "$DATA/Mods"
export TMPDIR="$DATA/tmp"

# A server-side mod from an earlier benchmark makes the server request that mod from
# each client that joins. The client then shows "You are missing 1 mods to join this
# server", and that looks like a defect in this mod.
#
# bench.sh manages that set of mods directly. A normal run must at least report what is
# installed.
leftover="$(ls -A "$DATA/Mods" 2>/dev/null || true)"
if [[ -n "$leftover" ]]; then
    echo "NOTE: server-side mods present, clients will be required to have them:" >&2
    echo "$leftover" | sed 's/^/  /' >&2
    echo "  (rm -rf $DATA/Mods/* for a vanilla server)" >&2
fi

# Try again after a failure. A stop and an immediate restart leave the port in
# TIME_WAIT, and that bind failure is temporary. Each other failure stops the script with
# a message and the end of the console log.
#
# There are six attempts at 15 seconds, and not four at 10 seconds. Linux holds TIME_WAIT
# for approximately 60 seconds. Thus the old budget of 40 seconds could not outlast it.
#
# That was acceptable while each restart had a long client run before it. It stopped
# being acceptable when check-matrix.sh started to restart the server two times in
# sequence, to compare one configuration against another.
for attempt in 1 2 3 4 5 6; do
    ready=0
    if vh_launch "Test server" "$PIDFILE" "$DATA/console.log" \
        dotnet VintagestoryServer.dll --dataPath "$DATA" \
        --withconfig="{ Port: $PORT, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }" \
        "$@"
    then
        # Wait for the ready state here. Do not make each caller know the marker. That
        # mistake started a client against a server that never started.
        vh_wait_for "$DATA/console.log" "Dedicated Server now running" 180 "$PIDFILE" && ready=1
    fi

    if [ "$ready" = 1 ]; then
        echo "  port $PORT, dataPath $DATA"
        exit 0
    fi

    rm -f "$PIDFILE"
    if grep -q "Address already in use" "$DATA/console.log" 2>/dev/null && [ "$attempt" -lt 6 ]; then
        echo "Test server: port $PORT still in use, retrying in 15s (attempt $attempt)" >&2
        sleep 15
        continue
    fi

    echo "Test server failed to become ready. Last lines of console.log:" >&2
    tail -n 20 "$DATA/console.log" >&2 || true
    exit 1
done
