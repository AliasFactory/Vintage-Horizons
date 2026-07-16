#!/usr/bin/env bash
set -euo pipefail

# Sandboxed Vintage Story DEDICATED SERVER for VintageHorizons multiplayer testing.
# Vanilla (no mods installed server-side) — that's the point: the mod must work
# with a client-side-only install. Same isolation rules as test-client.sh.
#
# Default config: port 42425, no auth (offline/local), not advertised, joining
# players get the admin role (so auto-explore teleports work).
# Console output: .testdata/server/console.log

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA="$ROOT/.testdata/server"
PIDFILE="$DATA/server.pid"
GAME="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.3}"

if [[ -f "$PIDFILE" ]] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    echo "Test server already running (pid $(cat "$PIDFILE")). Stop it with scripts/test-stop.sh first." >&2
    exit 1
fi

mkdir -p "$DATA/tmp"
export TMPDIR="$DATA/tmp"
export DOTNET_ROOT=/usr/share/dotnet

cd "$GAME"
dotnet VintagestoryServer.dll --dataPath "$DATA" \
    --withconfig="{ Port: 42425, VerifyPlayerAuth: false, WhitelistMode: 'off', AdvertiseServer: false, DefaultRoleCode: 'admin' }" \
    "$@" > "$DATA/console.log" 2>&1 &
echo $! > "$PIDFILE"
echo "Test server started: pid $(cat "$PIDFILE"), port 42425, dataPath $DATA"
