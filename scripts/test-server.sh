#!/usr/bin/env bash
set -euo pipefail

# Sandboxed Vintage Story DEDICATED SERVER for VintageHorizons multiplayer testing.
# Vanilla (no mods installed server-side) — that's the point: the mod must work
# with a client-side-only install. Same isolation rules as test-client.sh.
#
# Config: port 42425 by default (override with VH_TEST_PORT), no auth
# (offline/local), not advertised, joining players get the admin role so
# auto-explore teleports work. The game's CLI takes a single --withconfig, so
# extra config must go through the variables here rather than a second flag.
# Console output: .testdata/server/console.log (previous run kept as .prev).

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

DATA="$VH_SANDBOX/server"
PIDFILE="$DATA/server.pid"
PORT="${VH_TEST_PORT:-42425}"

vh_guard_not_running "Test server" "$PIDFILE"

mkdir -p "$DATA/tmp" "$DATA/Mods"
export TMPDIR="$DATA/tmp"

# A server-side mod left over from a previous benchmark makes the server demand it
# from every joining client, which shows up as "You are missing 1 mods to join this
# server" and looks like a bug in our own mod. bench.sh manages this set explicitly;
# a plain run must at least say what is installed.
leftover="$(ls -A "$DATA/Mods" 2>/dev/null || true)"
if [[ -n "$leftover" ]]; then
    echo "NOTE: server-side mods present, clients will be required to have them:" >&2
    echo "$leftover" | sed 's/^/  /' >&2
    echo "  (rm -rf $DATA/Mods/* for a vanilla server)" >&2
fi

# Retry: stopping and immediately restarting leaves the port in TIME_WAIT, and the
# bind failure is transient. Anything else fails loudly with the console tail.
#
# Six attempts at 15s, not four at 10s: Linux holds TIME_WAIT for about 60s, so the old
# 40s budget could not outlast it. That was survivable while every restart had a long
# client run in front of it, and stopped being so once check-matrix.sh started restarting
# the server twice in a row to compare one config against another.
for attempt in 1 2 3 4 5 6; do
    ready=0
    if vh_launch "Test server" "$PIDFILE" "$DATA/console.log" \
        dotnet VintagestoryServer.dll --dataPath "$DATA" \
        --withconfig="{ Port: $PORT, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }" \
        "$@"
    then
        # Wait for readiness here rather than making every caller know the marker --
        # getting that wrong once launched a client against a server that never started.
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
