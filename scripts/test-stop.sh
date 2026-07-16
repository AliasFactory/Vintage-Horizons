#!/usr/bin/env bash
set -euo pipefail

# Stop sandbox test instances — ONLY via their own pidfiles. This script must
# never kill by process name or argument pattern: the user runs their personal
# game concurrently and pgrep matching has burned us before.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

stop_one() {
    local label="$1" pidfile="$2"
    if [[ ! -f "$pidfile" ]]; then
        echo "$label: no pidfile, nothing to stop"
        return
    fi
    local pid
    pid="$(cat "$pidfile")"
    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid"
        for _ in $(seq 1 20); do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.5
        done
        if kill -0 "$pid" 2>/dev/null; then
            echo "$label: pid $pid did not exit after 10s; NOT force-killing — pidfile kept, check it manually" >&2
            return
        fi
        echo "$label: pid $pid stopped"
    else
        echo "$label: pid $pid already gone"
    fi
    rm -f "$pidfile"
}

stop_one "test client" "$ROOT/.testdata/test-instance.pid"
stop_one "test server" "$ROOT/.testdata/server/server.pid"
