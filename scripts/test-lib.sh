#!/usr/bin/env bash
# Shared plumbing for the sandbox test launchers. Sourced, never executed.
#
# The isolation rules below are safety-critical (a violation once crashed the
# user's live game); keeping them in ONE place is deliberate, so a fix can't be
# applied to the client launcher and forgotten in the server launcher.

VH_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VH_SANDBOX="$VH_ROOT/.testdata"
VH_GAME="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.3}"

# The desktop launcher's DOTNET_ROOT (~/.dotnet) is stale on this machine, but a
# caller who sets it deliberately must win — same contract as dev-run.sh.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

# True only if <pid> is live AND its command line runs against our sandbox
# dataPath. Liveness alone is not enough: a crashed instance leaves its pidfile
# behind and the kernel recycles PIDs, so a stale pidfile can point at an
# unrelated process — possibly the user's own game.
vh_is_ours() {
    local pid="$1"
    [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 1
    [[ -r "/proc/$pid/cmdline" ]] || return 1
    tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qF -- "$VH_SANDBOX" || return 1
    return 0
}

# Refuse to start a second instance, but only when the pidfile really points at
# a live sandbox process; a stale/recycled pidfile is cleared instead of
# blocking startup forever.
vh_guard_not_running() {
    local label="$1" pidfile="$2"
    [[ -f "$pidfile" ]] || return 0

    local pid
    pid="$(cat "$pidfile" 2>/dev/null || true)"
    if vh_is_ours "$pid"; then
        echo "$label already running (pid $pid). Stop it with scripts/test-stop.sh first." >&2
        exit 1
    fi

    rm -f "$pidfile"
    return 0
}

# Keep one generation of console output: it is the ONLY record of native crashes
# and pre-logger host failures (the game's own Logs/ archive covers everything
# that reaches its logger, but not those).
vh_rotate_log() {
    local log="$1"
    [[ -f "$log" ]] && mv -f "$log" "$log.prev"
    return 0
}

# Launch detached, record the PID, then confirm it survived startup — a
# backgrounded failure never trips set -e, and a pidfile pointing at a dead PID
# is exactly what feeds the recycled-PID hazard.
vh_launch() {
    local label="$1" pidfile="$2" log="$3"
    shift 3

    vh_rotate_log "$log"
    cd "$VH_GAME"
    "$@" > "$log" 2>&1 &
    local pid=$!
    echo "$pid" > "$pidfile"

    sleep 2
    if ! kill -0 "$pid" 2>/dev/null; then
        rm -f "$pidfile"
        echo "$label failed to start — last lines of $log:" >&2
        tail -n 15 "$log" >&2 || true
        exit 1
    fi

    echo "$label started: pid $pid"
}
