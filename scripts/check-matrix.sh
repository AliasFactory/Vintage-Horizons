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
# Scenarios: client-only both no-client-mod serving-off capture-off pregen sweep
#            nondestructive peekdiff generate generate-sp radius deferral

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

# VH_DEVTOOLS=1 in front of a call forwards the dev-tools switch to the server, which
# is what registers the block-placing /vhgen edittest. Off everywhere else.
start_server() {
    cleanup
    VINTAGEHORIZONS_DEVTOOLS="${VH_DEVTOOLS:-0}" "$VH_ROOT/scripts/test-server.sh" >/dev/null
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

# --- Scenario 6c: GENERATION MUST NOT TOUCH THE SAVEGAME. -------------------------
# The strict form of the non-destructive promise, and stricter than the sweep's row
# counts: identical counts also pass when content was rewritten, or when five rows
# left and five arrived. This compares the row KEY SETS and a CONTENT hash of all
# three terrain tables, byte for byte.
#
# The run is aimed at virgin land far from spawn, over the console FIFO, with no
# client connected. Every column then takes the Peek arm, the run loads nothing into
# the live world, and nothing ticks - so content equality is assertable here in a way
# it never can be for the sweep, whose loads hand columns to vanilla simulation.
#
# The savegame is snapshotted between two clean server sessions, so trailing worldgen
# from the first boot cannot masquerade as generation damage from the second.

save_state() {
    python3 - "$1" <<'PYEOF'
import hashlib, sqlite3, sys
c = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
h = hashlib.sha256()
for table in ("mapchunk", "chunk", "mapregion"):
    keys = [r[0] for r in c.execute(f"SELECT position FROM {table} ORDER BY position")]
    h.update(repr(keys).encode())
    for row in c.execute(f"SELECT position, data FROM {table} ORDER BY position"):
        h.update(repr(row[0]).encode())
        h.update(row[1] if row[1] is not None else b"~")
    print(table, len(keys), end="  ")
print(h.hexdigest()[:16])
PYEOF
}

if wants nondestructive; then
    echo "  [nondestructive] generation must leave the savegame byte-identical"
    server_mod; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "SweepSavegame": false,
  "PregenRadiusChunks": 0,
  "GenerateColumnsPerSecond": 32,
  "GenerateMaxInFlight": 64
}
JSON
    save="$VH_SANDBOX/server/Saves/default.vcdbs"

    # Session 1: boot and stop cleanly, so the measured session starts from a
    # settled savegame.
    start_server
    sleep 15
    "$VH_ROOT/scripts/test-stop.sh" server >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/server/server.pid" 180 || true
    before="$(save_state "$save")"

    # Session 2: generate 32,000 blocks from spawn - land no session has touched.
    start_server
    echo "/vhgen start 12 480000 480000" > "$VH_SANDBOX/server/console.in"
    ok=1
    if vh_wait_for "$SERVER_LOG" "Generation finished" 600 "$VH_SANDBOX/server/server.pid"; then
        line="$(grep -o "Generation finished.*" "$SERVER_LOG" | tail -1)"
        echo "      - $line"
        # 25x25 columns, every one virgin: all generated, none loaded, none failed.
        echo "$line" | grep -q "625 generated" || { echo "      x expected 625 generated"; ok=0; }
        echo "$line" | grep -q "0 timed out"   || { echo "      x peeks timed out"; ok=0; }
        # The mod's own instrument must agree with the external measurement below.
        echo "$line" | grep -qE "Verified [0-9]+/[0-9]+ sampled" || { echo "      x no verify clause"; ok=0; }
        echo "$line" | grep -qE "Verified ([0-9]+)/\1 " || { echo "      x verify found regrown positions"; ok=0; }
    else
        echo "      x generation never finished"
        ok=0
    fi
    "$VH_ROOT/scripts/test-stop.sh" server >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/server/server.pid" 180 || true
    after="$(save_state "$save")"

    echo "      - before: $before"
    echo "      - after:  $after"
    [[ "$before" == "$after" && -n "$before" ]] \
        || { echo "      x the savegame changed: keys or content differ"; ok=0; }

    db="$(ls "$VH_SANDBOX"/server/ModData/vintagehorizons/*-server.db 2>/dev/null | head -1)"
    sections=$(python3 -c "
import sqlite3
print(sqlite3.connect('file:$db?mode=ro', uri=True).execute('SELECT COUNT(*) FROM Section').fetchone()[0])" 2>/dev/null || echo 0)
    [[ "${sections:-0}" -gt 0 ]] || { echo "      x the LOD cache gained no sections"; ok=0; }

    if [[ "$ok" == 1 ]]; then
        echo "  nondestructive: ok (625 peeks, savegame byte-identical, $sections sections built)"
    else
        fail "nondestructive"
    fi

    # Piggyback: a broken config must be preserved, not clobbered with defaults.
    echo "  [nondestructive] a corrupt config file must be left untouched"
    printf '{ "EnableCapture": true,, }' > "$SERVER_CONFIG"
    cfg_before="$(sha256sum "$SERVER_CONFIG")"
    start_server
    vh_wait_for "$SERVER_LOG" "Dedicated Server now running" 180 "$VH_SANDBOX/server/server.pid" || true
    "$VH_ROOT/scripts/test-stop.sh" server >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/server/server.pid" 180 || true
    cfg_after="$(sha256sum "$SERVER_CONFIG")"
    if [[ "$cfg_before" == "$cfg_after" ]] && grep -q "Could not parse" "$SERVER_LOG"; then
        echo "  config-preserve: ok (file byte-identical, parse warning logged)"
    else
        echo "      x the config file changed, or no parse warning was logged"
        fail "config-preserve"
    fi
    rm -f "$SERVER_CONFIG"
fi

# --- Scenario 6c2: WHAT A PEEK LOSES, AND WHAT IT CANNOT SEE. ---------------------
# Two experiments, both server-only over the console FIFO.
#
# The diff records what a Terrain-pass peek is missing against a full generation of
# the same coordinates. That caveat used to read "no trees", which came from the
# EnumWorldGenPass doc comments rather than measurement - the same method that
# under-reported the sweep's neighbour dependency by three rings.
#
# The edit test places a marker, reads it back from the loaded world, then peeks the
# same coordinate and looks for it. This is the evidence behind Classify never peeking
# a column that exists. It needs the dev-tools switch because it writes blocks.
#
# Assertions are on SHAPE, not counts: exact block counts move with the seed and the
# game version, so pinning them makes failures uninformative. The real numbers go to
# the scenario output for a human to read on every run.

if wants peekdiff; then
    echo "  [peekdiff] measure what a peek loses, and that it cannot see an edit"
    server_mod; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "SweepSavegame": false,
  "PregenRadiusChunks": 0
}
JSON
    VH_DEVTOOLS=1 start_server
    ok=1

    grep -q "Developer tools on" "$SERVER_LOG" \
        || { echo "      x the dev-tools notice is missing, so edittest is not registered"; ok=0; }

    # Experiment A, on land no session has touched.
    echo "/vhgen diff 470000 470000" > "$VH_SANDBOX/server/console.in"
    if vh_wait_for "$SERVER_LOG" "surface height delta median" 420 "$VH_SANDBOX/server/server.pid"; then
        grep -oE "Peek diff( at chunk|:) .*" "$SERVER_LOG" | tail -4 | sed 's/^/      - /'

        only_loaded="$(grep -oE "ONLY IN THE FULL GENERATION \([0-9]+\)" "$SERVER_LOG" | tail -1 | grep -oE "[0-9]+")"
        only_peek="$(grep -oE "ONLY IN THE PEEK \([0-9]+\)" "$SERVER_LOG" | tail -1 | grep -oE "[0-9]+")"
        median="$(grep -oE "surface height delta median -?[0-9]+" "$SERVER_LOG" | tail -1 | grep -oE -- "-?[0-9]+$")"
        peek_blocks="$(grep -oE "peek produced [0-9]+ blocks" "$SERVER_LOG" | tail -1 | grep -oE "[0-9]+")"

        # A peek that produced nothing would satisfy "something is missing" trivially.
        [[ "${peek_blocks:-0}" -gt 0 ]] || { echo "      x the peek produced no blocks at all"; ok=0; }
        [[ "${only_loaded:-0}" -gt 0 ]] || { echo "      x nothing was missing from the peek, which contradicts the pass list"; ok=0; }

        # The strongest claim the data supports: a peek never invents anything. Every
        # block type it produces also appears in a full generation, so generated LOD
        # can only ever be INCOMPLETE, never wrong. In principle a later pass could
        # replace every instance of some type and break this; that has not happened,
        # and if it ever does the case is worth reading rather than tolerating.
        [[ "${only_peek:-1}" == "0" ]] \
            || { echo "      x the peek produced $only_peek block types absent from a real generation"; ok=0; }

        # The ground itself must not move. Measured: median +1, because the snow layer
        # (pass 4, which a Terrain peek never runs) adds one block on top of every
        # exposed position - 1024 of them in a 32x32 column. So 0 or 1 is correct and
        # anything larger means the Terrain pass alone is not setting the terrain.
        [[ "${median:-99}" == "0" || "${median:-99}" == "1" ]] \
            || { echo "      x surface height median delta is $median: the peek moved the ground"; ok=0; }
    else
        echo "      x the diff never reported"
        ok=0
    fi

    # Experiment B. No coordinates: the console falls back to spawn, which is loaded
    # and therefore has a column a marker can actually be placed into.
    echo "/vhgen edittest" > "$VH_SANDBOX/server/console.in"
    if vh_wait_for "$SERVER_LOG" "MARKER PRESENT WHEN PEEKED" 240 "$VH_SANDBOX/server/server.pid"; then
        grep -oE "Edit test:.*" "$SERVER_LOG" | tail -3 | sed 's/^/      - /'
        verdict="$(grep -oE "MARKER PRESENT WHEN LOADED: (True|False)\. MARKER PRESENT WHEN PEEKED: (True|False)" "$SERVER_LOG" | tail -1)"
        [[ "$verdict" == *"WHEN LOADED: True"* ]] \
            || { echo "      x the marker did not read back from the loaded world"; ok=0; }
        [[ "$verdict" == *"WHEN PEEKED: False"* ]] \
            || { echo "      x the peek CONTAINED the placed marker, contradicting the API contract"; ok=0; }
    else
        echo "      x the edit test never reported"
        ok=0
    fi

    "$VH_ROOT/scripts/test-stop.sh" server >/dev/null 2>&1 || true
    vh_wait_stopped "$VH_SANDBOX/server/server.pid" 180 || true

    [[ "$ok" == 1 ]] && echo "  peekdiff: ok" || fail "peekdiff"
fi

# --- Scenario 6d: /vhgen FROM A REAL PLAYER, SERVED ON REJOIN. --------------------
# The player path the console FIFO cannot exercise: chat registration, the privilege
# gate, Caller.Pos. Then the part that makes it useful - a client with an empty cache
# rejoins and receives the generated sections over the assist. The manifest is sent
# once at handshake, so the SECOND join is the one that can see them.

if wants generate; then
    echo "  [generate] a player runs /vhgen; a rejoin fetches what it built"
    client_mod; server_mod; wipe_client_cache; wipe_server_cache
    write_server_config <<'JSON'
{
  "EnableCapture": true,
  "EnableServing": true,
  "ServeRadiusBlocks": 0,
  "MaxSectionsPerSecondPerPlayer": 64,
  "MaxSectionsPerSecondTotal": 128,
  "SweepSavegame": false,
  "PregenRadiusChunks": 0,
  "GenerateColumnsPerSecond": 32
}
JSON
    start_server
    ok=1

    # Join 1 types the command (the hook fires 15s after finalize) and leaves; the
    # server finishes the run on its own.
    VINTAGEHORIZONS_AUTOCMD="/vhgen start 10" run_client "Level finalized" 30 || ok=0
    vh_wait_for "$SERVER_LOG" "Generation finished" 600 "$VH_SANDBOX/server/server.pid" || {
        echo "      x generation never finished after the player command"; ok=0; }
    # Attributed to a player, not to the console. The line carries the player's own
    # name, so match on what it is not rather than on a literal word.
    if ! grep -q "Generation started by " "$SERVER_LOG" \
       || grep -q "Generation started by the console" "$SERVER_LOG"; then
        echo "      x the run was not attributed to a player"
        ok=0
    fi

    # Join 2, empty cache: the generated sections must arrive over the wire.
    wipe_client_cache
    run_client "Level finalized" 45 || ok=0
    installed="$(assist_field "$CLIENT_LOG" installed)"
    if [[ -n "$installed" && "$installed" -gt 0 ]]; then
        echo "      - rejoin installed $installed sections from the server"
    else
        echo "      x the rejoin installed nothing"
        ok=0
    fi

    [[ "$ok" == 1 ]] && echo "  generate: ok" || fail "generate"
fi

# --- Scenario 6e: /vhgen IN SINGLEPLAYER. -----------------------------------------
# The integration that has no server process: the guard leaves the server side idle,
# the command opens the cache lazily hours after startup would have, and the client
# adopts the result through the local offer source - which only works because the
# client re-probes for a sibling cache that did not exist when the world finalized.

if wants generate-sp; then
    echo "  [generate-sp] singleplayer: lazy cache, local adoption, host privilege"
    client_mod
    # The integrated server reads the same ModConfig directory as the client sandbox.
    mkdir -p "$VH_SANDBOX/ModConfig"
    cat > "$VH_SANDBOX/ModConfig/vintagehorizons-server.json" <<'JSON'
{
  "EnableCapture": true,
  "SweepSavegame": false,
  "PregenRadiusChunks": 0,
  "GenerateColumnsPerSecond": 32
}
JSON
    rm -f "$CLIENT_LOG"
    SP_SERVER_LOG="$VH_SANDBOX/Logs/server-main.log"
    rm -f "$SP_SERVER_LOG"

    cleanup
    # Radius 12, not something smaller: the client streams and self-captures its own
    # surroundings, so the adoption assertion needs generated sections beyond that.
    VINTAGEHORIZONS_AUTOUNPAUSE=1 VINTAGEHORIZONS_STATS=1 \
    VINTAGEHORIZONS_AUTOCMD="/vhgen start 12" \
        "$VH_ROOT/scripts/test-client.sh" -o vhgen-sp -p preset-surviveandbuild >/dev/null

    ok=1
    vh_wait_for "$CLIENT_LOG" "Level finalized" 600 "$VH_SANDBOX/test-instance.pid" || ok=0
    # The startup guard must have left the server side idle - no sweep, no cache.
    grep -q "server side stays idle" "$SP_SERVER_LOG" \
        || { echo "      x the idle-guard notice is missing"; ok=0; }
    # The command must open the cache on demand and run to completion. This is also
    # the empirical test that the singleplayer host holds the privilege.
    vh_wait_for "$SP_SERVER_LOG" "Generation finished" 600 "$VH_SANDBOX/test-instance.pid" || {
        echo "      x generation never finished in singleplayer"; ok=0; }
    grep -q "Server LOD capture active" "$SP_SERVER_LOG" \
        || { echo "      x the cache never opened lazily"; ok=0; }

    # The client must notice the cache that appeared mid-session and adopt from it.
    if ! vh_wait_for "$CLIENT_LOG" "adopting them as the view needs them" 240 \
        "$VH_SANDBOX/test-instance.pid"; then
        echo "      x the client never saw the late-appearing server cache"
        ok=0
    fi
    sleep 20
    stop_client

    [[ "$ok" == 1 ]] && echo "  generate-sp: ok" || fail "generate-sp"
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
