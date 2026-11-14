#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

ensure_linux_browser() {
  if [[ $(uname -s) != Linux || $1 != plugins-dev/Cockpit.Plugin.Browser.Tests/Cockpit.Plugin.Browser.Tests.csproj ]]; then
    return
  fi

  apt-get update --quiet
  apt-get install --yes --no-install-recommends --quiet ca-certificates curl gnupg
  curl --fail --silent --show-error --location https://dl.google.com/linux/linux_signing_key.pub \
    | gpg --dearmor --yes --output /usr/share/keyrings/google-chrome.gpg
  echo 'deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main' \
    > /etc/apt/sources.list.d/google-chrome.list
  apt-get update --quiet
  apt-get install --yes --no-install-recommends --quiet google-chrome-stable
}

# --full names the existing no-argument gate for callers that want to be explicit.
if [[ ${1:-} == "--full" && $# -eq 1 ]]; then
  shift
fi

find_gate_python() {
  if command -v py >/dev/null 2>&1; then
    gate_python=py
  elif command -v python3 >/dev/null 2>&1; then
    gate_python=python3
  elif command -v python >/dev/null 2>&1; then
    gate_python=python
  else
    echo "Python is required for the local gate (tried py, python3, python)." >&2
    exit 49
  fi
}

# --nightly: run exactly the test steps of the 'test' job in .github/workflows/nightly.yml in the Linux container.
# Run it on main before dispatching a nightly; nothing in GitHub CI depends on it.
# Default is incremental: only the test projects touched since the last green nightly (--since <sha> overrides
# that sha); --all runs everything.
if [[ ${1:-} == "--nightly" ]]; then
  shift
  nightly_base=
  nightly_all=false
  while [[ $# -gt 0 ]]; do
    case $1 in
      --all) nightly_all=true; shift ;;
      --since) nightly_base=${2:?--since needs a sha}; shift 2 ;;
      *) echo "usage: scripts/local-gate.sh --nightly [--all | --since <sha>]" >&2; exit 2 ;;
    esac
  done
  find_gate_python
  if [[ $nightly_all == true ]]; then
    nightly_base=
  else
    if [[ -z $nightly_base ]]; then
      nightly_base=$(gh run list --workflow nightly.yml --status success --limit 1 --json headSha --jq '.[0].headSha // empty' | tr -d '\r')
    fi
    if [[ -z $nightly_base ]] || ! git cat-file -e "${nightly_base}^{commit}" 2>/dev/null; then
      echo "No usable last-green-nightly commit (${nightly_base:-none}); running everything. Use --since <sha> or git fetch." >&2
      nightly_base=
    fi
  fi
  steps=$("$gate_python" scripts/nightly-test-steps.py ${nightly_base:+--base "$nightly_base"} | tr -d '\r')
  selection=$(grep '^NIGHTLY_SELECTION=' <<<"$steps")
  selection=${selection#NIGHTLY_SELECTION=}
  steps=$(grep -v '^NIGHTLY_SELECTION=' <<<"$steps")
  echo "$selection" >&2
  {
    declare -f ensure_linux_browser
    printf 'selection=%s\n' "$selection"
    cat <<'NIGHTLY_GATE'
set -uo pipefail
SECONDS=0

tar -C /source --exclude=.git --exclude=.superpowers --exclude=TestResults \
  --exclude='*/bin' --exclude='*/bin/*' --exclude='*/obj' --exclude='*/obj/*' -cf - . | tar -xf -

mkdir -p "$HOME/.config"
apt-get update --quiet
apt-get install --yes --no-install-recommends --quiet libfontconfig1 python3
ensure_linux_browser plugins-dev/Cockpit.Plugin.Browser.Tests/Cockpit.Plugin.Browser.Tests.csproj

red=()
summary() {
  local mins=$((SECONDS / 60)) secs=$((SECONDS % 60))
  echo
  if [[ ${#red[@]} -eq 0 ]]; then
    echo "NIGHTLY GATE: GREEN (${mins}m${secs}s) - $selection"
    exit 0
  fi
  echo "NIGHTLY GATE: RED (${mins}m${secs}s) - ${red[*]} - $selection"
  exit 1
}

# A red test does not stop the run, a red build does: the tests after it run --no-build.
run_step() {
  local name=$1 cmd log names
  cmd=$(cat)
  log=$(mktemp)
  echo "::: $name"
  if bash -eo pipefail -c "$cmd" 2>&1 | tee "$log"; then
    rm -f "$log"
    return
  fi
  if [[ $cmd != *"dotnet test"* ]]; then
    red+=("[$name failed]")
    summary
  fi
  names=$(grep -E '^[[:space:]]+Failed ' "$log" | sed -E 's/^[[:space:]]+Failed //; s/ \[[0-9.]+ ?[a-z]+\]$//' | sort -u || true)
  if [[ -z $names ]]; then
    red+=("[$name: failed without naming a test]")
  else
    while IFS= read -r n; do red+=("$n"); done <<< "$names"
  fi
  rm -f "$log"
}

NIGHTLY_GATE
    printf '%s\n' "$steps"
    echo 'summary'
  } | MSYS_NO_PATHCONV=1 docker run --rm --interactive \
    --name "local-nightly-$$" \
    --label "cockpit.pane=${COCKPIT_PANE_ID:-local}" \
    --volume "$PWD:/source:ro" \
    --workdir /work \
    --env DOTNET_CLI_HOME=/tmp/dotnet-home \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -s
  exit
fi

if [[ ${1:-} == "--linux" ]]; then
  if [[ $# -lt 2 ]]; then
    echo "usage: scripts/local-gate.sh --linux <test-project.csproj> [dotnet test arguments]" >&2
    exit 2
  fi

  test_project=${2#./}
  case "$test_project" in
    /*|../*|*/../*)
      echo "The Linux test project must be a path inside this repository." >&2
      exit 2
      ;;
  esac
  if [[ $test_project != *.csproj || ! -f $test_project ]]; then
    echo "Test project not found: $test_project" >&2
    exit 2
  fi

  shift 2
  {
    declare -f ensure_linux_browser
    cat <<'LINUX_GATE'
set -euo pipefail

test_project=$1
shift

# Build on Docker's Linux filesystem; compiling through Docker Desktop's Windows bind mount is too slow and
# lets Linux obj/bin files interfere with the following Windows proof.
tar -C /source --exclude=.git --exclude=.superpowers --exclude=TestResults \
  --exclude='*/bin' --exclude='*/bin/*' --exclude='*/obj' --exclude='*/obj/*' -cf - . | tar -xf -

mkdir -p "$HOME/.config"
apt-get update --quiet
apt-get install --yes --no-install-recommends --quiet libfontconfig1

ensure_linux_browser "$test_project"

dotnet build "$test_project" --configuration Release -warnaserror

dotnet test "$test_project" \
  --configuration Release \
  --no-build \
  --logger trx \
  --logger "console;verbosity=minimal" \
  --results-directory /tmp/TestResults \
  --collect:"XPlat Code Coverage" \
  --blame-hang \
  --blame-hang-timeout 5m \
  --blame-hang-dump-type none \
  "$@"
LINUX_GATE
  } | MSYS_NO_PATHCONV=1 docker run --rm --interactive \
    --volume "$PWD:/source:ro" \
    --workdir /work \
    --env DOTNET_CLI_HOME=/tmp/dotnet-home \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -s -- "$test_project" "$@"
  exit
fi

if [[ $# -ne 0 ]]; then
  echo "usage: scripts/local-gate.sh [--full|--nightly|--linux <test-project.csproj> [dotnet test arguments]]" >&2
  exit 2
fi

find_gate_python

gate_output=$(mktemp)
trap 'rm -f "$gate_output"' EXIT

# A red test does not stop the run: every project is run, the red tests are kept per project and listed at the end,
# and the gate then exits non-zero. No exception list and no filter: a red test stays red.
red_tests=()
gate_test() {
  local log project
  log=$(mktemp)
  project=$(basename "${3:-?}" .csproj)
  if ! "$@" 2>&1 | tee "$log"; then
    local names
    names=$(grep -E '^[[:space:]]+Failed ' "$log" | sed -E 's/^[[:space:]]+Failed //; s/ \[[0-9.]+ ?[a-z]+\]$//' || true)
    if [[ -z $names ]]; then
      red_tests+=("$project: the run failed without naming a test")
    else
      while IFS= read -r name; do
        red_tests+=("$project: $name")
      done <<< "$names"
    fi
  fi
  rm -f "$log"
}

bash scripts/classify-changes.sh --all >> "$gate_output"
bash scripts/classify-changes.test.sh

dotnet restore Cockpit.slnx
dotnet build Cockpit.slnx --configuration Release --no-restore -warnaserror
dotnet build src/Cockpit.App --configuration Debug --no-restore
gate_test dotnet test tests/Cockpit.Core.Tests/Cockpit.Core.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
gate_test dotnet test tests/Cockpit.Infrastructure.Tests/Cockpit.Infrastructure.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
gate_test dotnet test tests/Cockpit.Backend.Tests/Cockpit.Backend.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
gate_test dotnet test tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
bash scripts/nightly-notes.test.sh

dotnet restore tests/Cockpit.Journeys/Cockpit.Journeys.csproj
dotnet build tests/Cockpit.Journeys/Cockpit.Journeys.csproj --configuration Release --no-restore -warnaserror
dotnet publish src/Cockpit.Server/Cockpit.Server.csproj --configuration Release --runtime linux-x64 --self-contained false --output out/server
if find out/server -iname 'Avalonia*.dll' -o -iname '*EchoProvider*' | grep .; then
  echo "The Cockpit.Server publish carries a UI assembly or a test fixture." >&2
  exit 1
fi
gate_test dotnet test tests/Cockpit.Journeys/Cockpit.Journeys.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type none

plugins_json=$(grep '^plugins=' "$gate_output" | tail -n 1)
while IFS= read -r plugin; do
  project="plugins-dev/$plugin/$plugin.csproj"
  test_project="plugins-dev/$plugin.Tests/$plugin.Tests.csproj"
  if [ -f "$test_project" ]; then
    project="$test_project"
  fi
  ensure_linux_browser "$project"
  dotnet build "$project" --configuration Release -warnaserror
  if [ -f "$test_project" ]; then
    gate_test dotnet test "$project" --configuration Release --no-build --logger "console;verbosity=minimal" --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
  fi
done < <("$gate_python" -c 'import json,sys; print("\n".join(json.loads(sys.argv[1])))' "${plugins_json#plugins=}" | tr -d '\r')

bash scripts/check-plugin-versions.test.sh
bash scripts/check-plugin-abstractions-version.test.sh
git fetch origin main
bash scripts/check-plugin-versions.sh "$(git rev-parse origin/main)" HEAD
bash scripts/check-plugin-abstractions-version.sh
"$gate_python" scripts/check-xmldoc-scope.py
"$gate_python" scripts/check-xmldoc-scope.test.py
"$gate_python" scripts/check-comment-length.py
"$gate_python" scripts/check-comment-length.test.py
"$gate_python" scripts/check-test-loopback.py
"$gate_python" scripts/check-plugin-ui-sync-waits.py
"$gate_python" scripts/check-plugin-ui-sync-waits.test.py
"$gate_python" scripts/check-test-count.py
"$gate_python" scripts/check-test-count.test.py
"$gate_python" scripts/check-duplicate-test-bodies.py
"$gate_python" scripts/check-duplicate-test-bodies.test.py
"$gate_python" scripts/check-descendant-selector-scope.py
"$gate_python" scripts/check-descendant-selector-scope.test.py
"$gate_python" scripts/store-index-upsert.test.py

if [[ ${#red_tests[@]} -gt 0 ]]; then
  echo
  echo "Red tests:" >&2
  printf '  %s\n' "${red_tests[@]}" >&2
  exit 1
fi
