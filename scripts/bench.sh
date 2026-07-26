#!/usr/bin/env bash
set -euo pipefail

# Run one benchmark configuration end to end, in the sandbox, and collect its results.
#
#   scripts/bench.sh <label> [--mods <dir-or-zip>[,<dir-or-zip>...]] [--server-mods <...>]
#                            [--route <file>] [--settle <sec>] [--measure <sec>]
#                            [--detail <blocks>]
#
# Examples:
#   scripts/bench.sh vanilla                            # no LOD mod at all: the baseline
#   scripts/bench.sh vintagehorizons --mods dist/vintagehorizons_0.1.0.zip
#   scripts/bench.sh farseer --mods /path/farseer.zip --server-mods /path/farseer.zip
#
# The label names the configuration under test and appears in every output filename, so
# results from different mods sit side by side in one directory:
#   .testdata/bench/<label>.csv        frame timings per waypoint
#   .testdata/bench/<label>--<wp>.png  one screenshot per waypoint
#
# Comparisons are only meaningful if the world, route, settle and measure times are
# identical across runs -- change one of those and previously recorded results no longer
# compare. The harness mod pins time of day, weather and camera angles for the same
# reason.
#
# Server-side mods: Farseer and ChunkLOD are 'Universal' and required on both sides, so
# they need --server-mods as well as --mods. VintageHorizons never does.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/test-lib.sh"

label="${1:-}"
if [[ -z "$label" || "$label" == -* ]]; then
    echo "usage: bench.sh <label> [--mods <list>] [--server-mods <list>] [--route <file>] [--settle <s>] [--measure <s>] [--detail <blocks>]" >&2
    exit 2
fi
shift

client_mods=""
server_mods=""
route="$ROOT/bench/routes/vhsurvival.txt"
settle=20
measure=10
detail=""
watch=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mods) client_mods="$2"; shift 2 ;;
        --server-mods) server_mods="$2"; shift 2 ;;
        --route) route="$2"; shift 2 ;;
        --settle) settle="$2"; shift 2 ;;
        --measure) measure="$2"; shift 2 ;;
        --detail) detail="$2"; shift 2 ;;
        --watch) watch=1; shift ;;
        *) echo "bench.sh: unknown option '$1'" >&2; exit 2 ;;
    esac
done

[[ -f "$route" ]] || { echo "bench.sh: route '$route' not found" >&2; exit 2; }

BENCH_OUT="$VH_SANDBOX/bench"
CLIENT_MODS="$VH_SANDBOX/Mods"
SERVER_MODS="$VH_SANDBOX/server/Mods"

mkdir -p "$BENCH_OUT" "$CLIENT_MODS" "$SERVER_MODS"

# Start from a known mod set every time: a mod left over from the previous
# configuration would silently be part of the next one's measurement.
install_mods() {
    local dest="$1" list="$2"
    rm -rf "$dest"
    mkdir -p "$dest"
    [[ -z "$list" ]] && return 0
    local IFS=','
    for item in $list; do
        [[ -e "$item" ]] || { echo "bench.sh: mod '$item' not found" >&2; exit 2; }
        cp -r "$item" "$dest/"
        echo "  installed $(basename "$item") -> $dest"
    done
}

echo "Bench '$label': preparing mods"
install_mods "$CLIENT_MODS" "$client_mods"
install_mods "$SERVER_MODS" "$server_mods"

# The harness itself is always present on the client, whatever is under test.
BENCH_MOD="$ROOT/bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench"
[[ -d "$BENCH_MOD" ]] || { echo "bench.sh: build the harness first (dotnet build bench/VintageHorizonsBench)" >&2; exit 2; }
cp -r "$BENCH_MOD" "$CLIENT_MODS/"

# Pre-seed the detail distance when benchmarking our own mod at a given setting.
if [[ -n "$detail" ]]; then
    mkdir -p "$VH_SANDBOX/ModConfig"
    printf '{\n  "FarViewDistanceCap": 0,\n  "DetailDistance": %s\n}\n' "$detail" \
        > "$VH_SANDBOX/ModConfig/vintagehorizons.json"
    echo "  detail distance pinned to $detail"
fi

# Uncap the frame rate. With vsync on, every configuration reports the monitor's
# refresh rate as its average and the comparison says nothing; only the 1% lows would
# differ. Written into the sandbox settings so it applies whatever mod is under test.
#
# --watch turns vsync back on so the run is comfortable to sit and watch: rendering
# hundreds of uncapped frames per second does not present cleanly on a compositor and
# makes the window look stale or blank. Numbers from a watch run are NOT comparable
# with measured runs, and are labelled to keep them out of the comparison.
python3 - "$VH_SANDBOX/clientsettings.json" "$watch" <<'PY'
import json, os, sys
path, watch = sys.argv[1], sys.argv[2] == "1"
# The file only exists once the client has run at least once in this sandbox.
cfg = {}
if os.path.exists(path):
    with open(path) as f:
        cfg = json.load(f)
cfg.setdefault("intSettings", {})["vsyncMode"] = 1 if watch else 0
cfg["intSettings"]["maxFps"] = 60 if watch else 0
with open(path, "w") as f:
    json.dump(cfg, f, indent=1)
print("  vsync on, 60 fps cap (watchable)" if watch else "  vsync off, fps uncapped")
PY

if [[ "$watch" == 1 ]]; then
    label="${label}-watch"
    echo "  watch mode: results labelled '$label' so they cannot be mistaken for measurements"
fi

rm -f "$BENCH_OUT/$label.done" "$BENCH_OUT/$label.csv"

"$ROOT/scripts/test-stop.sh" >/dev/null 2>&1 || true
"$ROOT/scripts/test-server.sh"

export VHBENCH_ROUTE="$route"
export VHBENCH_LABEL="$label"
export VHBENCH_OUT="$BENCH_OUT"
export VHBENCH_SETTLE="$settle"
export VHBENCH_MEASURE="$measure"
export VINTAGEHORIZONS_AUTOUNPAUSE=1   # the window is unfocused during unattended runs

"$ROOT/scripts/test-client.sh" -c "localhost:${VH_TEST_PORT:-42425}"

waypoints="$(grep -cvE '^\s*(#|$)' "$route")"
# Generous: every waypoint costs settle + measure, plus world load and teleport waits.
budget=$(( waypoints * (settle + measure + 15) + 180 ))
echo "Bench '$label': $waypoints waypoints, allowing up to ${budget}s"

if vh_wait_for "$BENCH_OUT/$label.done" "" "$budget" "$VH_SANDBOX/test-instance.pid"; then
    echo "Bench '$label': complete"
else
    echo "Bench '$label': did not finish within ${budget}s (or the client died)" >&2
fi

"$ROOT/scripts/test-stop.sh" >/dev/null 2>&1 || true

if [[ -f "$BENCH_OUT/$label.csv" ]]; then
    echo
    column -t -s, "$BENCH_OUT/$label.csv" 2>/dev/null || cat "$BENCH_OUT/$label.csv"
    echo
    echo "Screenshots: $BENCH_OUT/${label}--*.png"
else
    echo "Bench '$label': no CSV produced; check $VH_SANDBOX/Logs/client-main.log" >&2
    exit 1
fi
