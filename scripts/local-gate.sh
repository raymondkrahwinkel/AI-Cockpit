#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

gate_output=$(mktemp)
trap 'rm -f "$gate_output"' EXIT

bash scripts/classify-changes.sh --all >> "$gate_output"
bash scripts/classify-changes.test.sh

dotnet restore Cockpit.slnx
dotnet build Cockpit.slnx --configuration Release --no-restore -warnaserror
dotnet build src/Cockpit.App --configuration Debug --no-restore
dotnet test tests/Cockpit.Core.Tests/Cockpit.Core.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
dotnet test tests/Cockpit.Infrastructure.Tests/Cockpit.Infrastructure.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
dotnet test tests/Cockpit.Backend.Tests/Cockpit.Backend.Tests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
dotnet test tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
bash scripts/nightly-notes.test.sh

dotnet restore tests/Cockpit.Journeys/Cockpit.Journeys.csproj
dotnet build tests/Cockpit.Journeys/Cockpit.Journeys.csproj --configuration Release --no-restore -warnaserror
dotnet publish src/Cockpit.Server/Cockpit.Server.csproj --configuration Release --runtime linux-x64 --self-contained false --output out/server
if find out/server -iname 'Avalonia*.dll' -o -iname '*EchoProvider*' | grep .; then
  echo "The Cockpit.Server publish carries a UI assembly or a test fixture." >&2
  exit 1
fi
dotnet test tests/Cockpit.Journeys/Cockpit.Journeys.csproj --configuration Release --no-build --verbosity minimal --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type none

plugins_json=$(grep '^plugins=' "$gate_output" | tail -n 1)
while IFS= read -r plugin; do
  project="plugins-dev/$plugin/$plugin.csproj"
  test_project="plugins-dev/$plugin.Tests/$plugin.Tests.csproj"
  if [ -f "$test_project" ]; then
    project="$test_project"
  fi
  dotnet build "$project" --configuration Release -warnaserror
  if [ -f "$test_project" ]; then
    dotnet test "$project" --configuration Release --no-build --logger "console;verbosity=minimal" --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
  fi
done < <(python3 -c 'import json,sys; print("\n".join(json.loads(sys.argv[1])))' "${plugins_json#plugins=}" | tr -d '\r')

bash scripts/check-plugin-versions.test.sh
bash scripts/check-plugin-abstractions-version.test.sh
git fetch origin main
bash scripts/check-plugin-versions.sh "$(git rev-parse origin/main)" HEAD
bash scripts/check-plugin-abstractions-version.sh
python3 scripts/check-xmldoc-scope.py
python3 scripts/check-xmldoc-scope.test.py
python3 scripts/check-comment-length.py
python3 scripts/check-comment-length.test.py
python3 scripts/check-test-loopback.py
python3 scripts/check-test-count.py
python3 scripts/check-test-count.test.py
python3 scripts/check-duplicate-test-bodies.py
python3 scripts/check-duplicate-test-bodies.test.py
python3 scripts/check-descendant-selector-scope.py
python3 scripts/check-descendant-selector-scope.test.py
python3 scripts/store-index-upsert.test.py
