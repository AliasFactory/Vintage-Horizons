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

mkdir -p "$DATA/tmp"
export TMPDIR="$DATA/tmp"

# Retry: stopping and immediately restarting leaves the port in TIME_WAIT, and the
# bind failure is transient. Anything else fails loudly with the console tail.
for attempt in 1 2 3 4; do
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
    if grep -q "Address already in use" "$DATA/console.log" 2>/dev/null && [ "$attempt" -lt 4 ]; then
        echo "Test server: port $PORT still in use, retrying in 10s (attempt $attempt)" >&2
        sleep 10
        continue
    fi

    echo "Test server failed to become ready. Last lines of console.log:" >&2
    tail -n 20 "$DATA/console.log" >&2 || true
    exit 1
done
