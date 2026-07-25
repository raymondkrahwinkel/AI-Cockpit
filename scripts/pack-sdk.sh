#!/usr/bin/env bash
# Packs the plugin SDK (Cockpit.Plugins.Abstractions) for a release page: the .nupkg an out-of-repo plugin
# author references, plus a zip of the bare assembly for whoever would rather drop a <Reference> in than add
# a feed.
#
# Why a GitHub release asset and not nuget.org: a package id on nuget.org is public and permanent, and this
# one still moves — the product rename is undecided, and burning the id under a name that may change would
# leave an abandoned package and plugin authors switching mid-flight. A release asset is retractable, so the
# SDK can be available to out-of-repo authors now and land on a public feed once the name is final.
#
# Usage:   scripts/pack-sdk.sh <output-dir> [version-suffix]
# Example: scripts/pack-sdk.sh artifacts                  -> Cockpit.Plugins.Abstractions.1.27.0.nupkg
#          scripts/pack-sdk.sh artifacts nightly.42       -> ...1.27.0-nightly.42.nupkg
# Output:  <output-dir>/Cockpit.Plugins.Abstractions.<version>.nupkg
#          <output-dir>/cockpit-plugin-sdk-<version>.zip
#
# The suffix exists for the nightly. NuGet caches a restored package by id+version, so re-publishing changed
# bytes under the same 1.27.0 would be restored from cache and silently stale — a prerelease suffix per run
# keeps a moving channel honest, and marks it as not-a-release to anyone who reads the version.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Cockpit.Plugins.Abstractions/Cockpit.Plugins.Abstractions.csproj"

output_dir="${1:?usage: scripts/pack-sdk.sh <output-dir> [version-suffix]}"
suffix="${2:-}"

# Ask MSBuild for the version rather than grepping the csproj: that file carries a prose comment per version
# bump, so a grep for <Version> matches the history above the property as readily as the property itself.
base="$(dotnet msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
version="$base"
if [ -n "$suffix" ]; then
  version="$base-$suffix"
fi

mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

dotnet pack "$project" --configuration Release -p:Version="$version" --output "$output_dir"

# The zip holds exactly what a PackageReference would put on the compile line — the assembly and its XML docs
# — and nothing more. The SDK's own dependencies (Avalonia, the DI abstractions, Material.Icons) come off
# nuget.org like any other package; shipping copies of them here would invite the type-identity mistake the
# guide warns about, which is also why the note below is in the zip rather than only in the docs.
bin="$(dirname "$project")/bin/Release/net10.0"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

cp "$bin/Cockpit.Plugins.Abstractions.dll" "$bin/Cockpit.Plugins.Abstractions.xml" "$staging/"

cat > "$staging/README.txt" <<EOF
Cockpit plugin SDK $version

  Cockpit.Plugins.Abstractions.dll   the contract every plugin compiles against
  Cockpit.Plugins.Abstractions.xml   the XML docs, for IntelliSense

Reference the assembly with <Private>false</Private> so your plugin folder does not ship a copy of it. The
host loads its own, and two copies mean two different types with the same name — the host then silently
ignores your plugin.

  <Reference Include="Cockpit.Plugins.Abstractions">
    <HintPath>lib\Cockpit.Plugins.Abstractions.dll</HintPath>
    <Private>false</Private>
  </Reference>

The .nupkg on the same release page does that for you and pulls in the SDK's own dependencies as well; this
zip leaves both to you. Either way you also need Avalonia and
Microsoft.Extensions.DependencyInjection.Abstractions, compile-only, at the versions the host ships.

Guide: https://github.com/raymondkrahwinkel/AI-Cockpit/blob/main/docs/plugins/PLUGIN-SDK.md
EOF

( cd "$staging" && zip -qr -X "$output_dir/cockpit-plugin-sdk-$version.zip" . )

echo "Packed the plugin SDK $version into $output_dir:"
ls -1 "$output_dir/Cockpit.Plugins.Abstractions.$version.nupkg" "$output_dir/cockpit-plugin-sdk-$version.zip"
