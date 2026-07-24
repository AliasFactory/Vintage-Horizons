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

vh_launch "Test server" "$PIDFILE" "$DATA/console.log" \
    dotnet VintagestoryServer.dll --dataPath "$DATA" \
    --withconfig="{ Port: $PORT, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }" \
    "$@"

echo "  port $PORT, dataPath $DATA"
