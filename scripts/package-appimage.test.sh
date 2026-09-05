#!/usr/bin/env bash
# Tests for the AppRun heredoc that scripts/package-appimage.sh writes into the AppDir (AC-1116).
#
# Why extract the heredoc instead of running the packaging script end to end: building a real AppImage needs a
# linux-x64 publish and appimagetool, neither available where this runs. What AC-1116 actually changed is AppRun's
# logic — copy to a versioned cache and exec from there, falling back to the mount on any failure — and that logic
# is pure shell over a filesystem, so it can be pulled out and driven directly with a stand-in "Cockpit.App" and a
# fake mount directory.
#
#   scripts/package-appimage.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_script="${script_dir}/package-appimage.sh"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  printf '  expected: %s\n' "$2" >&2
  printf '  actual:   %s\n' "$3" >&2
  failures=$((failures + 1))
}

pass() {
  printf 'ok: %s\n' "$1"
}

assert_contains() {
  case "$3" in
    *"$2"*) pass "$1" ;;
    *) fail "$1" "output containing: $2" "$3" ;;
  esac
}

apprun="${work}/AppRun.extracted"
awk '/^cat > "\$appdir\/AppRun" <<.APPRUN./{flag=1;next} /^APPRUN$/{if(flag)flag=0} flag' \
  "$source_script" >"$apprun"
if [ ! -s "$apprun" ]; then
  fail "the AppRun heredoc extracts from package-appimage.sh" "a non-empty script" "nothing (marker text moved?)"
  printf '\n%d test(s) failed\n' "$((failures + 1))" >&2
  exit 1
fi
chmod +x "$apprun"

version="9.9.9-test"
mount_dir="${work}/mnt/AI-Cockpit.AppDir"
mkdir -p "${mount_dir}/usr/bin"
printf '%s' "$version" >"${mount_dir}/VERSION"
cp "$apprun" "${mount_dir}/AppRun"
cat >"${mount_dir}/usr/bin/Cockpit.App" <<'APP'
#!/usr/bin/env bash
echo "RAN:$0"
APP
chmod +x "${mount_dir}/usr/bin/Cockpit.App" "${mount_dir}/AppRun"

home="${work}/home"
mkdir -p "$home"
export HOME="$home"
unset XDG_CACHE_HOME || true

cache_root="${home}/.cache/Cockpit"

# --- stale version present before the first run: cleanup must remove it (criterion 3) -----------------
mkdir -p "${cache_root}/0.0.1-old/usr/bin"
: >"${cache_root}/0.0.1-old/usr/bin/Cockpit.App"

output1="$("${mount_dir}/AppRun")"
assert_contains "first run execs the copy in the versioned cache, not the mount" "${cache_root}/${version}/usr/bin/Cockpit.App" "$output1"

if [ -e "${cache_root}/0.0.1-old" ]; then
  fail "a version other than the one just cached is cleaned up" "0.0.1-old removed" "0.0.1-old still present"
else
  pass "a version other than the one just cached is cleaned up"
fi

# --- second run must be a cache hit, not a re-copy (criterion 2): delete the mount's usr/ so any attempt to
# copy from it again would fail, then confirm the second run still succeeds unchanged -------------------------
rm -rf "${mount_dir}/usr"
output2="$("${mount_dir}/AppRun")"
assert_contains "a second run of the same version reuses the cache without touching the mount's usr/ again" \
  "${cache_root}/${version}/usr/bin/Cockpit.App" "$output2"

# --- copying to the cache fails outright: AppRun must still start, from the mount (criterion 4) ---------------
mount_dir2="${work}/mnt2/AI-Cockpit.AppDir"
mkdir -p "${mount_dir2}/usr/bin"
printf '%s' "${version}-fallback" >"${mount_dir2}/VERSION"
cp "$apprun" "${mount_dir2}/AppRun"
cat >"${mount_dir2}/usr/bin/Cockpit.App" <<'APP'
#!/usr/bin/env bash
echo "RAN:$0"
APP
chmod +x "${mount_dir2}/usr/bin/Cockpit.App" "${mount_dir2}/AppRun"

# A plain file where the cache root should be a directory makes every mkdir -p under it fail, without relying on
# chmod semantics that differ between POSIX and this MSYS shell.
rm -rf "$cache_root"
: >"$cache_root"

output3="$("${mount_dir2}/AppRun")"
assert_contains "a cache the AppRun cannot write to falls back to running from the mount" \
  "${mount_dir2}/usr/bin/Cockpit.App" "$output3"

# --- a half-written cache_dir from a previous interrupted run must heal itself, not fall back forever ---------
version3="9.9.9-test-heal"
mount_dir3="${work}/mnt3/AI-Cockpit.AppDir"
mkdir -p "${mount_dir3}/usr/bin"
printf '%s' "$version3" >"${mount_dir3}/VERSION"
cp "$apprun" "${mount_dir3}/AppRun"
cat >"${mount_dir3}/usr/bin/Cockpit.App" <<'APP'
#!/usr/bin/env bash
echo "RAN:$0"
APP
chmod +x "${mount_dir3}/usr/bin/Cockpit.App" "${mount_dir3}/AppRun"

rm -rf "$cache_root"
mkdir -p "${cache_root}/${version3}/usr/bin"
: >"${cache_root}/${version3}/usr/bin/Cockpit.App"  # present but not executable: a half-written cache_dir

output4="$("${mount_dir3}/AppRun")"
assert_contains "a half-written cache_dir heals on the next start instead of falling back to the mount forever" \
  "${cache_root}/${version3}/usr/bin/Cockpit.App" "$output4"

if [ "$failures" -ne 0 ]; then
  printf '\n%d test(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall tests passed\n'
