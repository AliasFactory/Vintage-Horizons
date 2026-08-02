#!/usr/bin/env bash
set -uo pipefail

# The full test regimen. Run this before you commit.
#
#   scripts/check.sh              all three tiers, in order (~25 min)
#   scripts/check.sh fast         pure logic and static assets, no game (~30 s)
#   scripts/check.sh smoke        one end-to-end sandbox run (~5 min)
#   scripts/check.sh matrix       install combinations and admin controls (~20 min)
#
# There is no CI, and there cannot be one. A build of this repository needs the Vintage
# Story assemblies from a local game installation, and Anego Studios does not permit
# redistribution of them.
#
# Thus this script is the only safety net. Nothing else examines any of this again.
#
# The tiers run in order, with the cheapest first, and the script stops at the first
# failure. Thus a build that does not compile costs thirty seconds, and not half an
# hour.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

TIER="${1:-all}"
case "$TIER" in
    all|fast|smoke|matrix) ;;
    *) echo "usage: $(basename "$0") [all|fast|smoke|matrix]" >&2; exit 2 ;;
esac
shift || true

started=$SECONDS

rule() { printf '\n\033[1m%s\033[0m\n' "$1"; }

build() {
    local project="$1" label="$2"
    if dotnet build "$project" -v quiet --nologo >/dev/null 2>&1; then
        echo "  build $label: ok"
        return 0
    fi
    echo "  build $label: FAILED"
    dotnet build "$project" --nologo 2>&1 | grep -E "error" | head -20
    return 1
}

run_fast() {
    rule "fast - pure logic and static assets"

    # Nothing else makes sure that both projects still compile. There is no CI to find
    # that. The bench harness is easy to break with no notice, because no normal workflow
    # uses it.
    build "$ROOT/VintageHorizons" "mod" || return 1
    build "$ROOT/bench/VintageHorizonsBench" "bench" || return 1

    # Do not use --nologo here. `dotnet run` sends it to the program, and it does not use
    # it. Then the program reads it as a suite filter.
    #
    # Program.cs ignores an argument that starts with a dash, for exactly that reason. But
    # it is better to send no such argument than to depend on that guard.
    dotnet run --project "$ROOT/tests/VintageHorizons.Checks" -v quiet -- "$@"
}

run_smoke()  { rule "smoke - one end-to-end sandbox run";        "$ROOT/scripts/check-smoke.sh" "$@"; }
run_matrix() { rule "matrix - install combinations and controls"; "$ROOT/scripts/check-matrix.sh" "$@"; }

case "$TIER" in
    fast)   run_fast "$@";   status=$? ;;
    smoke)  run_smoke "$@";  status=$? ;;
    matrix) run_matrix "$@"; status=$? ;;
    all)
        status=0
        run_fast   || status=$?
        [[ $status -eq 0 ]] && { run_smoke  || status=$?; }
        [[ $status -eq 0 ]] && { run_matrix || status=$?; }
        ;;
esac

elapsed=$((SECONDS - started))
printf '\n'
if [[ $status -eq 0 ]]; then
    printf '\033[32m  all checks passed\033[0m (%dm%02ds)\n\n' $((elapsed / 60)) $((elapsed % 60))
else
    printf '\033[31m  CHECKS FAILED\033[0m (%dm%02ds)\n\n' $((elapsed / 60)) $((elapsed % 60))
fi
exit $status
