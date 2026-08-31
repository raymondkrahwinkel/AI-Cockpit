#!/usr/bin/env bash

# Re-bases one commit per scenario onto a shared base and checks classify-changes.sh's flags for each --
# a wrong flag here means a job silently skips a real regression, or silently always runs and defeats the
# ticket's point.

#   scripts/classify-changes.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${script_dir}/classify-changes.sh"

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

assert_equals() {
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "$2" "$3"; fi
}

cd "$work"
git init --quiet .
git config user.email test@example.com
git config user.name Test
git config commit.gpgsign false
git config core.autocrlf false

# A minimal stand-in for the real Cockpit.Core / Cockpit.Infrastructure / Cockpit.App / *.Tests layout,
# with the same layering: Infrastructure -> Core, App -> Core + Infrastructure, Core.Tests -> Core +
# App (mirroring the real repo's own back-reference), Infrastructure.Tests -> Core + Infrastructure only,
# App.ViewTests -> App only. Bar depends on Foo, while build-only Baz is independent.
mkdir -p src/Cockpit.Core src/Cockpit.Infrastructure src/Cockpit.App src/Cockpit.Unknown \
         plugins-dev/Cockpit.Plugin.Foo plugins-dev/Cockpit.Plugin.Foo.Tests \
         plugins-dev/Cockpit.Plugin.Bar plugins-dev/Cockpit.Plugin.Bar.Tests plugins-dev/Cockpit.Plugin.Baz \
         tests/Cockpit.Core.Tests tests/Cockpit.Infrastructure.Tests tests/Cockpit.App.ViewTests tests/Cockpit.TestSupport \
         docs scripts .github/workflows

# Cockpit.Unknown deliberately has no csproj -- it stands in for a project the graph can't resolve.

printf 'seed\n' >src/Cockpit.Core/Seed.cs
cat >src/Cockpit.Core/Cockpit.Core.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"></Project>
EOF

printf 'seed\n' >src/Cockpit.Infrastructure/Seed.cs
cat >src/Cockpit.Infrastructure/Cockpit.Infrastructure.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Cockpit.Core\Cockpit.Core.csproj" />
  </ItemGroup>
</Project>
EOF

printf 'seed\n' >src/Cockpit.App/Seed.cs
cat >src/Cockpit.App/Cockpit.App.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\Cockpit.Core\Cockpit.Core.csproj" />
    <ProjectReference Include="..\Cockpit.Infrastructure\Cockpit.Infrastructure.csproj" />
    <ProjectReference Include="..\..\plugins-dev\Cockpit.Plugin.Bar\Cockpit.Plugin.Bar.csproj" />
  </ItemGroup>
</Project>
EOF

printf 'seed\n' >plugins-dev/Cockpit.Plugin.Foo/Seed.cs
printf '<Project Sdk="Microsoft.NET.Sdk"></Project>\n' >plugins-dev/Cockpit.Plugin.Foo/Cockpit.Plugin.Foo.csproj
printf 'seed\n' >plugins-dev/Cockpit.Plugin.Foo.Tests/Seed.cs
cat >plugins-dev/Cockpit.Plugin.Foo.Tests/Cockpit.Plugin.Foo.Tests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Cockpit.Plugin.Foo/Cockpit.Plugin.Foo.csproj" /></ItemGroup>
</Project>
EOF

printf 'seed\n' >plugins-dev/Cockpit.Plugin.Bar/Seed.cs
cat >plugins-dev/Cockpit.Plugin.Bar/Cockpit.Plugin.Bar.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Cockpit.Plugin.Foo/Cockpit.Plugin.Foo.csproj" /></ItemGroup>
</Project>
EOF
printf 'seed\n' >plugins-dev/Cockpit.Plugin.Bar.Tests/Seed.cs
cat >plugins-dev/Cockpit.Plugin.Bar.Tests/Cockpit.Plugin.Bar.Tests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="../Cockpit.Plugin.Bar/Cockpit.Plugin.Bar.csproj" /></ItemGroup>
</Project>
EOF

printf 'seed\n' >plugins-dev/Cockpit.Plugin.Baz/Seed.cs
printf '<Project Sdk="Microsoft.NET.Sdk"></Project>\n' >plugins-dev/Cockpit.Plugin.Baz/Cockpit.Plugin.Baz.csproj

printf 'seed\n' >tests/Cockpit.Core.Tests/Seed.cs
# Stands in for ThemeHexColorGuardTests.cs: a source-tree lint that reads plugins-dev/ by path, with no
# <ProjectReference> recording the coupling -- the exact case the mention-check below has to catch.
printf '// scans plugins-dev/ by path, no ProjectReference records it\n' >tests/Cockpit.Core.Tests/PluginsDevLintSeed.cs
cat >tests/Cockpit.Core.Tests/Cockpit.Core.Tests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/Cockpit.Core/Cockpit.Core.csproj" />
    <ProjectReference Include="../../src/Cockpit.App/Cockpit.App.csproj" />
  </ItemGroup>
</Project>
EOF

printf 'seed\n' >tests/Cockpit.Infrastructure.Tests/Seed.cs
cat >tests/Cockpit.Infrastructure.Tests/Cockpit.Infrastructure.Tests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/Cockpit.Core/Cockpit.Core.csproj" />
    <ProjectReference Include="../../src/Cockpit.Infrastructure/Cockpit.Infrastructure.csproj" />
  </ItemGroup>
</Project>
EOF

printf 'seed\n' >tests/Cockpit.App.ViewTests/Seed.cs
cat >tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../../src/Cockpit.App/Cockpit.App.csproj" />
  </ItemGroup>
</Project>
EOF

printf 'seed\n' >tests/Cockpit.TestSupport/Seed.cs
printf '<Project Sdk="Microsoft.NET.Sdk"></Project>\n' >tests/Cockpit.TestSupport/Cockpit.TestSupport.csproj

printf '# seed\n' >docs/seed.md
printf 'seed\n' >README.md
printf 'seed\n' >CHANGELOG.md
printf 'seed\n' >scripts/seed.sh
git add -A
git commit --quiet -m base
base="$(git rev-parse HEAD)"

all_plugins='["Cockpit.Plugin.Bar","Cockpit.Plugin.Baz","Cockpit.Plugin.Foo"]'
assert_equals "--all: plugins" "$all_plugins" \
  "$("$guard" --all | grep '^plugins=' | cut -d= -f2-)"

# run <name> <src> <plugins_dev> <other> <core> <infra> <view> <plugins JSON> <path>...
run() {
  local name="$1" want_src="$2" want_plugins_dev="$3" want_other="$4"
  local want_core="$5" want_infra="$6" want_view="$7" want_plugins="$8"
  shift 8
  git checkout --quiet "$base"
  for path in "$@"; do printf 'changed\n' >>"$path"; done
  git add -A
  git commit --quiet -m "$name"

  local out src plugins_dev other core infra view plugins
  out="$("$guard" "$base" HEAD)"
  src="$(printf '%s\n' "$out" | grep '^src=' | cut -d= -f2)"
  plugins_dev="$(printf '%s\n' "$out" | grep '^plugins_dev=' | cut -d= -f2)"
  other="$(printf '%s\n' "$out" | grep '^other=' | cut -d= -f2)"
  core="$(printf '%s\n' "$out" | grep '^run_core_tests=' | cut -d= -f2)"
  infra="$(printf '%s\n' "$out" | grep '^run_infrastructure_tests=' | cut -d= -f2)"
  view="$(printf '%s\n' "$out" | grep '^run_view_tests=' | cut -d= -f2)"
  plugins="$(printf '%s\n' "$out" | grep '^plugins=' | cut -d= -f2-)"

  assert_equals "$name: src" "$want_src" "$src"
  assert_equals "$name: plugins_dev" "$want_plugins_dev" "$plugins_dev"
  assert_equals "$name: other" "$want_other" "$other"
  assert_equals "$name: run_core_tests" "$want_core" "$core"
  assert_equals "$name: run_infrastructure_tests" "$want_infra" "$infra"
  assert_equals "$name: run_view_tests" "$want_view" "$view"
  assert_equals "$name: plugins" "$want_plugins" "$plugins"
}

#    name                        src    plugins_dev other  core   infra  view   plugins
run "docs-only (md)"             false  false       false  false  false  false  '[]' README.md
run "docs-only (docs/)"          false  false       false  false  false  false  '[]' docs/seed.md
run "docs-only (changelog)"      false  false       false  false  false  false  '[]' CHANGELOG.md
# Bar references Foo, so a Foo change must select both plugins. The host suites also reach Foo through
# App -> Bar -> Foo, proving the same project closure handles dependencies without a mapping table.
run "plugin dependency" false true false true false true \
  '["Cockpit.Plugin.Bar","Cockpit.Plugin.Foo"]' plugins-dev/Cockpit.Plugin.Foo/Seed.cs

# Bar is only reachable through App, so it pulls in every suite that reaches App -- Core.Tests and
# App.ViewTests, not Infrastructure.Tests (which never references App at all in this layering).
run "single plugin" false true false true false true \
  '["Cockpit.Plugin.Bar"]' plugins-dev/Cockpit.Plugin.Bar/Seed.cs

run "build-only plugin" false true false true false false \
  '["Cockpit.Plugin.Baz"]' plugins-dev/Cockpit.Plugin.Baz/Seed.cs

# Core is the leaf every suite transitively depends on -- touching it must run all three.
run "Core change" true false false true true true \
  "$all_plugins" src/Cockpit.Core/Seed.cs

# App references Infrastructure directly, and both Core.Tests and App.ViewTests reference App -- so an
# Infrastructure change reaches every suite here too, same as a Core change. There is no test suite in
# this repo's layering an Infrastructure change can safely skip.
run "Infrastructure change" true false false true true true \
  "$all_plugins" src/Cockpit.Infrastructure/Seed.cs

# The scenario the ticket calls out by name: App-only must not still run Infrastructure.Tests, but
# Core.Tests still must -- its own csproj references App directly, so skipping it here would be exactly
# the silent, wrongly-skipped test AC-863 is written against.
run "App-only" true false false true false true \
  "$all_plugins" src/Cockpit.App/Seed.cs

run "src plus plugins-dev" true true false true true true \
  "$all_plugins" src/Cockpit.Core/Seed.cs plugins-dev/Cockpit.Plugin.Foo/Seed.cs

# A suite's own test file changing (no source change) must only require that suite.
run "Infrastructure.Tests-only" false false true false true false '[]' tests/Cockpit.Infrastructure.Tests/Seed.cs

run "shared test support" false false true false false false \
  "$all_plugins" tests/Cockpit.TestSupport/Seed.cs

# The safe-side fallback AC-863 requires: an unrecognized path (here, a project directory the graph
# doesn't know) can't be resolved to a project, so every suite must run rather than silently skip one.
run "unresolved project" true false false true true true \
  "$all_plugins" src/Cockpit.Unknown/Seed.cs

run "ambiguous script" false false true true true true \
  "$all_plugins" scripts/seed.sh
run "ambiguous workflow file" false false true true true true \
  "$all_plugins" .github/workflows/ci.yml

if [ "$failures" -ne 0 ]; then
  printf '\n%d test(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall tests passed\n'
