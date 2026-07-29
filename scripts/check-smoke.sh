#!/usr/bin/env bash
set -euo pipefail

# Tier 2: does the whole pipeline run, end to end, without anything going wrong?
#
# Boots a strictly vanilla dedicated server and a sandboxed client with the mod as its
# only addition, lets it capture for a while, stops cleanly, and asserts on what the
# logs say happened. Then restarts against the warm cache, which is the only way to
# check that what was written can be read back.
#
# Everything here goes through the existing test-*.sh isolation plumbing unchanged.
# Those rules are safety-critical - a violation once crashed the user's live game -
# and this script is a caller, never a modification.
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

# A vanilla server, deliberately: the mod must work as a client-side-only install, and
# that is the configuration almost every player is actually in.
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

    # Stop before asserting: the final statistics line and the storage drain both only
    # happen on a clean shutdown, and "nothing left unwritten" is exactly what we want
    # to know. Then wait for it to really be gone - flushing a few thousand sections
    # regularly outlasts test-stop.sh's 10s patience, and it must not be hurried.
    "$VH_ROOT/scripts/test-stop.sh" client >/dev/null
    vh_wait_stopped "$VH_SANDBOX/test-instance.pid" 120 \
        || echo "      - client still shutting down after 2 minutes"
    return 0
}

# --- Pass 1: cold cache. ---
# Wiped, so every section in this run was captured from scratch this session.
rm -rf "${VH_SANDBOX:?}/ModData/vintagehorizons"

if run_client "cold" "Level finalized"; then
    python3 "$VH_ROOT/scripts/check-log.py" "$CLIENT_LOG" \
        --label "smoke cold " --expect-capture || failures=$((failures + 1))
else
    failures=$((failures + 1))
fi

# --- Pass 2: warm cache. ---
# The persistence round trip. A section that cannot be read back is invisible until
# someone restarts and finds a hole, so this is the assertion that catches it.
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
