#!/usr/bin/env bash
set -euo pipefail

# Tier 3: the install combinations and the admin-facing controls.
#
# Tier 2 proves the pipeline works. This proves it behaves correctly in the
# configurations other people will actually put it in - including the ones where the
# right answer is "do nothing and stay out of the way".
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

# --- Sandbox state helpers -------------------------------------------------------

client_mod()   { "$VH_ROOT/scripts/deploy-sandbox.sh" client >/dev/null; }
server_mod()   { "$VH_ROOT/scripts/deploy-sandbox.sh" server >/dev/null; }
no_client_mod(){ rm -rf "${VH_SANDBOX:?}/Mods/vintagehorizons"; }
no_server_mod(){ rm -rf "${VH_SANDBOX:?}/server/Mods/vintagehorizons"; }

wipe_client_cache() { rm -rf "${VH_SANDBOX:?}/ModData/vintagehorizons"; }
wipe_server_cache() { rm -rf "${VH_SANDBOX:?}/server/ModData/vintagehorizons"; }

# Written before the server starts; it sanitizes and rewrites this file on load, so
# reading it back afterwards also proves the round trip.
write_server_config() {
    mkdir -p "$(dirname "$SERVER_CONFIG")"
    cat > "$SERVER_CONFIG"
}

start_server() {
    cleanup
    "$VH_ROOT/scripts/test-server.sh" >/dev/null
}

# Runs a client, waits for the marker, lets it settle, then stops it cleanly so the
# final statistics line and the storage drain both land in the log.
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

# test-stop.sh gives a client 10s to exit and then refuses to escalate, which is correct:
# a client mid-shutdown is flushing its LOD cache. Waiting is the caller's job, and
# skipping it means the next scenario trips over a pidfile that is still valid.
stop_client() {
    "$VH_ROOT/scripts/test-stop.sh" client >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/test-instance.pid" 120 \
        || echo "      - client still shutting down after 2 minutes"
}

assert_log() { python3 "$VH_ROOT/scripts/check-log.py" "$CLIENT_LOG" "$@"; }

# One field out of the assist statistics line, e.g. "633" from "... 633 installed, ...".
# Takes the LAST occurrence: the settled figure, not a sample from mid-run.
assist_field() {
    grep -oE "[0-9]+ $2" "$1" 2>/dev/null | tail -1 | grep -oE "^[0-9]+" || true
}

# --- Scenario 1: the ordinary case. A vanilla server, mod on the client only. -----
# The configuration almost every player is in, and the one where the assist must
# conclude "nothing here" without breaking anything.

if wants client-only; then
    echo "  [client-only] mod on the client, vanilla server"
    # Strip the server first: deploy-sandbox.sh warns when the server still has the mod,
    # and a warning printed and then immediately made untrue is worse than none.
    no_server_mod; client_mod; wipe_client_cache
    start_server
    if run_client; then
        assert_log --label "client-only" --expect-capture --expect-assist absent \
            || fail "client-only"
    else
        fail "client-only"
    fi
fi

# --- Scenario 2: mod on both sides, server pre-generated so it has something. -----

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

    # Wait for the pre-generation to finish, so what follows is testing the serve
    # path rather than a race against worldgen.
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

    # The config file is rewritten on load with sanitized values, so what is on disk
    # now is what the server actually applied.
    if [[ -f "$SERVER_CONFIG" ]]; then
        echo "      - server applied: $(tr -d ' \n' < "$SERVER_CONFIG")"
    fi
fi

# --- Scenario 3: a client WITHOUT the mod joins a server that has it. -------------
# side: Universal with both required flags false. Get this wrong and the server
# demands the mod from every player, which is the one failure that would make an
# admin uninstall it immediately.

if wants no-client-mod; then
    echo "  [no-client-mod] vanilla client joins a modded server"
    # Its own server, rather than inheriting the one scenario 2 left running: every
    # scenario has to stand alone or --only silently tests something else. Pre-generation
    # off, because all this needs is a server that HAS the mod.
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
    # Without our mod none of our own log lines exist, so the marker has to be a vanilla
    # one - and it has to prove the join COMPLETED. "Connected to server" appears during
    # the handshake and so would also appear on a run that is about to be rejected;
    # receiving the block registry only happens once the server has accepted the client.
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

# --- Scenario 4: serving switched off. Cache kept, nothing shared. ----------------

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

# --- Scenario 5: capture switched off entirely. -----------------------------------
# Clients must be completely unaffected: exactly as on a server without the mod.

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

# --- Scenario 6: pre-generation covers exactly the square it promises. ------------

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

# --- Scenario 6b: SWEEPING MUST NOT GENERATE. -------------------------------------
# The single promise the feature makes, and the reason it is safe to default on where
# pre-generation is not. Loading a column whose surroundings are absent makes the engine
# generate them to finish worldgen across the seam, so this is not self-evident: an
# earlier version of the sweep silently added 1,460 columns to the savegame.
#
# Asserted against the savegame's own row counts, which is the only place the truth is.

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

        # Stop first: rows are not all flushed while the server is live.
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
# Measured before but never watched. This is the map-revealing control: without it a
# new player could pull a survey of the whole explored world without travelling, so
# it is the setting an admin will judge the mod on.

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

    # The uncapped control. A bare "declined > 0" proves nothing on its own: sections
    # resident in RAM but not yet flushed to disk are also declined, and an uncapped run
    # was measured producing 55 of them. Terrain missing at distance looks identical
    # whether the server refused it or never had it, so the only honest test is the same
    # server cache served twice with the cap as the only difference.
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

    # INFORMATIONAL ONLY. This captures two frames; it does not and cannot assert what is
    # in them. Across three attempts the same route and configs produced contradictory
    # images - including a capped run that rendered nothing at a 180s settle after
    # rendering terrain at 75s - because what the client has fetched is not what it has
    # drawn: meshing, eviction and quadtree descent all sit in between, and the game's fog
    # hides the ring distance anyway. The counters above are the verification. Read these
    # frames only alongside them, and never conclude anything from the pair alone.
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

                # An empty client cache each time, or the second run simply reads back
                # what the first one fetched and both pictures look the same.
                wipe_client_cache
                rm -f "$VH_SANDBOX/bench/$label.done" "$VH_SANDBOX/bench/$label.csv"

                # A long settle, and not arbitrarily: at 75s a capped run was screenshotted
                # with 348 sections resident but only 20 meshed and 4 selected, so the
                # picture showed meshing progress rather than what the server had served.
                # Fill-in milestones put 600 meshes at ~40s on a well-fed client, and the
                # uncapped side has twice as many sections to get through.
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
# Two mods drawing distant terrain fight over the camera far plane and draw over each
# other. Going idle is the correct behaviour, and it must be complete.

if wants deferral; then
    echo "  [deferral] another LOD mod present means we stay idle"
    if [[ -d "$VH_ROOT/bench/mods/farseer" ]]; then
        client_mod; server_mod
        # On the server too, and not as a convenience: Farseer is requiredOnServer, so
        # against a vanilla server the client disables it and IsModEnabled returns false.
        # There is then nothing to defer to and the scenario tests nothing. That is also
        # the real-world shape - a server running one of these forces it on every client.
        cp -r "$VH_ROOT/bench/mods/farseer" "$VH_SANDBOX/Mods/"
        cp -r "$VH_ROOT/bench/mods/farseer" "$VH_SANDBOX/server/Mods/"
        start_server
        # Deferring returns from StartClientSide before a world exists, so "Level
        # finalized" is never logged. The idle notice is the only marker there is.
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
