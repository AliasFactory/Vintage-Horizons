#!/usr/bin/env bash
set -euo pipefail

# Tier 3: the installation combinations, and the controls that an admin uses.
#
# Tier 2 proves that the pipeline operates. This tier proves that the mod behaves
# correctly in the configurations that other people use. That includes each configuration
# where the correct behaviour is to do nothing.
#
# Usage: check-matrix.sh [--only <scenario>] [--skip-visual] [--settle <seconds>]
#
# Scenarios: client-only both no-client-mod serving-off capture-off pregen sweep radius deferral

source "$(dirname "${BASH_SOURCE[0]}")/test-lib.sh"

ONLY=""
SKIP_VISUAL=0
SETTLE=60

while [[ $# -gt 0 ]]; do
    case "$1" in
        --only) ONLY="$2"; shift 2 ;;
        --skip-visual) SKIP_VISUAL=1; shift ;;
        --settle) SETTLE="$2"; shift 2 ;;
        *) echo "usage: $(basename "$0") [--only <scenario>] [--skip-visual] [--settle <n>]" >&2; exit 2 ;;
    esac
done

CLIENT_LOG="$VH_SANDBOX/Logs/client-main.log"
SERVER_LOG="$VH_SANDBOX/server/Logs/server-main.log"
SERVER_CONFIG="$VH_SANDBOX/server/ModConfig/vintagehorizons-server.json"
BENCH_BUILT="$VH_ROOT/bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench"
PORT="${VH_TEST_PORT:-42425}"
failures=0

cleanup() { "$VH_ROOT/scripts/test-stop.sh" all >/dev/null 2>&1 || true; }
trap cleanup EXIT

wants() { [[ -z "$ONLY" || "$ONLY" == "$1" ]]; }
fail()  { echo "  $1: FAILED"; failures=$((failures + 1)); }

# --- The helpers for the sandbox state ------------------------------------------

client_mod()   { "$VH_ROOT/scripts/deploy-sandbox.sh" client >/dev/null; }
server_mod()   { "$VH_ROOT/scripts/deploy-sandbox.sh" server >/dev/null; }
no_client_mod(){ rm -rf "${VH_SANDBOX:?}/Mods/vintagehorizons"; }
no_server_mod(){ rm -rf "${VH_SANDBOX:?}/server/Mods/vintagehorizons"; }

wipe_client_cache() { rm -rf "${VH_SANDBOX:?}/ModData/vintagehorizons"; }
wipe_server_cache() { rm -rf "${VH_SANDBOX:?}/server/ModData/vintagehorizons"; }

# This script writes the file before the server starts. The server clamps the values and
# writes the file again at load. Thus a read of the file afterward also proves the round
# trip.
write_server_config() {
    mkdir -p "$(dirname "$SERVER_CONFIG")"
    cat > "$SERVER_CONFIG"
}

start_server() {
    cleanup
    "$VH_ROOT/scripts/test-server.sh" >/dev/null
}

# Run a client. Wait for the marker. Let the client reach a stable state. Then stop it
# cleanly, thus the last statistics line and the write of the storage queue both go into
# the log.
run_client() {
    local marker="${1:-Level finalized}" settle="${2:-$SETTLE}"
    rm -f "$CLIENT_LOG"

    VINTAGEHORIZONS_AUTOUNPAUSE=1 VINTAGEHORIZONS_STATS=1 \
        "$VH_ROOT/scripts/test-client.sh" -c "localhost:$PORT" >/dev/null

    if ! vh_wait_for "$CLIENT_LOG" "$marker" 240 "$VH_SANDBOX/test-instance.pid"; then
        echo "      x client never reached '$marker'"
        tail -n 20 "$VH_SANDBOX/launch.log" >&2 || true
        stop_client
        return 1
    fi

    sleep "$settle"
    stop_client
    return 0
}

# test-stop.sh gives a client 10 seconds to exit, and then it does not send a stronger
# signal. That is correct, because a client in the middle of a shutdown writes its LOD
# cache.
#
# The wait is the work of the caller. Without that wait, the next scenario meets a
# pidfile that is still valid.
stop_client() {
    "$VH_ROOT/scripts/test-stop.sh" client >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/test-instance.pid" 120 \
        || echo "      - client still shutting down after 2 minutes"
}

assert_log() { python3 "$VH_ROOT/scripts/check-log.py" "$CLIENT_LOG" "$@"; }

# One field from the statistics line of the assist. One example is "633", from
# "... 633 installed, ...". This takes the LAST occurrence, which is the stable value. It
# is not a sample from the middle of a run.
assist_field() {
    grep -oE "[0-9]+ $2" "$1" 2>/dev/null | tail -1 | grep -oE "^[0-9]+" || true
}

# --- Scenario 1: the normal case. A vanilla server, with the mod on the client only. ---
# Almost every player has this configuration. Here the assist must conclude "nothing
# here", and it must break nothing.

if wants client-only; then
    echo "  [client-only] mod on the client, vanilla server"
    # Remove the mod from the server first. deploy-sandbox.sh gives a warning when the
    # server still has the mod. A warning that this script prints and then makes incorrect
    # is worse than no warning.
    no_server_mod; client_mod; wipe_client_cache
    start_server
    if run_client; then
        assert_log --label "client-only" --expect-capture --expect-assist absent \
            || fail "client-only"
    else
        fail "client-only"
    fi
fi

# --- Scenario 2: the mod on both sides, with a pre-generated server, thus it holds
# data. ---

if wants both || wants radius; then
    echo "  [both] mod on both sides, server pre-generating a cache"
    client_mod; server_mod; wipe_client_cache; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 64,
  "MaxSectionsPerSecondTotal": 128,
  "PregenRadiusChunks": 24,
  "PregenColumnsPerSecond": 64
}
JSON
    start_server

    # Wait for the pre-generation to complete. Thus the steps that follow test the serve
    # path, and not a race against the worldgen.
    if vh_wait_for "$SERVER_LOG" "LOD pre-generation finished" 600 "$VH_SANDBOX/server/server.pid"; then
        echo "      - server pre-generation complete"
    else
        echo "      - server pre-generation did not finish in time; continuing anyway"
    fi
fi

if wants both; then
    if run_client; then
        assert_log --label "both      " --expect-capture --expect-assist connected \
            --expect-fetched || fail "both"
    else
        fail "both"
    fi

    # The server writes the config file again at load, with the clamped values. Thus the
    # content of the disk now is what the server applied.
    if [[ -f "$SERVER_CONFIG" ]]; then
        echo "      - server applied: $(tr -d ' \n' < "$SERVER_CONFIG")"
    fi
fi

# --- Scenario 3: a client WITHOUT the mod joins a server that has it. -------------
# The mod uses side: Universal, with both required flags false. An error here makes the
# server request the mod from each player. That is the one failure that makes an admin
# remove the mod immediately.

if wants no-client-mod; then
    echo "  [no-client-mod] vanilla client joins a modded server"
    # This scenario starts its own server. It does not use the server that scenario 2 left
    # running. Each scenario must operate alone. Without that, --only tests something
    # different, and it gives no message.
    #
    # Pre-generation is off, because this scenario needs a server that HAS the mod, and
    # nothing more.
    server_mod; no_client_mod
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 8,
  "MaxSectionsPerSecondTotal": 32,
  "PregenRadiusChunks": 0,
  "PregenColumnsPerSecond": 8
}
JSON
    start_server
    # The client has no mod, thus no log line of this mod exists. The marker must be a
    # vanilla line.
    #
    # The marker must also prove that the join COMPLETED. "Connected to server" appears
    # during the handshake, thus it also appears on a run that the server is about to
    # reject. The block registry arrives only after the server accepts the client.
    if run_client "block types from server" 15; then
        if grep -qi "missing.*mods to join\|you are missing" "$CLIENT_LOG" "$VH_SANDBOX/launch.log" 2>/dev/null; then
            echo "      x the server demanded the mod from a vanilla client"
            fail "no-client-mod"
        else
            echo "  no-client-mod: 1 ok"
        fi
    else
        fail "no-client-mod"
    fi
    client_mod
fi

# --- Scenario 4: the serving is off. The server keeps its cache, and gives nothing. ---

if wants serving-off; then
    echo "  [serving-off] server keeps its cache but shares none of it"
    client_mod; server_mod; wipe_client_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": false,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 8,
  "MaxSectionsPerSecondTotal": 32,
  "PregenRadiusChunks": 0,
  "PregenColumnsPerSecond": 8
}
JSON
    start_server
    if run_client; then
        assert_log --label "serving-off" --expect-capture --expect-assist off \
            --expect-no-fetch || fail "serving-off"
    else
        fail "serving-off"
    fi
fi

# --- Scenario 5: the capture is fully off. ----------------------------------------
# Each client must be unaffected. The result must be exactly the result on a server with
# no mod.

if wants capture-off; then
    echo "  [capture-off] server builds no cache at all"
    client_mod; server_mod; wipe_client_cache; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": false,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 8,
  "MaxSectionsPerSecondTotal": 32,
  "PregenRadiusChunks": 0,
  "PregenColumnsPerSecond": 8
}
JSON
    start_server
    if grep -q "Server LOD capture disabled" "$SERVER_LOG" 2>/dev/null; then
        echo "      - server reported capture disabled"
    else
        echo "      x server did not report capture as disabled"
        failures=$((failures + 1))
    fi
    if run_client; then
        assert_log --label "capture-off" --expect-capture --expect-no-fetch || fail "capture-off"
    else
        fail "capture-off"
    fi
fi

# --- Scenario 6: the pre-generation covers exactly the square that it promises. ---

if wants pregen; then
    echo "  [pregen] radius 2 chunks must request exactly (2*2+1)^2 = 25 columns"
    server_mod; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 8,
  "MaxSectionsPerSecondTotal": 32,
  "PregenRadiusChunks": 2,
  "PregenColumnsPerSecond": 64
}
JSON
    start_server
    if vh_wait_for "$SERVER_LOG" "LOD pre-generation finished" 180 "$VH_SANDBOX/server/server.pid"; then
        requested="$(grep -o "pre-generation finished: [0-9]* columns" "$SERVER_LOG" | tail -1 | grep -o '[0-9]*')"
        if [[ "$requested" == "25" ]]; then
            echo "  pregen: 1 ok ($requested columns, exactly the 5x5 square)"
        else
            echo "      x expected 25 columns, got '$requested'"
            failures=$((failures + 1))
        fi
    else
        fail "pregen"
    fi
fi

# --- Scenario 6b: A SWEEP MUST GENERATE NOTHING. ----------------------------------
# This is the one promise that the function makes. It is also the reason why a sweep is
# safe to have on by default, and pre-generation is not.
#
# This is not obvious. A load of a column whose surround is absent makes the engine
# generate that surround, to complete the worldgen across the seam. An earlier version of
# the sweep added 1,460 columns to the savegame, and it gave no message.
#
# This test uses the row counts of the savegame. That is the only place that holds the
# truth.

if wants sweep; then
    echo "  [sweep] indexing existing terrain must add none"
    server_mod; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 8,
  "MaxSectionsPerSecondTotal": 32,
  "SweepSavegame": true,
  "SweepRadiusChunks": 48,
  "SweepColumnsPerSecond": 32,
  "PregenRadiusChunks": 0,
  "PregenColumnsPerSecond": 8
}
JSON
    save="$VH_SANDBOX/server/Saves/default.vcdbs"
    before="$(python3 -c "
import sqlite3
c = sqlite3.connect('file:$save?mode=ro', uri=True)
print(c.execute('SELECT COUNT(*) FROM mapchunk').fetchone()[0],
      c.execute('SELECT COUNT(*) FROM chunk').fetchone()[0])
" 2>/dev/null)"

    start_server
    if vh_wait_for "$SERVER_LOG" "Savegame sweep finished" 900 "$VH_SANDBOX/server/server.pid"; then
        grep -o "Savegame sweep finished:.*nothing generated" "$SERVER_LOG" | tail -1 | sed 's/^/      - /'

        # Stop the server first. It does not write each row while it runs.
        "$VH_ROOT/scripts/test-stop.sh" server >/dev/null 2>&1 || true
        vh_wait_stopped "$VH_SANDBOX/server/server.pid" 180 || true

        after="$(python3 -c "
import sqlite3
c = sqlite3.connect('file:$save?mode=ro', uri=True)
print(c.execute('SELECT COUNT(*) FROM mapchunk').fetchone()[0],
      c.execute('SELECT COUNT(*) FROM chunk').fetchone()[0])
" 2>/dev/null)"

        echo "      - savegame before: $before   after: $after"
        if [[ "$before" == "$after" && -n "$before" ]]; then
            echo "  sweep: 1 ok (savegame unchanged - nothing was generated)"
        else
            echo "      x the savegame grew: sweeping generated terrain"
            failures=$((failures + 1))
        fi
    else
        fail "sweep"
    fi
fi

# --- Scenario 7: THE SERVE RADIUS. ------------------------------------------------
# A person measured this control before, and never watched it.
#
# This is the control for the map-revealing problem. Without it, a new player takes a
# survey of the full explored world, with no travel. Thus an admin judges the mod on this
# setting.

if wants radius; then
    echo "  [radius] capped serving must refuse sections outside the ring"
    client_mod; server_mod; wipe_client_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 512,
  "MaxSectionsPerSecondPerPlayer": 64,
  "MaxSectionsPerSecondTotal": 128,
  "PregenRadiusChunks": 24,
  "PregenColumnsPerSecond": 64
}
JSON
    start_server
    vh_wait_for "$SERVER_LOG" "LOD pre-generation finished" 600 "$VH_SANDBOX/server/server.pid" || true

    if run_client; then
        assert_log --label "radius    " --expect-capture --expect-assist connected \
            --expect-declined || fail "radius"
        capped_installed="$(assist_field "$CLIENT_LOG" installed)"
        capped_declined="$(assist_field "$CLIENT_LOG" declined)"
    else
        fail "radius"
    fi

    # This is the control with no limit.
    #
    # A test of "declined > 0" alone proves nothing. A section that is resident in RAM,
    # and that the mod did not write to the disk yet, is also declined. A run with no limit
    # produced 55 of those.
    #
    # Terrain that is absent at a distance looks the same, whether the server refused it or
    # never held it. Thus the only honest test gives the same server cache two times, with
    # the limit as the only difference.
    echo "      running the uncapped control"
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 64,
  "MaxSectionsPerSecondTotal": 128,
  "PregenRadiusChunks": 24,
  "PregenColumnsPerSecond": 64
}
JSON
    start_server
    vh_wait_for "$SERVER_LOG" "Dedicated Server now running" 180 "$VH_SANDBOX/server/server.pid" || true
    wipe_client_cache

    if run_client; then
        open_installed="$(assist_field "$CLIENT_LOG" installed)"
        open_declined="$(assist_field "$CLIENT_LOG" declined)"

        echo "      - capped 512: ${capped_installed:-?} installed, ${capped_declined:-?} declined"
        echo "      - uncapped:   ${open_installed:-?} installed, ${open_declined:-?} declined"

        if [[ -n "${capped_installed:-}" && -n "${open_installed:-}" \
              && "$capped_installed" -lt "$open_installed" ]]; then
            echo "  radius cap: 1 ok (the cap delivered fewer sections from the same cache)"
        else
            echo "      x the cap did not reduce sections delivered"
            failures=$((failures + 1))
        fi

        if [[ -n "${capped_declined:-}" && -n "${open_declined:-}" \
              && "$capped_declined" -gt "$open_declined" ]]; then
            echo "  radius refusals: 1 ok (the cap refused more than the uncapped baseline)"
        else
            echo "      x the cap did not raise refusals above the uncapped baseline"
            failures=$((failures + 1))
        fi
    else
        fail "radius uncapped control"
    fi

    # CAUTION: This step gives information only. It captures two frames. It does not test
    # what is in them, and it cannot test that.
    #
    # Across three attempts, the same route and the same settings gave images that did not
    # agree. One capped run drew nothing at a settle of 180 s, after it drew terrain at
    # 75 s.
    #
    # The reason is that what the client fetched is not what it drew. Meshing, eviction and
    # the descent of the quadtree are all between the two. The fog of the game also hides
    # the ring distance.
    #
    # The counters above are the verification. Read these frames beside those counters
    # only. Never make a conclusion from the pair of images alone.
    if [[ "$SKIP_VISUAL" == "0" ]]; then
        if [[ -d "$BENCH_BUILT" ]]; then
            echo "      capturing the visual pair (informational: not asserted on)"
            cp -r "$BENCH_BUILT" "$VH_SANDBOX/Mods/"
            mkdir -p "$VH_SANDBOX/bench"

            capture_ring() {
                local label="$1" radius="$2"
                write_server_config <<JSON
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": $radius,
  "MaxSectionsPerSecondPerPlayer": 64,
  "MaxSectionsPerSecondTotal": 128,
  "PregenRadiusChunks": 24,
  "PregenColumnsPerSecond": 64
}
JSON
                start_server
                vh_wait_for "$SERVER_LOG" "Dedicated Server now running" 180 \
                    "$VH_SANDBOX/server/server.pid" || true

                # Empty the client cache each time. Without that, the second run reads back
                # what the first run fetched, and the two images look the same.
                wipe_client_cache
                rm -f "$VH_SANDBOX/bench/$label.done" "$VH_SANDBOX/bench/$label.csv"

                # The settle time is long, and this value is not arbitrary. At 75 s, a
                # capped run gave a screenshot with 348 sections resident, but only 20
                # meshed and 4 selected. Thus that image showed the progress of the
                # meshing, and not what the server gave.
                #
                # The fill-in milestones put 600 meshes at approximately 40 s on a client
                # with enough data. The uncapped side has two times as many sections to
                # process.
                VHBENCH_ROUTE="$VH_ROOT/bench/routes/radius-cap.txt" \
                VHBENCH_LABEL="$label" VHBENCH_OUT="$VH_SANDBOX/bench" \
                VHBENCH_SETTLE="${VH_RING_SETTLE:-180}" VHBENCH_MEASURE=5 \
                VINTAGEHORIZONS_AUTOUNPAUSE=1 \
                    "$VH_ROOT/scripts/test-client.sh" -c "localhost:$PORT" >/dev/null

                vh_wait_for "$VH_SANDBOX/bench/$label.done" "" 300 \
                    "$VH_SANDBOX/test-instance.pid" || echo "      x $label capture timed out"
                stop_client
            }

            capture_ring "ring-capped-512" 512
            capture_ring "ring-uncapped" 0

            shots="$(ls "$VH_SANDBOX"/bench/ring-*.png 2>/dev/null | wc -l)"
            if [[ "$shots" -ge 2 ]]; then
                echo "  radius visual: $shots screenshots in $VH_SANDBOX/bench/"
                ls "$VH_SANDBOX"/bench/ring-*.png | sed 's/^/      - /'
            else
                echo "      x expected two screenshots, got $shots"
                failures=$((failures + 1))
            fi
            rm -rf "${VH_SANDBOX:?}/Mods/vintagehorizonsbench"
        else
            echo "      - skipping the visual pair: build bench/VintageHorizonsBench first"
        fi
    fi
fi

# --- Scenario 8: another LOD mod is installed. ------------------------------------
# Two mods that draw distant terrain set the far plane of the camera against each other,
# and they draw over each other. The correct behaviour is to do nothing, and that must be
# complete.

if wants deferral; then
    echo "  [deferral] another LOD mod present means we stay idle"
    if [[ -d "$VH_ROOT/bench/mods/farseer" ]]; then
        client_mod; server_mod
        # Install it on the server also. This is not for convenience. Farseer is
        # requiredOnServer. Thus against a vanilla server the client disables it, and
        # IsModEnabled returns false. Then there is nothing to defer to, and the scenario
        # tests nothing.
        #
        # This is also the shape in the real world. A server that runs one of these mods
        # makes each client install it.
        cp -r "$VH_ROOT/bench/mods/farseer" "$VH_SANDBOX/Mods/"
        cp -r "$VH_ROOT/bench/mods/farseer" "$VH_SANDBOX/server/Mods/"
        start_server
        # The deferral returns from StartClientSide before a world exists. Thus the mod
        # never records "Level finalized". The notice about the idle state is the only
        # marker.
        if run_client "staying idle" 15; then
            assert_log --label "deferral  " --expect-idle || fail "deferral"
        else
            fail "deferral"
        fi
        rm -rf "${VH_SANDBOX:?}/Mods/farseer" "${VH_SANDBOX:?}/server/Mods/farseer"
    else
        echo "      - skipping: no competing LOD mod at bench/mods/farseer"
    fi
fi

cleanup
exit $((failures > 0 ? 1 : 0))
