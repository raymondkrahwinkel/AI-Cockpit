# Cockpit Plugin SDK

Build a plugin that extends the cockpit with its own Options tab, a left-menu section, and actions on the
active session — without touching the cockpit's own code. This guide is the reference; the
[GitHub Issues plugin](../../plugins-dev/Cockpit.Plugin.GitHubIssues) is a complete working example.

> **Trust model — read this first.** A plugin is a .NET assembly that runs **in-process, unsandboxed, with
> your account's permissions**. There is no security boundary (.NET cannot provide one for in-process
> plugins). The cockpit protects you only by requiring a **manual install** and a **first-load consent**
> that pins the assembly's SHA-256 — a changed file re-prompts. **Only install plugins you trust.**

## What a plugin can do

A plugin implements one interface, `ICockpitPlugin`, and contributes through the host (`ICockpitHost`):

| Contribution point | Method | Result |
|---|---|---|
| Options tab | `host.AddOptionsTab(title, () => control)` | A tab in **Options**, next to Notifications/Sessions/Voice/Plugins. |
| Left-menu section | `host.AddSideMenuSection(title, () => control)` | An accordion under the session list in the sidebar. |
| Act on the session | `host.Actions` | Inject text into the active session's prompt, or set the clipboard. |
| Persist settings | `host.Storage` | Per-plugin key/value storage in `cockpit.json`. |
| Register services | `plugin.ConfigureServices(services)` | Add your own services to the host DI container (phase 1). |

## The contract

All of these live in `Cockpit.Plugins.Abstractions` (the only assembly you must reference):

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
    void AddOptionsTab(string title, Func<Control> createView);
    void AddSideMenuSection(string title, Func<Control> createView);
    ICockpitActions Actions { get; }
    IPluginStorage Storage { get; }
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

public sealed record PluginMetadata(string Id, string DisplayName, string Version, string? Author, string? Description);
```

### Two-phase lifecycle

1. **`ConfigureServices(IServiceCollection)`** runs **before** the host builds its DI container, so you can
   register your own services. It only runs at startup for an already-enabled plugin — a plugin enabled
   *this* session (its consent just given) contributes its **UI** immediately but its **services only after
   the next restart** (the container is already built). Keep `ConfigureServices` optional where you can.
2. **`Initialize(ICockpitHost)`** runs once the host and UI exist. Register your contribution points here.
3. **`Dispose()`** runs when the plugin is disabled or the app exits. Note: the assembly is **not** unloaded
   until the process restarts (a loaded plugin cannot be truly unloaded) — "disable" means UI off + Dispose.

## The manifest — `plugin.json`

Ships in the plugin's folder root. Parsed and validated **before** anything is loaded.

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
| `id` | yes | Stable identity; normalized to the install folder slug. |
| `name` | yes | Display name in the Plugins manager. |
| `version` | yes | Your plugin's version. |
| `entryAssembly` | yes | The DLL carrying your `ICockpitPlugin`. |
| `entryType` | no | Fully-qualified entry type; omit to let the host find the single `ICockpitPlugin`. |
| `abstractionsVersion` | yes | The SDK **major** you built against — must equal the host's (`AbstractionsContract.Version`). |
| `minHostVersion`, `description`, `author` | no | Metadata. |

## Project setup

The one rule that matters: **the shared assemblies must not ship in your plugin folder.** The host provides
`Cockpit.Plugins.Abstractions`, Avalonia and the DI abstractions; if your folder carried its own copies,
`ICockpitPlugin` would be a *different type* across the load boundary and the host would silently ignore
your plugin (the type-identity pitfall). Reference them **compile-only**:

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
version the host uses** (12.0.5 today). A mismatch fails at load, not compile.

### Building views: prefer code over XAML

Contribution points hand back a `Func<Control>`. You can build that control however you like, but
**compiled XAML is pinned to the exact Avalonia build** and is the most fragile part of a plugin. The
sturdiest approach — used by the example plugin — is to **build controls in C#** (`new StackPanel { ... }`).
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
   enable** and consent. Enabling takes effect on the **next restart** (a plugin can't be loaded live).

## Publishing the SDK as a NuGet (out-of-repo authors)

From the repo, `dotnet pack src/Cockpit.Plugins.Abstractions -c Release` produces
`Cockpit.Plugins.Abstractions.<version>.nupkg`. Host it on a feed (or use a local folder feed) and reference
it with `<PackageReference Include="Cockpit.Plugins.Abstractions" Version="1.x" ExcludeAssets="runtime" />`.

## Gotchas

- **Type-identity (the big one):** never ship `Cockpit.Plugins.Abstractions`, Avalonia or the DI
  abstractions in your folder — reference them `Private=false` / `ExcludeAssets=runtime`. Otherwise the host
  silently ignores your plugin.
- **Unload is an illusion:** disabling removes the UI and disposes the plugin, but the assembly is freed only
  on restart. The manager says so.
- **Not sandboxed:** your plugin runs with the operator's rights. Do not do anything you wouldn't want a
  trusted local tool to do.
- **Version gate:** build against the host's abstractions major and Avalonia version, or the host rejects
  (or fails to load) your plugin with a clear message.

## Scaffold a new plugin

Use the `dotnet new` template in [`templates/cockpit-plugin`](../../templates/cockpit-plugin):

```bash
dotnet new install ./templates/cockpit-plugin
dotnet new cockpit-plugin -n My.Plugin -o plugins-dev/My.Plugin
```
