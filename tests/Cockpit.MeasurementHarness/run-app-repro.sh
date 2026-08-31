#!/usr/bin/env bash
# The Linux counterpart of run-app-repro.ps1 (AC-1104): drives the real Debug app -- real sessions, real
# transcripts, real rendering -- through the in-app repro trigger, and reads the app's own freeze instruments
# afterwards. There is no pwsh on the Fedora machine, so the PowerShell runner cannot be used there at all.
#
# It starts only a Debug build whose PID it records, gives that process its own COCKPIT_STATE_ROOT and TMPDIR,
# validates the host-ready PID/state-root marker before writing the trigger, and stops only that process.
# It never touches a running production or nightly cockpit.
set -euo pipefail

exe=""; label="run"; sessions=8; seconds=60; shape="new-rows"; width=900; height=520; docked=false
out="${TMPDIR:-/tmp}/cockpit-app-repro"

while [ $# -gt 0 ]; do
    case "$1" in
        --exe) exe="$2"; shift 2;;
        --label) label="$2"; shift 2;;
        --sessions) sessions="$2"; shift 2;;
        --seconds) seconds="$2"; shift 2;;
        --shape) shape="$2"; shift 2;;
        --width) width="$2"; shift 2;;
        --height) height="$2"; shift 2;;
        --out) out="$2"; shift 2;;
        # The docked assistant chat is a second follower in the same visual tree (AC-1178 §3).
        --assistant-docked) docked=true; shift;;
        # An unknown flag is refused rather than ignored, like the harness itself (README, E5).
        *) echo "unknown flag: $1" >&2; exit 2;;
    esac
done

[ -x "$exe" ] || { echo "--exe must point at a built Debug Cockpit.App" >&2; exit 2; }

run="$out/$label"
work="$run/work"; state="$run/state"
rm -rf "$run"; mkdir -p "$work" "$state"

# Focus+rail is the arrangement under test (AC-1178 §5.3); the window size decides which rail tile ends up
# outside the visible area, so it is seeded rather than left to whatever the platform picks.
cat > "$state/cockpit.json" <<JSON
{
  "Layout": {
    "FocusRailLayout": true, "SingleSessionLayout": false, "StackSessionsVertically": false,
    "AssistantDocked": $docked, "OpenDockPanelId": $(if [ "$docked" = true ]; then echo '"assistant"'; else echo null; fi)
  },
  "Debug": { "LogDiagnosticSnapshots": true, "ShowDebugControls": false },
  "WindowBounds": { "main": { "X": 60, "Y": 60, "Width": $width, "Height": $height, "IsMaximized": false } }
}
JSON

wait_for_json() { # path, timeout seconds, what
    local deadline=$(( $(date +%s) + $2 ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if [ -s "$1" ] && python3 -c "import json,sys;json.load(open(sys.argv[1]))" "$1" >/dev/null 2>&1; then
            return 0
        fi
        sleep 0.25
    done
    echo "timeout waiting for $3: $1" >&2
    return 1
}

json_field() { python3 -c "import json,sys;print(json.load(open(sys.argv[1])).get(sys.argv[2],''))" "$1" "$2"; }
cpu_seconds() { # pid -> utime+stime in seconds
    awk '{print ($14 + $15) / '"$(getconf CLK_TCK)"'}' "/proc/$1/stat" 2>/dev/null || echo ""
}

echo "uptime at start: $(uptime)"
env COCKPIT_MEASUREMENT_HARNESS=1 COCKPIT_LEAKSIM=1 COCKPIT_MEASUREMENT_ROOT="$work" \
    COCKPIT_STATE_ROOT="$state" TMPDIR="$work" TEMP="$work" TMP="$work" \
    "$exe" > "$run/stdout.log" 2>&1 &
pid=$!
echo "started pid=$pid label=$label sessions=$sessions ${width}x${height} shape=$shape assistantDocked=$docked"

cleanup() {
    if kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid" 2>/dev/null || true
        for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
        kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null || true
    fi
}
trap cleanup EXIT

# The host-ready marker is the positive control on the runner itself: it names the PID and the state root, so a
# run against somebody else's cockpit refuses here instead of reporting numbers from the wrong process.
wait_for_json "$work/measurement-host.ready.json" 120 "host-ready marker"
[ "$(json_field "$work/measurement-host.ready.json" pid)" = "$pid" ] || { echo "host-ready PID mismatch" >&2; exit 3; }
[ "$(json_field "$work/measurement-host.ready.json" stateRoot)" = "$state" ] || { echo "host-ready state-root mismatch" >&2; exit 3; }

cpu_before="$(cpu_seconds "$pid")"
printf 'apprepro:%s,%s,%s' "$sessions" "$seconds" "$shape" > "$work/cockpit-leaksim.trigger"

wait_for_json "$work/app-repro.ready.json" 180 "sessions to start"
started="$(json_field "$work/app-repro.ready.json" started)"
[ "$started" = "$sessions" ] || { echo "started $started of $sessions sessions" >&2; exit 3; }
echo "sessions started: $started"

# A frozen UI thread never finishes the stream, so the missing completion marker is itself an observation --
# that is how AC-1178's dev-app rounds were caught after three of them were first read as clean zeros.
deadline=$(( $(date +%s) + seconds + 45 ))
: > "$run/cpu.csv"
while [ "$(date +%s)" -lt "$deadline" ]; do
    echo "$(date +%s),$(cpu_seconds "$pid")" >> "$run/cpu.csv"
    if [ -s "$work/app-repro.done.json" ]; then break; fi
    if ! kill -0 "$pid" 2>/dev/null; then echo "the app exited during the run" >&2; break; fi
    sleep 1
done
cpu_after="$(cpu_seconds "$pid")"
completed=no; if [ -s "$work/app-repro.done.json" ]; then completed=yes; fi

log="$state/logs/cockpit.log"
count() { if [ -f "$log" ]; then grep -c "$1" "$log" || true; else echo 0; fi; }
loops_file="$state/logs/layout-loops.log"

echo "--- $label ---"
echo "completed stream        : $completed"
echo "uifreeze hang           : $(count 'uifreeze hang')"
echo "uifreeze recovered      : $(count 'uifreeze recovered')"
echo "renderclock stalled     : $(count 'renderclock stalled')"
echo "renderclock resumed     : $(count 'renderclock resumed')"
echo "Infinite layout loop    : $(count 'Infinite layout loop detected')"
# AC-1263: the run has to name the bound it ran under, or a cut and a freeze read as the same run afterwards.
echo "layout cut-off N        : ${COCKPIT_LAYOUT_CUTOFF_SAMPLES:-3 (default)}"
echo "subtrees cut off        : $(count 'layout loop cut off after')"
echo "layout-loops.log lines  : $(if [ -f "$loops_file" ]; then wc -l < "$loops_file"; else echo 0; fi)"
echo "process CPU during run  : ${cpu_before}s -> ${cpu_after}s"
echo "log                     : $log"
echo "uptime at end: $(uptime)"
