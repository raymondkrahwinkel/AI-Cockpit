#!/usr/bin/env bash
# A plugin whose shipped files changed must also carry a new version in its manifest — otherwise the change
# cannot reach anybody. The store decides "is there an update?" by comparing version strings
# (StorePluginRowViewModel: UpdateAvailable => PluginVersion.IsNewer(latest, installed)), so code that lands
# under an unchanged number is silently unpublishable: the publish workflow would upload a zip the store then
# refuses to offer, and every installed copy keeps running the old dll for good.
#
# This is not hypothetical. AC-276 changed three files in Cockpit.Plugin.ClaudeProvider on 2026-07-30 and left
# the version at 0.12.0, the number published the day before. The result was two different dlls both calling
# themselves 0.12.0 and a fix that reached no one — while the store index reported every plugin "up to date",
# because by its own measure it was.
#
# Markdown is excluded: a README is not in the zip and changing it changes nothing for a user. Everything else
# under the plugin folder is, including plugin.json's own description, which the store shows.
#
#   scripts/check-plugin-versions.sh <base-ref> [head-ref]
set -euo pipefail

base="${1:?usage: check-plugin-versions.sh <base-ref> [head-ref]}"
head="${2:-HEAD}"

# Git Bash can misread a Windows worktree's gitdir as a converted path. Do not
# let `set -e` turn that into a quiet, unlabelled exit: a guard that did not run
# is a failed guard, never a clean one.
git_or_fail() {
  local output status
  output="$(git "$@" 2>&1)" || {
    status=$?
    printf 'FAIL: check-plugin-versions.sh could not run git %q (exit %s).\n' "$1" "$status" >&2
    printf 'The plugin version guard did not run; fix the Git/worktree setup and retry.\n' >&2
    [ -z "$output" ] || printf '%s\n' "$output" >&2
    return "$status"
  }
  printf '%s' "$output"
}

git_or_fail rev-parse --is-inside-work-tree >/dev/null || exit 2

# The version as the manifest carries it at a given commit, or empty when the plugin did not exist there.
# Deliberately grep/sed rather than jq: this runs on developer machines too, and a guard that needs a tool
# the developer lacks is a guard that gets skipped. "version" is matched with its opening quote, so
# "abstractionsVersion" and "minHostVersion" cannot be mistaken for it.
# The trailing `|| true` is not decoration. Under `pipefail` a grep that matches nothing fails the whole
# pipeline, and an unguarded failure here would abort the run with `set -e` on the first plugin that does not
# exist at the base ref — reporting nothing at all, which reads exactly like "everything is fine".
manifest_version() {
  local ref="$1" path="$2" json
  json="$(git show "${ref}:${path}" 2>/dev/null || true)"
  printf '%s\n' "$json" \
    | grep -oE '"version"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -1 \
    | sed -E 's/.*"([^"]*)"$/\1/' \
    || true
}

# True when $1 sorts strictly after $2. A bump has to go up: a number that moves down or sideways leaves
# every installed copy thinking it is already current, which is the same dead end as not bumping at all.
version_gt() {
  [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

# Collected as newline-separated text rather than an array on purpose: expanding an empty array under
# `set -u` is an error in bash 3.2, which is still what a stock macOS ships, and a guard that dies on the
# machine it is meant to protect is worse than none.
offenders=''

changed_files="$(git_or_fail diff --name-only "$base" "$head" -- plugins-dev ':!*.md')" || exit 2
case $'\n'"$changed_files"$'\n' in *$'\nplugins-dev/_shared/'*) shared_changed=1;; *) shared_changed=0;; esac

for dir in plugins-dev/Cockpit.Plugin.*/; do
  dir="${dir%/}"
  case "$dir" in *.Tests) continue;; esac

  manifest="${dir}/plugin.json"
  [ -f "$manifest" ] || continue

  # Test projects ship nothing, so their changes are none of this guard's business — and they need no filter
  # of their own: a directory pathspec matches on "<dir>/", so Cockpit.Plugin.Foo never picks up
  # Cockpit.Plugin.Foo.Tests (verified, not assumed). An extra grep here would look like protection and catch
  # nothing, which is worse than no guard. Markdown is excluded because a README is not in the zip.
  changed=''
  while IFS= read -r path; do
    case "$path" in "$dir"/*) changed=1; break;; esac
  done <<<"$changed_files"

  # A plugin that links shared source (plugins-dev/_shared, AC-964) ships that code inside its own dll, so a
  # change there changes what it publishes just as surely as a change in its own folder — and the pathspec above
  # cannot see it. Only the plugins whose project actually names the file are affected.
  if [ "$shared_changed" -eq 1 ] && grep -q '_shared' "$dir"/*.csproj 2>/dev/null; then
    changed=1
  fi

  [ -n "$changed" ] || continue

  before="$(manifest_version "$base" "$manifest")" || exit 2
  after="$(manifest_version "$head" "$manifest")" || exit 2

  # A plugin that did not exist on the base ref is a first release; there is nothing to bump past.
  [ -n "$before" ] || continue

  if [ "$before" = "$after" ]; then
    offenders="${offenders}${dir}|${after}|unchanged"$'\n'
  elif ! version_gt "$after" "$before"; then
    offenders="${offenders}${dir}|${before} -> ${after}|not newer"$'\n'
  fi
done

if [ -z "$offenders" ]; then
  printf 'ok: every plugin with changed files carries a newer version.\n'
  exit 0
fi

printf 'These plugins changed but cannot be published:\n\n' >&2
while IFS='|' read -r dir version reason; do
  [ -n "$dir" ] || continue
  printf '  %-46s %-22s (%s)\n' "$dir" "$version" "$reason" >&2
done <<<"$offenders"
printf '\nRaise "version" in each plugin.json: patch for a fix, minor for new visible behaviour.\n' >&2
printf 'A released number is immutable — correct forward, never sideways.\n' >&2
exit 1
