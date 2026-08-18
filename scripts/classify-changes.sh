#!/usr/bin/env bash

# Classifies a diff into src/plugins_dev/other flags for ci.yml's job-level `if:` skips (see the `changes`
# job in ci.yml for why skipping happens that way and not via the `on:` trigger). `other` is the safe
# default: anything unrecognized forces every downstream job to run, same as a src/ change would.
#
# On top of that, resolves which of the three test suites in the `build` job (Cockpit.Core.Tests,
# Cockpit.Infrastructure.Tests, Cockpit.App.ViewTests) a diff actually needs -- derived mechanically from
# the real <ProjectReference> graph in the csproj files, never a hand-maintained table. A hand-kept mapping
# drifts out of sync with the real build the moment someone adds a reference, and a wrong mapping here is a
# silent, wrongly-skipped test -- exactly the failure AC-863 is written against. Anything the graph can't
# resolve (an unrecognized path, a missing csproj) falls back to running every suite, same philosophy as
# the `other` bucket above.

#   scripts/classify-changes.sh <base-ref> [head-ref]
set -euo pipefail

base="${1:?usage: classify-changes.sh <base-ref> [head-ref]}"
head="${2:-HEAD}"

repo_root="$(git rev-parse --show-toplevel)"

TEST_SUITES=(
  "run_core_tests:tests/Cockpit.Core.Tests/Cockpit.Core.Tests.csproj"
  "run_infrastructure_tests:tests/Cockpit.Infrastructure.Tests/Cockpit.Infrastructure.Tests.csproj"
  "run_view_tests:tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj"
)

# Every csproj file (repo-relative) reachable from $1 via <ProjectReference>, transitively, including $1
# itself -- read straight from the file so the graph can't drift out of sync with the real build.
project_closure() {
  local start="$1"
  local -a queue=("$start")
  local -A seen=()
  local path dir ref resolved

  while [ "${#queue[@]}" -gt 0 ]; do
    path="${queue[0]}"
    queue=("${queue[@]:1}")
    [ -n "${seen[$path]:-}" ] && continue
    seen["$path"]=1
    [ -f "$repo_root/$path" ] || continue

    dir="$(dirname "$path")"
    while IFS= read -r ref; do
      [ -n "$ref" ] || continue
      resolved="$(cd "$repo_root/$dir" && realpath --relative-to="$repo_root" "$ref" 2>/dev/null)" || continue
      queue+=("$resolved")
    done < <(grep -oE '<ProjectReference Include="[^"]*"' "$repo_root/$path" 2>/dev/null \
              | sed -E 's/.*Include="([^"]*)"/\1/; s#\\#/#g')
  done

  printf '%s\n' "${!seen[@]}"
}

# The project a changed file belongs to, as its repo-relative csproj path. src/plugins-dev/tests all
# follow the same <root>/<ProjectName>/<ProjectName>.csproj layout in this repo.
owning_project() {
  local f="$1" root name
  case "$f" in
    src/*/*)         root="src" ;;
    plugins-dev/*/*) root="plugins-dev" ;;
    tests/*/*)       root="tests" ;;
    *) return 1 ;;
  esac
  name="${f#"$root"/}"
  name="${name%%/*}"
  printf '%s/%s/%s.csproj\n' "$root" "$name" "$name"
}

src=false; plugins_dev=false; other=false; unresolved=false
declare -A touched_projects=()

while IFS= read -r f; do
  [ -n "$f" ] || continue
  case "$f" in
    src/*) src=true ;;
    plugins-dev/*) plugins_dev=true ;;
    docs/*|*.md|CHANGELOG.md) continue ;;
    *) other=true ;;
  esac

  if proj="$(owning_project "$f")" && [ -f "$repo_root/$proj" ]; then
    touched_projects["$proj"]=1
  else
    unresolved=true
  fi
done <<<"$(git diff --name-only "$base" "$head")"

printf 'src=%s\n' "$src"
printf 'plugins_dev=%s\n' "$plugins_dev"
printf 'other=%s\n' "$other"

for entry in "${TEST_SUITES[@]}"; do
  flag="${entry%%:*}"
  csproj="${entry#*:}"

  if [ "$unresolved" = true ]; then
    printf '%s=true\n' "$flag"
    continue
  fi

  run=false
  if [ "${#touched_projects[@]}" -gt 0 ]; then
    while IFS= read -r member; do
      if [ -n "${touched_projects[$member]:-}" ]; then
        run=true
        break
      fi
    done < <(project_closure "$csproj")
  fi
  printf '%s=%s\n' "$flag" "$run"
done
