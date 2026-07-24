#!/usr/bin/env bash
set -euo pipefail

# Stop sandbox test instances — ONLY via their own pidfiles, and ONLY after
# confirming the process really is our sandbox instance.
#
# Two hard rules, both learned the hard way:
#  - Never kill by process name or argument pattern: the user runs their personal
#    game concurrently and pgrep matching has burned us before.
#  - Never trust a pidfile on liveness alone. A crashed instance leaves its
#    pidfile behind, and the kernel recycles PIDs — the recycled PID could be
#    the user's own game. is_ours() checks /proc/<pid>/cmdline for the sandbox
#    dataPath before any signal is sent.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$ROOT/.testdata"

# True only if <pid> is a live process whose command line runs the game against
# our sandbox dataPath. Anything else — dead, recycled, or the user's own game —
# is refused.
is_ours() {
    local pid="$1"
    [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 1
    [[ -r "/proc/$pid/cmdline" ]] || return 1
    tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qF -- "$SANDBOX" || return 1
    return 0
}

failed=0

stop_one() {
    local label="$1" pidfile="$2"
    if [[ ! -f "$pidfile" ]]; then
        echo "$label: no pidfile, nothing to stop"
        return 0
    fi

    local pid
    pid="$(cat "$pidfile" 2>/dev/null || true)"

    if ! is_ours "$pid"; then
        if kill -0 "$pid" 2>/dev/null; then
            echo "$label: pid $pid is NOT a sandbox process (recycled PID?) — refusing to signal it; clearing stale pidfile" >&2
        else
            echo "$label: pid $pid already gone"
        fi
        rm -f "$pidfile"
        return 0
    fi

    kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do
        is_ours "$pid" || break
        sleep 0.5
    done

    if is_ours "$pid"; then
        echo "$label: pid $pid did not exit after 10s; NOT force-killing — pidfile kept, check it manually" >&2
        failed=1
        return 0
    fi

    echo "$label: pid $pid stopped"
    rm -f "$pidfile"
    return 0
}

case "${1:-all}" in
    client) stop_one "test client" "$SANDBOX/test-instance.pid" ;;
    server) stop_one "test server" "$SANDBOX/server/server.pid" ;;
    all)
        stop_one "test client" "$SANDBOX/test-instance.pid"
        stop_one "test server" "$SANDBOX/server/server.pid"
        ;;
    *)
        echo "usage: test-stop.sh [client|server|all]" >&2
        exit 2
        ;;
esac

exit "$failed"
