#!/usr/bin/env bash
set -uo pipefail

# The standing test regimen. Run this before committing.
#
#   scripts/check.sh              all three tiers, in order (~25 min)
#   scripts/check.sh fast         pure logic and static assets, no game (~30 s)
#   scripts/check.sh smoke        one end-to-end sandbox run (~5 min)
#   scripts/check.sh matrix       install combinations and admin controls (~20 min)
#
# There is no CI, and there cannot be: building this repo requires the Vintage Story
# assemblies from a local game install, which are not redistributable. So this script
# is the whole safety net — nothing else re-checks any of it.
#
# Tiers run in order and stop at the first failure, cheapest first, so a broken build
# costs thirty seconds rather than half an hour.

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
    rule "fast — pure logic and static assets"

    # Nothing else enforces that either project still compiles; there is no CI to catch
    # it, and the bench harness in particular is easy to break without noticing because
    # no normal workflow touches it.
    build "$ROOT/VintageHorizons" "mod" || return 1
    build "$ROOT/bench/VintageHorizonsBench" "bench" || return 1

    # No --nologo here: dotnet run forwards it to the program rather than consuming it,
    # where it would be read as a suite filter. Program.cs ignores dash-prefixed args for
    # exactly that reason, but not handing it one is better than relying on the catch.
    dotnet run --project "$ROOT/tests/VintageHorizons.Checks" -v quiet -- "$@"
}

run_smoke()  { rule "smoke — one end-to-end sandbox run";        "$ROOT/scripts/check-smoke.sh" "$@"; }
run_matrix() { rule "matrix — install combinations and controls"; "$ROOT/scripts/check-matrix.sh" "$@"; }

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
