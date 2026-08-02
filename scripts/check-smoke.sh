#!/usr/bin/env bash
set -euo pipefail

# Tier 2. It answers one question: does the full pipeline operate, end to end, with no
# error?
#
# The script starts a vanilla dedicated server, and a client in a sandbox with the mod as
# its only addition. It lets the client capture for a time. It stops both cleanly. Then
# it examines what the logs record.
#
# Then it starts the client again, against the warm cache. That second run is the only
# way to know that the mod can read back what it wrote.
#
# CAUTION: Each step here uses the isolation code in the test-*.sh scripts, with no
# change. Those rules are safety-critical, and a violation stopped the live game of the
# user one time. This script is a caller of them, and never a change to them.
#
# Usage: check-smoke.sh [--settle <seconds>]

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

SETTLE=90
while [[ $# -gt 0 ]]; do
    case "$1" in
        --settle) SETTLE="$2"; shift 2 ;;
        *) echo "usage: $(basename "$0") [--settle <seconds>]" >&2; exit 2 ;;
    esac
done

CLIENT_LOG="$VH_SANDBOX/Logs/client-main.log"
PORT="${VH_TEST_PORT:-42425}"
failures=0

cleanup() { "$VH_ROOT/scripts/test-stop.sh" all >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "  smoke: deploying a fresh build"
"$VH_ROOT/scripts/deploy-sandbox.sh" client >/dev/null

# The server is vanilla, on purpose. The mod must operate with an installation on the
# client only, and almost every player has that configuration.
rm -rf "${VH_SANDBOX:?}/server/Mods/vintagehorizons"

cleanup
echo "  smoke: starting a vanilla dedicated server"
"$VH_ROOT/scripts/test-server.sh" >/dev/null

run_client() {
    local label="$1" wait_for="$2"
    shift 2

    rm -f "$CLIENT_LOG"

    VINTAGEHORIZONS_AUTOUNPAUSE=1 VINTAGEHORIZONS_STATS=1 \
        "$VH_ROOT/scripts/test-client.sh" -c "localhost:$PORT" >/dev/null

    if ! vh_wait_for "$CLIENT_LOG" "$wait_for" 240 "$VH_SANDBOX/test-instance.pid"; then
        echo "  smoke ($label): client never reached '$wait_for'"
        tail -n 25 "$VH_SANDBOX/launch.log" >&2 || true
        return 1
    fi

    echo "  smoke ($label): joined, capturing for ${SETTLE}s"
    sleep "$SETTLE"

    # Stop the client before the tests. The last statistics line and the write of the
    # storage queue both occur at a clean shutdown only. "Nothing stays unwritten" is
    # exactly what these tests must know.
    #
    # Then wait until the process is gone. A write of a few thousand sections regularly
    # needs more than the 10 seconds that test-stop.sh waits, and nothing must hurry
    # it.
    "$VH_ROOT/scripts/test-stop.sh" client >/dev/null
    vh_wait_stopped "$VH_SANDBOX/test-instance.pid" 120 \
        || echo "      - client still shutting down after 2 minutes"
    return 0
}

# --- Pass 1: a cold cache. ---
# The script empties the cache. Thus this session captured each section in this run from
# the start.
rm -rf "${VH_SANDBOX:?}/ModData/vintagehorizons"

if run_client "cold" "Level finalized"; then
    python3 "$VH_ROOT/scripts/check-log.py" "$CLIENT_LOG" \
        --label "smoke cold " --expect-capture || failures=$((failures + 1))
else
    failures=$((failures + 1))
fi

# --- Pass 2: a warm cache. ---
# This is the round trip of the persistence. A section that the mod cannot read back is
# invisible, until a person restarts the game and finds a hole. Thus this test finds
# it.
if run_client "warm" "Level finalized"; then
    python3 "$VH_ROOT/scripts/check-log.py" "$CLIENT_LOG" \
        --label "smoke warm " --expect-capture --expect-cache-loaded || failures=$((failures + 1))
else
    failures=$((failures + 1))
fi

cleanup

if [[ -d "$VH_SANDBOX/ModData/vintagehorizons" ]]; then
    echo "      - cache on disk: $(du -sh "$VH_SANDBOX/ModData/vintagehorizons" | cut -f1)"
fi

exit $((failures > 0 ? 1 : 0))
