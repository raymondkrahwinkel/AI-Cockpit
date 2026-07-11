# Cockpit Plugin SDK

Build a plugin that extends the cockpit with its own settings, a left-menu section, dialogs, and actions on
the active session — without touching the cockpit's own code. This guide is the how-to; the
**[API reference](API-REFERENCE.md)** documents every method with signatures and examples, and
[Examples](#examples) below tours the three built-in sample plugins under
[`plugins-dev/`](../../plugins-dev).

> **Trust model — read this first.** A plugin is a .NET assembly that runs **in-process, unsandboxed, with
> your account's permissions**. There is no security boundary (.NET cannot provide one for in-process
> plugins). The cockpit protects you only by requiring a **manual install** and a **first-load consent**
> that pins the assembly's SHA-256 — a changed file re-prompts. **Only install plugins you trust.**

## Overview

A Cockpit plugin is a small .NET assembly, dropped in its own folder under the config directory's
`plugins/` folder, that implements one interface (`ICockpitPlugin`) and contributes UI and behaviour through
a host facade (`ICockpitHost`) handed to it at startup. Everything a plugin can reference from the host lives
in a single assembly, **`Cockpit.Plugins.Abstractions`** — that is the entire contract surface.

**Load model.** Each plugin gets its own `AssemblyLoadContext` (`PluginLoadContext`, internal to the host),
built from the .NET "app with plugins" pattern:

- An `AssemblyDependencyResolver` resolves the plugin's *own* dependencies (anything it ships itself) from
  its plugin folder, using the `.deps.json` the build produces.
- Anything the plugin does **not** carry — `Cockpit.Plugins.Abstractions`, Avalonia, the DI abstractions —
  falls through to the host's default load context instead. This is what keeps `ICockpitPlugin`, `Control`,
  `IServiceCollection`, etc. a **single type identity** across the plugin/host boundary. If a plugin shipped
  its own copy of one of these assemblies, the types it implements would be a *different* `ICockpitPlugin`
  from the host's point of view, and the host would silently fail to find a plugin in it.
- Loading is **non-collectible**: disabling a plugin removes its UI and calls `Dispose()`, but the assembly
  itself is only actually freed when the process restarts (a loaded assembly cannot be truly unloaded from a
  non-collectible context). The plugin manager says as much when you disable one.

**Unsandboxed.** A plugin runs with the same rights as the cockpit process — your account, your file
system, your network. There is no capability restriction, permission prompt per-API, or process isolation.
The only gate is the **manual install + first-load consent** flow (see [Installing](#installing-enabling-disabling-removing)),
which pins the entry assembly's SHA-256 so a subsequent tampering or update re-prompts for consent.

## Quickstart

The shortest path from nothing to a running plugin:

```bash
# 1. Scaffold from the template (see "Scaffold a new plugin" below for one-time template install)
dotnet new cockpit-plugin -n My.Plugin -o plugins-dev/My.Plugin

# 2. Build
dotnet build plugins-dev/My.Plugin -c Release

# 3. Package — plugin.json must sit at the zip root
Compress-Archive -Path plugins-dev/My.Plugin/bin/Release/net10.0/* -DestinationPath my-plugin-1.0.0.zip

# 4. Install: in the cockpit, Options -> Plugins -> Install from zip..., pick the zip,
#    "Review & enable", consent, then click "Restart cockpit now" (#53) — or restart it yourself.
```

That scaffolds a plugin with a left-menu button that opens a dialog (see
[`templates/cockpit-plugin`](../../templates/cockpit-plugin)); read on for every contribution point, the
manifest fields, and how to publish it to a store.

### Scaffold a new plugin

The `dotnet new` template lives in [`templates/cockpit-plugin`](../../templates/cockpit-plugin). Install it
once per machine (or after the template changes), then instantiate it under `plugins-dev/` so its relative
`ProjectReference` to `Cockpit.Plugins.Abstractions` resolves:

```bash
dotnet new install ./templates/cockpit-plugin
dotnet new cockpit-plugin -n My.Plugin -o plugins-dev/My.Plugin
```

This generates a `.csproj` with the compile-only shared references already wired up, a `plugin.json`, a
minimal `ICockpitPlugin` (`SamplePlugin.cs`) that adds a left-menu button, and a small control
(`SamplePanelControl.cs`) that reads/writes `host.Storage` and calls `host.Actions`. Rename the generated
`Cockpit.Plugin.Sample` namespace/class/ids to your own before shipping.

## What a plugin can do

A plugin implements one interface, `ICockpitPlugin`, and contributes through the host (`ICockpitHost`):

| Contribution point | Method | Result |
|---|---|---|
| Settings | `host.AddSettings(() => control)` | Opened from the **gear** next to your plugin in the Plugins manager — a per-plugin settings dialog (no top-level Options tab). |
| Left-menu button | `host.AddSideMenuButton(title, onInvoke)` | A launcher button in the sidebar; clicking runs your action (usually opening a dialog). |
| Dialog | `host.ShowDialogAsync(title, () => control)` | A modal dialog over the main window hosting your control. The host provides the **DataGrid** (control + theme) app-wide, so you can use it. |
| Left-menu section | `host.AddSideMenuSection(title, () => control)` | An inline accordion under the session list — for small, always-visible content. |
| Act on the session | `host.Actions` | Inject text into the active session's prompt, or set the clipboard. |
| Persist settings | `host.Storage` | Per-plugin key/value storage in `cockpit.json`. |
| Register services | `plugin.ConfigureServices(services)` | Add your own services to the host DI container (phase 1). |

## The contract

All of these live in `Cockpit.Plugins.Abstractions` (the only assembly you must reference); see the
**[API reference](API-REFERENCE.md)** for every member with a code example:

```csharp
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    void ConfigureServices(IServiceCollection services); // phase 1: before the container is built
    void Initialize(ICockpitHost host);                  // phase 2: after the host is up
}

public interface ICockpitHost
{
    IServiceProvider Services { get; }
    ICockpitActions Actions { get; }
    IPluginStorage Storage { get; }
    void AddSettings(Func<Control> createView);                 // opened from the manager's gear
    void AddSideMenuButton(string title, Action onInvoke);      // sidebar launcher button
    void AddSideMenuSection(string title, Func<Control> createView); // inline sidebar accordion
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
}

public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);
    Task InjectIntoActiveSessionAsync(string text); // SDK sessions: the input box · TTY sessions: the pty
    bool HasActiveSession { get; }
}

public interface IPluginStorage
{
    T? Get<T>(string key);        // JSON-serialized values
    void Set<T>(string key, T value);
}

public interface IPluginSettingsView
{
    bool Save(); // return true to close the dialog, false to keep it open (e.g. validation failed)
}

public sealed record PluginMetadata(string Id, string DisplayName, string Version, string? Author, string? Description);
```

### Two-phase lifecycle

1. **`ConfigureServices(IServiceCollection)`** runs **before** the host builds its DI container, so you can
   register your own services. It only runs at startup for an already-enabled plugin — a plugin enabled
   *this* session (its consent just given) contributes its **UI** immediately but its **services only after
   the next restart** (the container is already built). Keep `ConfigureServices` optional where you can.
2. **`Initialize(ICockpitHost)`** runs once the host and UI exist. Register your contribution points here —
   at most once each for `AddSettings`, and as many `AddSideMenuButton`/`AddSideMenuSection` calls as you need.
3. **`Dispose()`** runs when the plugin is disabled or the app exits. Note: the assembly is **not** unloaded
   until the process restarts (a loaded plugin cannot be truly unloaded) — "disable" means UI off + Dispose.

### Settings dialog

Register a settings view with `host.AddSettings(() => new MySettingsControl(...))`; it opens from the gear
next to your plugin in the manager. The host wraps it in a dialog with a **Close** button, and — if your
control implements `IPluginSettingsView` — a **Save** button too:

```csharp
public sealed class MySettingsControl : UserControl, IPluginSettingsView
{
    public bool Save() { /* persist via host.Storage */ return true; } // return false to keep the dialog open
}
```

The host calls `Save()` and closes the dialog when it returns true, so every plugin's settings dialog gets
the same Save/Close behaviour — you don't add your own Save button. A view that applies changes live can
skip the interface and just gets a Close button.

## The manifest — `plugin.json`

Ships in the plugin's folder root. Parsed and validated **before** anything is loaded — a malformed or
version-mismatched manifest is rejected with a message rather than crashing mid-load.

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "entryAssembly": "My.Plugin.dll",
  "entryType": "My.Plugin.MyPlugin",
  "abstractionsVersion": 1,
  "minHostVersion": "1.0.0",
  "description": "What it does, one line.",
  "author": "You"
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable identity; normalized to the install folder slug. Non-empty string. |
| `name` | yes | Display name in the Plugins manager. Non-empty string. |
| `version` | yes | Your plugin's version. Non-empty string (no particular format enforced). |
| `entryAssembly` | yes | The DLL carrying your `ICockpitPlugin`, relative to the plugin folder. The installer refuses the zip if this file is missing. |
| `abstractionsVersion` | yes | The SDK **major** you built against (an integer) — must equal the host's (`AbstractionsContract.Version`), or the host refuses to load the plugin with a clear message. |
| `entryType` | no | Fully-qualified entry type; omit to let the host find the single `ICockpitPlugin` in the entry assembly. |
| `minHostVersion` | no | Informational only today — the host parses and stores it but does **not** currently enforce it as a gate. Set it anyway so a future host version can. |
| `description`, `author` | no | Shown in the Plugins manager and any store catalogue. |

## Project setup

The one rule that matters: **the shared assemblies must not ship in your plugin folder.** The host provides
`Cockpit.Plugins.Abstractions`, Avalonia and the DI abstractions; if your folder carried its own copies,
`ICockpitPlugin` would be a *different type* across the load boundary and the host would silently ignore
your plugin (the type-identity pitfall — see [Overview](#overview)). Reference them **compile-only**:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading> <!-- emits the .deps.json the loader needs -->
  </PropertyGroup>

  <ItemGroup>
    <!-- In-repo: a project reference. Out-of-repo: <PackageReference Include="Cockpit.Plugins.Abstractions" .../> -->
    <ProjectReference Include="..\..\src\Cockpit.Plugins.Abstractions\Cockpit.Plugins.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.0.5"><ExcludeAssets>runtime</ExcludeAssets></PackageReference>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.9">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
    <!-- Only if your UI uses a DataGrid: the host provides the control + app-wide theme, so reference it
         compile-only too, the same way (see the GitHub Issues / Pull Requests / YouTrack examples). -->
    <PackageReference Include="Avalonia.Controls.DataGrid" Version="12.0.1">
      <ExcludeAssets>runtime</ExcludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
  </ItemGroup>
</Project>
```

Your **own** dependencies (a NuGet the host doesn't provide) are referenced normally — they ship in your
folder and the loader resolves them from there via the `.deps.json`.

### Match the host's versions

A plugin is bound to the host's Avalonia major (and the abstractions major). Reference the **same Avalonia
version the host uses** (12.0.5 today). A mismatch fails at load, not compile, and — unlike the
`abstractionsVersion` gate — is not caught up front with a clean message; it surfaces as a runtime load or
binding error.

### Building views: prefer code over XAML

Contribution points hand back a `Func<Control>`. You can build that control however you like, but
**compiled XAML is pinned to the exact Avalonia build** and is the most fragile part of a plugin. The
sturdiest approach — used by all three example plugins — is to **build controls in C#** (`new StackPanel { ... }`).
Your controls inherit the host's theme because they live in the host's visual tree. Do **not** ship
app-wide styles or `ResourceDictionary` merges; scope any styling to your own root control.

## Storage and actions

```csharp
public void Initialize(ICockpitHost host)
{
    // Persist settings per-plugin (stored in cockpit.json under your plugin's slice):
    var repo = host.Storage.Get<string>("repo") ?? "";
    host.Storage.Set("repo", "owner/name");

    // Act on the running session:
    if (host.Actions.HasActiveSession)
        await host.Actions.InjectIntoActiveSessionAsync("some prompt text");
    else
        await host.Actions.SetClipboardTextAsync("some prompt text");
}
```

## Build, package, install

1. **Build:** `dotnet build -c Release`. The output folder holds your DLL, `.deps.json` and `plugin.json`
   (and none of the shared assemblies — verify that).
2. **Package:** zip the output folder's contents so `plugin.json` sits at the **zip root**:
   ```powershell
   Compress-Archive -Path bin/Release/net10.0/* -DestinationPath my-plugin-1.0.0.zip
   ```
3. **Install:** in the cockpit, **Options → Plugins → Install from zip…**, pick the zip, then **Review &
   enable** and consent. Enabling takes effect on the **next restart** (a plugin can't be loaded live) — a
   **"Restart cockpit now"** button (#53) appears right there once one is pending, so you don't have to close
   and relaunch the app by hand.

## Installing, enabling, disabling, removing

What actually happens under the hood, so you can reason about the "restart to apply" behaviour:

- **Install from zip.** The zip is extracted into a staging folder (rejecting any entry that would escape
  it — a zip-slip guard), its root `plugin.json` is parsed and its `abstractionsVersion` checked against the
  host's, and only then is the staging folder moved into `plugins/<id>/`. Installing over an existing folder
  for the same `id` **replaces it outright**, which is what re-triggers consent below (a changed entry
  assembly means a changed hash).
- **First load / consent.** On startup the host scans `plugins/`, hashes each plugin's entry assembly
  (SHA-256), and decides what to do with it:
  - Never seen before → **needs consent**: the first-load dialog shows name/version/author/path/hash and the
    "runs unsandboxed" warning; only an explicit **Enable** click pins the hash into `cockpit.json` and
    enables it.
  - Previously enabled and the hash still matches the pinned one → **loads normally**.
  - Previously enabled but the hash **changed** (you rebuilt it, updated it, or it was tampered with) →
    **needs consent again**, same dialog, same pin-on-enable.
  - Previously disabled → **stays disabled** (no dialog) until you re-enable it in the manager.
  - `abstractionsVersion` mismatch → refused outright, regardless of any of the above.
- **Disable.** Removes its UI contributions and calls `Dispose()`, but the assembly stays loaded (and its
  folder stays locked) until the process exits — a loaded plugin cannot be truly unloaded.
- **Remove.** Drops a marker file in the plugin's folder; the folder is actually deleted at the **next
  startup** (once the previous process has released the file lock), not immediately.
- **Restart to apply.** Because of the above, every state change that isn't purely "remove UI" — a fresh
  install's services, a version update, a real folder deletion — only takes full effect after restarting the
  cockpit. The manager surfaces this in its messaging; don't expect a freshly-installed plugin's
  `ConfigureServices` registrations to be live before that restart.
- **This does not apply to settings (#52).** Saving your plugin's settings view (the gear's Save button)
  never needs a restart — it's a plain `IPluginStorage` write, not a code load. A settings-backed value read
  fresh on every access (the common pattern; see the example plugins) reflects the save immediately, and a
  dialog opened via `ShowDialogAsync`/`AddSideMenuButton` is rebuilt fresh each time it opens. Only a
  contribution that cached settings-derived data at construction (e.g. a side-menu section's already-fetched
  list) needs to explicitly reload — via `host.OnSettingsSaved(...)`, documented in the [API reference](API-REFERENCE.md#icockpithost).

## Publishing the SDK as a NuGet (out-of-repo authors)

From the repo, `dotnet pack src/Cockpit.Plugins.Abstractions -c Release` produces
`Cockpit.Plugins.Abstractions.<version>.nupkg`. Host it on a feed (or use a local folder feed) and reference
it with `<PackageReference Include="Cockpit.Plugins.Abstractions" Version="1.x" ExcludeAssets="runtime" />`.

## Gotchas

- **Type-identity (the big one):** never ship `Cockpit.Plugins.Abstractions`, Avalonia or the DI
  abstractions in your folder — reference them `Private=false` / `ExcludeAssets=runtime`. Otherwise the host
  silently ignores your plugin. The same applies to `Avalonia.Controls.DataGrid` if you use it.
- **Unload is an illusion:** disabling removes the UI and disposes the plugin, but the assembly is freed only
  on restart. The manager says so.
- **Not sandboxed:** your plugin runs with the operator's rights. Do not do anything you wouldn't want a
  trusted local tool to do.
- **Version gate:** build against the host's abstractions major and Avalonia version, or the host rejects
  (abstractions major) or fails to load with a less clean error (Avalonia mismatch).
- **Secrets belong in `IPluginStorage`, never hardcoded.** Tokens, API keys and instance URLs (see the GitHub
  and YouTrack examples) should be entered through your settings view and persisted via `host.Storage`, not
  baked into source or shipped in the zip.
- **Register each contribution once.** `AddSettings` is documented as "call at most once"; treat
  `AddSideMenuButton`/`AddSideMenuSection` the same way — call them from `Initialize`, not from anywhere that
  could run twice.

## Publishing a plugin store

A **store** is any public location serving an `index.json` catalogue plus the plugin zips it lists. The
cockpit adds a store under **Options → Plugins → Plugin stores**, **Browse**s it, and installs or updates
from it — every download still goes through the normal validation + first-load consent (and, when the index
supplies a `sha256`, an integrity check on the downloaded zip against it before it's ever handed to the
installer).

**The official store.** The cockpit ships pre-configured to know about
**[`https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins`](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)** —
on a genuine first run (no stores configured yet) it is seeded automatically so a store is available out of
the box. If you remove it, it is not silently re-added. Anyone can add their own store alongside or instead
of it, and can publish plugins there via a PR if they want them listed alongside the official ones.

### Point the cockpit at a store

Any of these work — the cockpit auto-detects the shape:

- **A GitHub repo:** `https://github.com/owner/repo` (or `.../tree/branch`) → it reads
  `https://raw.githubusercontent.com/owner/repo/<branch, default main>/index.json`.
- **A direct index URL:** `https://…/index.json`.
- **A base directory:** `https://…/store` → it appends `index.json`.

### The index — `index.json`

Zip paths are **relative to the index's location**. See
[example-store-index.json](example-store-index.json).

```json
{
  "name": "My Cockpit plugin store",
  "plugins": [
    {
      "id": "github-issues",
      "name": "GitHub Issues",
      "description": "One line shown in the catalogue.",
      "author": "You",
      "latestVersion": "1.0.0",
      "versions": [
        {
          "version": "1.0.0",
          "path": "github-issues/github-issues-1.0.0.zip",
          "abstractionsVersion": 1,
          "minHostVersion": "1.0.0",
          "sha256": "<sha-256 of the zip, hex lowercase — optional but recommended>",
          "notes": "Initial release."
        }
      ]
    }
  ]
}
```

`latestVersion` drives update detection (compared against the installed plugin's `version`); the full
`versions` history lets you keep older zips around. Compute a zip's checksum with
`(Get-FileHash plugin.zip -Algorithm SHA256).Hash.ToLower()` (PowerShell) or `sha256sum plugin.zip` — a
mismatch on download is rejected before the zip is ever handed to the installer. A typical repo layout:

```
index.json
github-issues/github-issues-1.0.0.zip
github-issues/github-issues-1.1.0.zip
```

Note that the catalogue is advertising only: the zip's own `plugin.json` remains the source of truth at
install time, and consent + hash pinning still apply exactly as for a manual zip install.

## Examples

Three complete, working plugins live under [`plugins-dev/`](../../plugins-dev), each built exactly as
described above (compile-only shared refs, code-built views, settings persisted via `host.Storage`). Between
them they exercise every contribution point:

- **[GitHub Issues](../../plugins-dev/Cockpit.Plugin.GitHubIssues)** — a settings view (GitHub CLI vs.
  single-repo HTTP mode, editable prompt template) plus a left-menu **button** that opens a searchable,
  sortable **dialog** (using the host's DataGrid) listing open issues across your repos (via `gh`) or one
  repo; clicking an issue injects a rendered prompt template into the active session.
- **[GitHub Pull Requests](../../plugins-dev/Cockpit.Plugin.GitHubPullRequests)** — the same settings
  pattern, but contributes an inline **side-menu section** (always visible under the session list, showing up
  to 5 open PRs) instead of a launcher button, plus a "view all" dialog. Demonstrates
  `AddSideMenuSection` end-to-end.
- **[YouTrack](../../plugins-dev/Cockpit.Plugin.YouTrack)** — mirrors the Pull Requests plugin's inline
  section + dialog, but is **HTTP-only** (a permanent token against a configured instance/project — YouTrack
  has no local CLI equivalent to `gh`). Good reference for a plugin whose only external dependency is a
  plain `HttpClient` and settings-stored credentials.

All three ship their `plugin.json`, use `ConfigureServices` as an empty no-op (their state lives in
`host.Storage` instead of the DI container), and fall back to `SetClipboardTextAsync` when
`HasActiveSession` is false.
