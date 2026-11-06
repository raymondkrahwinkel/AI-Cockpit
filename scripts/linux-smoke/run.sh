#!/usr/bin/env bash
# The Linux smoke test (AC-2065), journey "LinuxSmoke": starts the real AppImage on a virtual screen with no GPU,
# opens every surface of the shipped plugins once and, after each step, checks that the process is alive, that no
# crash dump or crash.log appeared, and that the screen is neither blank nor unchanged. One screenshot per step.
# Runs on any Linux with Xvfb, xdotool and ImageMagick: the container scripts/linux-smoke.sh builds, or a VM.
#
#   run.sh <AppImage> <staged-browser-plugin-dir> <out-dir>
#
# Input goes to the Xvfb display only (xdotool); nothing on the machine's own screen is touched. Coordinates belong
# to the fixed 1400x900 display, where the window opens at a fixed place.
set -uo pipefail

appimage="$(readlink -f "$1")"; plugin="$(readlink -f "$2")"; out="$(mkdir -p "$3" && readlink -f "$3")"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# State, home and dumps live in a scratch directory, not in <out-dir>: the browser engine unpacks to 1.6 GB, and a
# mounted volume can be many times slower than local disk. Only what a person reads (screenshots, logs) goes to <out-dir>.
work="$(mktemp -d)"; state="$work/state"; home="$work/home"; dumps="$work/dumps"; shots="$out/shots"
rm -rf "$shots"; mkdir -p "$state" "$home" "$dumps" "$shots"
python3 "$here/seed-state.py" "$state" "$plugin" || exit 2

export DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 COCKPIT_STATE_ROOT="$state" HOME="$home" APPIMAGE_EXTRACT_AND_RUN=1
# A .NET crash writes its dump here (createdump ships in the AppImage, AC-989).
export DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpName="$dumps/%p.dmp"
engine_wait="${SMOKE_ENGINE_WAIT:-420}"

Xvfb :99 -screen 0 1400x900x24 >"$out/xvfb.log" 2>&1 & xvfb=$!
cleanup() {
    [ -n "${launcher:-}" ] && _stop_tree "$launcher"
    kill "$xvfb" 2>/dev/null
    cp "$state"/logs/* "$out/" 2>/dev/null; cp "$state/browser-runtime/cef.log" "$out/" 2>/dev/null
    rm -rf "$work"
}
trap cleanup EXIT
sleep 2

_tree() { local p; echo "$1"; for p in $(pgrep -P "$1" 2>/dev/null); do _tree "$p"; done; }
# Only the processes this script started: SIGTERM, then a bounded wait, then SIGKILL (SIGTERM can hang a stop).
_stop_tree() { local pids; pids="$(_tree "$1")"; kill $pids 2>/dev/null; for _ in $(seq 1 20); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.5; done; kill -9 $pids 2>/dev/null; }
_app_pid() { local p; for p in $(_tree "$launcher"); do [ "$(cat /proc/$p/comm 2>/dev/null)" = "Cockpit.App" ] && { echo "$p"; return 0; }; done; return 1; }
_crash_files() { find "$state" "$home" "$dumps" \( -name 'crash.log' -o -name '*.dmp' -o -name 'core' -o -name 'core.*' -o -name 'hs_err*' \) 2>/dev/null | sort; }
_stat() { convert "$1" -crop "$2" +repage -colorspace Gray -format "%[fx:$3]" info:; }

"$appimage" >"$out/app.log" 2>&1 & launcher=$!
failures=0; n=0; prev=""; started=$SECONDS; : >"$out/summary.txt"
baseline="$(_crash_files)"

_fail() { failures=$((failures + 1)); echo "FAIL  $1" | tee -a "$out/summary.txt"; }

# step <name> <settle-seconds> <action...>: act, wait, then verify. A blank or unchanged screen is a failure even
# when the process lives: an empty or half-built tab is what this test exists to catch.
step() {
    local name="$1" settle="$2"; shift 2
    n=$((n + 1)); local file="$shots/$(printf '%02d' $n)-$name.png" t0=$SECONDS before=$failures
    "$@"; sleep "$settle"
    import -window root "$file"
    local pid; pid="$(_app_pid)" || pid=""
    if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then _fail "$name: the process is gone"; fi
    if [ "$(_crash_files)" != "$baseline" ]; then _fail "$name: new crash artefact: $(comm -13 <(echo "$baseline") <(_crash_files) | tr '\n' ' ')"; fi
    if [ "$(_stat "$file" 1030x690+256+140 'standard_deviation > 0.005 ? 1 : 0')" != 1 ]; then _fail "$name: the surface is blank"; fi
    if [ -n "$prev" ] && [ "$(compare -metric AE -fuzz 8% "$prev" "$file" null: 2>&1)" -lt 400 ]; then _fail "$name: the screen did not change"; fi
    prev="$file"
    [ $failures = $before ] && echo "ok    $name ($((SECONDS - t0))s)" | tee -a "$out/summary.txt"
    [ -n "$pid" ] || { echo "Stopping: no process left to test."; finish; }
}
# clicks <x y>...: one click per pair. The pointer arrives first and rests a moment (a click straight after the move is
# sometimes lost), and the pairs are a second apart so a menu or dialog is up before the next one.
clicks() { while [ $# -ge 2 ]; do xdotool mousemove "$1" "$2"; sleep 0.4; xdotool click 1; shift 2; [ $# -ge 2 ] && sleep 1; done; }
# Until the page is painted: the Browser opens on a white page and the app is dark, so a bright content area is the
# engine drawing (in the rail panel or the tab; the strip checked is white in both). The first run downloads the engine (~326 MB), hence the long bound.
browser_painted() {
    local name="$1"; shift; "$@"; local waited=0
    while [ $waited -lt "$engine_wait" ]; do
        import -window root "$out/poll.png"
        [ "$(_stat "$out/poll.png" 320x500+960+300 'mean > 0.6 ? 1 : 0')" = 1 ] && return 0
        _app_pid >/dev/null || return 0
        sleep 5; waited=$((waited + 5))
    done
    _fail "$name: nothing was painted within ${engine_wait}s"
}

finish() {
    echo "total $((SECONDS - started))s, $n steps, $failures failures" | tee -a "$out/summary.txt"
    [ $failures = 0 ]; exit $?
}

# The main window is up when the blue "New session" button is drawn; before that the splash is showing.
main_window_up() {
    local waited=0
    while [ $waited -lt 180 ]; do
        import -window root "$out/poll.png"
        [ "$(convert "$out/poll.png" -crop 1x1+100+137 +repage -format '%[fx:b > 0.7 && r < 0.4 ? 1 : 0]' info:)" = 1 ] && return 0
        sleep 2; waited=$((waited + 2))
    done
}
step start 3 main_window_up
# Each step first undoes what the previous one left open (a dialog, a rail panel), so one surface is open at a time.
step depot-inbox 3 clicks 144 601
step schedules 3 clicks 1177 135 139 631
step rail-notifications 3 clicks 1278 90 1320 150
step rail-agents 3 clicks 1320 150 1320 240
step rail-depot 3 clicks 1320 240 1320 400
step depot-settings 3 clicks 1320 400 1090 116
step browser-settings 3 clicks 329 669
step new-terminal 6 clicks 881 760 213 137 150 198
step new-session-dialog 3 clicks 100 137

step rail-browser 5 browser_painted rail-browser clicks 822 586 1320 320
step workspace-browser 5 browser_painted workspace-browser clicks 1320 320 1275 116 1132 208
finish
