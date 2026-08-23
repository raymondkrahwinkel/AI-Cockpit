#!/usr/bin/env bash
# A plugin whose plugin.json declares an abstractionsVersion that does not match
# AbstractionsContract.Version loads on nobody's host: PluginLoadPolicy refuses it outright, silently, with
# no build error and no red check (AC-1039 — Discord and Slack merged this way and were about to publish).
#
# Runs over every plugin in plugins-dev/ and templates/, not just changed ones: the plugin AC-1039 caught
# was not touched by the pull request that broke it. templates/ is included because a scaffold that carries
# the wrong number multiplies the bug into every plugin scaffolded from it.
#
#   scripts/check-plugin-abstractions-version.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
contract="${repo_root}/src/Cockpit.Plugins.Abstractions/AbstractionsContract.cs"

expected="$(grep -oE 'public const int Version = [0-9]+;' "$contract" | grep -oE '[0-9]+')"
[ -n "$expected" ] || { printf 'could not read AbstractionsContract.Version from %s\n' "$contract" >&2; exit 1; }

# The opening quote is part of the pattern, so "abstractionsVersion" cannot be mistaken for "minHostVersion"
# or any other field — same trick check-plugin-versions.sh uses for "version" against "abstractionsVersion".
# Deliberately matches only a numeric value: a missing field or a non-numeric one both come back empty, and
# the caller reports that as "missing" rather than silently skipping it.
manifest_abstractions_version() {
  grep -oE '"abstractionsVersion"[[:space:]]*:[[:space:]]*[0-9]+' "$1" \
    | head -1 \
    | grep -oE '[0-9]+$' \
    || true
}

offenders=''

for dir in "${repo_root}"/plugins-dev/Cockpit.Plugin.*/ "${repo_root}"/templates/*/; do
  dir="${dir%/}"
  case "$dir" in *.Tests) continue;; esac

  manifest="${dir}/plugin.json"
  [ -f "$manifest" ] || continue

  actual="$(manifest_abstractions_version "$manifest")"

  if [ -z "$actual" ]; then
    offenders="${offenders}$(basename "$dir")|missing or non-numeric"$'\n'
  elif [ "$actual" != "$expected" ]; then
    offenders="${offenders}$(basename "$dir")|${actual}"$'\n'
  fi
done

if [ -z "$offenders" ]; then
  printf 'ok: every plugin declares abstractionsVersion %s.\n' "$expected"
  exit 0
fi

printf 'These plugins declare an abstractionsVersion that does not match AbstractionsContract.Version (%s):\n\n' "$expected" >&2
while IFS='|' read -r name actual; do
  [ -n "$name" ] || continue
  printf '  %-40s abstractionsVersion: %s, expected: %s\n' "$name" "$actual" "$expected" >&2
done <<<"$offenders"
printf '\nA plugin whose abstractionsVersion does not match the host contract is refused at load. Update plugin.json.\n' >&2
exit 1
