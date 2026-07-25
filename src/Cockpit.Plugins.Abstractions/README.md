# Cockpit Plugin SDK

The contract assembly for building [AI-Cockpit](https://github.com/raymondkrahwinkel/AI-Cockpit) plugins. A
plugin is a .NET assembly that implements `ICockpitPlugin` and contributes settings, side-menu entries,
dialogs, widgets, whole workspaces, session providers and MCP servers through the `ICockpitHost` facade it is
handed at startup. Everything a plugin may reference from the host lives in this one assembly.

Full guide: **[docs/plugins/PLUGIN-SDK.md](https://github.com/raymondkrahwinkel/AI-Cockpit/blob/main/docs/plugins/PLUGIN-SDK.md)** ·
API: **[API-REFERENCE.md](https://github.com/raymondkrahwinkel/AI-Cockpit/blob/main/docs/plugins/API-REFERENCE.md)**

## Reference it compile-only

The host loads its own copy of this assembly, so your plugin folder must **not** ship one. Two copies mean two
different types with the same name, and the host then silently finds no plugin in your assembly.

```xml
<PackageReference Include="Cockpit.Plugins.Abstractions" Version="1.27.0">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

The same applies to the assemblies this package depends on — Avalonia and
`Microsoft.Extensions.DependencyInjection.Abstractions` — and to `Avalonia.Controls.DataGrid` if your UI uses
the grid the host provides. Reference each of them with `<ExcludeAssets>runtime</ExcludeAssets>` too, at the
version the host ships (Avalonia 12.0.5 today). Your *own* dependencies are referenced normally: they belong
in your plugin folder and the loader resolves them from there.

Also set `<EnableDynamicLoading>true</EnableDynamicLoading>`, which emits the `.deps.json` the plugin loader
reads.

## Two version gates, and they are not this package's version

- **`abstractionsVersion`** in your `plugin.json` is this package's **major** — a breaking-change counter the
  host enforces. Mismatch, and the host refuses to load your plugin with a clear message.
- **`minHostVersion`** is the oldest cockpit your plugin actually works against. Within a major the contract
  only grows, so building against an older minor than the host is fine; the reverse means you may call a
  member the running host does not have. Name the first host version that carries the contribution points you
  use — not the number a template happened to ship.

This package's own version is plain semver on its members: a new member is a minor, a removal or a signature
change is a major.

## Not on nuget.org (yet)

Releases publish this package as an asset on the
[GitHub release page](https://github.com/raymondkrahwinkel/AI-Cockpit/releases) rather than to a public feed:
the product name is not final, and a package id on nuget.org is permanent. Download the `.nupkg` and add the
folder as a source:

```bash
dotnet nuget add source /path/to/folder -n cockpit-sdk
```

The SDK guide covers this, and the `nuget.config` form that keeps it reproducible for anyone cloning your
plugin, under "Getting the SDK outside the repo".

## Trust model

A plugin runs **in-process, unsandboxed, with the operator's permissions** — .NET offers no security boundary
for in-process plugins. The cockpit's only protection is a manual install plus a first-load consent that pins
the assembly's SHA-256. Write plugins accordingly, and keep credentials in `IPluginStorage`, never in source.

MIT licensed. © 2026 Raymond Krahwinkel / Krahwinkel-IT.
