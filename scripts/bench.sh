#!/usr/bin/env bash
set -euo pipefail

# Run one benchmark configuration end to end, in the sandbox, and collect the results.
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
# The label names the configuration under test, and it appears in each output filename.
# Thus the results of different mods are together in one directory:
#   .testdata/bench/<label>.csv        the frame timings for each waypoint
#   .testdata/bench/<label>--<wp>.png  one screenshot for each waypoint
#
# CAUTION: A comparison has a meaning only when the world, the route, the settle time and
# the measure time are identical across the runs. A change to one of those makes the
# earlier results impossible to compare. The harness mod fixes the time of day, the
# weather and the camera angles, for the same reason.
#
# Server-side mods: Farseer and ChunkLOD are 'Universal', and a server needs them on both
# sides. Thus they need --server-mods and --mods. VintageHorizons never needs
# --server-mods.

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

# Start from a known set of mods each time. A mod from the previous configuration becomes
# part of the measurement of the next one, and it gives no message.
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

# The harness itself is always on the client, whatever mod is under test.
BENCH_MOD="$ROOT/bench/VintageHorizonsBench/bin/Debug/net10.0/Mods/vintagehorizonsbench"
[[ -d "$BENCH_MOD" ]] || { echo "bench.sh: build the harness first (dotnet build bench/VintageHorizonsBench)" >&2; exit 2; }
cp -r "$BENCH_MOD" "$CLIENT_MODS/"

# Set the detail distance first, when this run benchmarks this mod at a given value.
if [[ -n "$detail" ]]; then
    mkdir -p "$VH_SANDBOX/ModConfig"
    printf '{\n  "FarViewDistanceCap": 0,\n  "DetailDistance": %s\n}\n' "$detail" \
        > "$VH_SANDBOX/ModConfig/vintagehorizons.json"
    echo "  detail distance pinned to $detail"
fi

# Remove the limit on the frame rate. With vsync on, each configuration reports the
# refresh rate of the monitor as its average, and the comparison gives nothing. Only the
# 1% low values differ. This goes into the sandbox settings, thus it applies to each mod
# under test.
#
# The flag --watch turns vsync on again, thus a person can watch the run comfortably.
# Hundreds of frames each second, with no limit, do not present correctly on a compositor,
# and the window looks old or empty.
#
# CAUTION: The numbers from a watch run are NOT comparable with the numbers from a
# measured run. This script labels them, thus they stay out of the comparison.
python3 - "$VH_SANDBOX/clientsettings.json" "$watch" <<'PY'
import json, os, sys
path, watch = sys.argv[1], sys.argv[2] == "1"
# This file exists only after the client ran one time or more in this sandbox.
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
# This timeout is large. Each waypoint costs the settle time and the measure time. The
# world load and the waits for a teleport come on top of that.
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
