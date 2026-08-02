#!/usr/bin/env bash
# The shared code for the sandbox test launchers. A script sources this file. Nothing
# executes it directly.
#
# CAUTION: The isolation rules below are safety-critical. A violation stopped the live
# game of the user one time.
#
# These rules are in ONE place on purpose. Thus nobody can correct the client launcher
# and forget the server launcher.

VH_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VH_SANDBOX="$VH_ROOT/.testdata"
VH_GAME="${VINTAGE_STORY:-$HOME/Games/vintagestory1.22.5}"

# The DOTNET_ROOT of the desktop launcher, at ~/.dotnet, is old on this machine. But a
# caller that sets it deliberately must win. This is the same contract as dev-run.sh.
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/share/dotnet}"

# True only when <pid> is alive AND its command line uses the sandbox dataPath.
#
# A live process alone is not sufficient. An instance that crashed leaves its pidfile,
# and the kernel uses a PID again. Thus an old pidfile can point at an unrelated
# process, and that process can be the game of the user.
vh_is_ours() {
    local pid="$1"
    [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]] || return 1
    [[ -r "/proc/$pid/cmdline" ]] || return 1
    tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qF -- "$VH_SANDBOX" || return 1
    return 0
}

# Refuse to start a second instance. Do this only when the pidfile points at a live
# sandbox process. Clear a pidfile that is old, or whose PID the kernel used again.
# Without that, the pidfile stops each start, forever.
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

# Keep the console output of the previous run. It is the ONLY record of a native crash,
# and of a host failure before the logger starts. The Logs/ directory of the game holds
# each message that reaches its logger, but it does not hold those two.
vh_rotate_log() {
    local log="$1"
    [[ -f "$log" ]] && mv -f "$log" "$log.prev"
    return 0
}

# Start the process detached. Record the PID. Then make sure that the process survived
# the startup.
#
# A failure in the background never triggers set -e. A pidfile that points at a dead PID
# is also exactly what causes the hazard with a reused PID.
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
        # Return, and do not exit. A caller can try again. A server that stopped just now
        # leaves its port in TIME_WAIT, and the bind fails for a few seconds.
        return 1
    fi

    echo "$label started: pid $pid"
    return 0
}

# Wait until an instance is gone, after test-stop.sh asked it to stop.
#
# test-stop.sh sends SIGTERM and polls for 10 seconds. Then it stops, and it does not
# send SIGKILL. That is the correct decision. A client in the middle of a shutdown writes
# its LOD cache, and a SIGKILL there gives a database that is half written.
#
# But a client with a few thousand sections to write regularly needs more than 10
# seconds. Then a caller that starts the next instance immediately triggers
# vh_guard_not_running, on a pidfile that is still valid.
#
# Thus this function waits. It returns 0 after the process is gone, and 1 at the
# timeout.
vh_wait_stopped() {
    local pidfile="$1" timeoutSec="${2:-90}"
    local waited=0

    while [ "$waited" -lt "$timeoutSec" ]; do
        [[ -f "$pidfile" ]] || return 0

        local pid
        pid="$(cat "$pidfile" 2>/dev/null || true)"
        if ! vh_is_ours "$pid"; then
            rm -f "$pidfile"
            return 0
        fi

        sleep 2
        waited=$((waited + 2))
    done
    return 1
}

# Wait for a marker to appear in a log. Stop when the process dies first.
#
# A crash reporter can keep a process alive long after the liveness check of vh_launch.
# Thus a live process alone does not prove a successful start.
#
# This function returns 0 for a success, and 1 for a timeout or a death.
#
# An empty marker means "wait for the file to appear". The bench harness uses that form
# to give its completion.
vh_wait_for() {
    local log="$1" marker="$2" timeoutSec="$3" pidfile="$4"
    local waited=0

    while [ "$waited" -lt "$timeoutSec" ]; do
        if [[ -z "$marker" ]]; then
            [[ -s "$log" ]] && return 0
        elif grep -qF -- "$marker" "$log" 2>/dev/null; then
            return 0
        fi
        if [[ -n "$pidfile" && -f "$pidfile" ]]; then
            local pid
            pid="$(cat "$pidfile" 2>/dev/null || true)"
            if ! vh_is_ours "$pid"; then
                return 1
            fi
        fi
        sleep 2
        waited=$((waited + 2))
    done
    return 1
}
