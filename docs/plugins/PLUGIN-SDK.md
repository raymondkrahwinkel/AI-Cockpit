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
| Session header item | `host.AddSessionHeaderItem(session => control)` | A small control in **every session's header bar**, built once per session and handed that session's own [`IPluginSessionContext`](API-REFERENCE.md#ipluginsessioncontext) — for status that belongs to one session. See [Session header items](#session-header-items--status-that-belongs-to-one-session). |
| Conversation picker | `host.AddConversationPicker(registration)` | Lends your history-browsing to the **New-session dialog**: it grows a **Search…** button next to "resume by session id", which runs your picker. See [Conversation pickers](#conversation-pickers--let-the-operator-choose-a-conversation-to-resume). |
| Read the profiles | `host.GetProfilesAsync()` | The configured session profiles (label, provider, config directory) — how you find where a provider keeps its state on disk instead of guessing. |
| Session provider | `host.AddSessionProvider(registration)` | Registers a new selectable **session provider** (#45) — your own `IPluginSessionDriver` becomes a picker entry alongside Claude CLI/Ollama/LM Studio. See [Provider plugins](#provider-plugins--registering-a-session-driver). |
| MCP server | `host.AddMcpServer(contribution)` | Upserts an HTTP MCP server into the **shared registry** (#60) so sessions can use its tools without the user adding it by hand. See [MCP server registration](#mcp-server-registration). |
| Act on the session | `host.Actions` | Inject text into the active session's prompt, or set the clipboard. |
| Observe the sessions | `host.Sessions` | The **selection-following** read surface: the active session's working directory, its `ActivePaneId`, and a stream of every session's output. (For one *specific* session, use a session header item's context instead — and match its `PaneId` against `ActivePaneId` when a dialog acts "on the current session".) |
| Keyboard shortcut | `host.AddShortcut(shortcut)` | A gesture and a command-palette entry, listed in Options → Shortcuts alongside the app's own. |
| Toast | `host.ShowToast(message, severity, actionLabel, onAction)` | A transient in-app notification with an optional single action button — how you tell the operator something happened while they were looking elsewhere. |
| Persist settings | `host.Storage` | Per-plugin key/value storage in `cockpit.json`. |
| Live-apply settings | `host.OnSettingsSaved(callback)` | Re-run a callback after your settings are saved, without needing an app restart. |
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
    void OnSettingsSaved(Action callback);                      // re-run after your settings are saved
    void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView); // a control in every session's header
    void AddConversationPicker(ConversationPickerRegistration picker);          // browse history for the New-session dialog
    void AddShortcut(PluginShortcut shortcut);                  // a gesture + command-palette entry
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information,
                   string? actionLabel = null, Action? onAction = null);      // an in-app notification
    void AddSessionProvider(SessionProviderRegistration registration); // register a new session provider (#45)
    Task AddMcpServer(McpServerContribution contribution);      // upsert an MCP server into the registry (#60)
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync();  // the configured profiles and where they keep state
    ICockpitSessionObserver Sessions { get; }                   // the selection-following read surface
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

## Provider plugins — registering a session driver

A plugin can add a whole new **session provider** — the same picker slot as the built-in Claude CLI / Ollama
/ LM Studio choices — by implementing a small driver and handing it to `host.AddSessionProvider(...)`. Two
shapes exist in [`plugins-dev/`](../../plugins-dev):

- **Persistent chat client** (Gemini/OpenAI Provider, GitHub Models): one long-lived `IChatClient` per
  session, talking an OpenAI-compatible endpoint via `Microsoft.Extensions.AI` — the same stack the host uses
  internally for Ollama/LM Studio.
- **Subprocess-per-turn** (CLI Agent Provider): shells out to a CLI (`codex exec --json`, resumed for
  follow-up turns) and maps its JSON-lines output onto the event vocabulary.

The contract, all under `Cockpit.Plugins.Abstractions.Sessions`, is deliberately **narrow** — a trimmed mirror
of the host's own internal session-driver contract, covering only what a third-party provider can realistically
support (no live model switch, plan mode, thinking budget, or always-allow persistence; see
[`PluginSessionCapabilities`](API-REFERENCE.md#pluginsessioncapabilities)). Full member-by-member reference:
**[API reference → The `Sessions` namespace](API-REFERENCE.md#the-sessions-namespace--provider-plugins)**.

Minimal shape, from the real Gemini/OpenAI provider plugin:

```csharp
public sealed class MyProviderPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new("my-provider", "My Provider", "0.1.0", "You", "...");

    public void ConfigureServices(IServiceCollection services) { } // no shared state — a driver per session

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId: "my-provider.my-model",           // namespaced — never rename once profiles use it
            DisplayName: "My Model",
            CreateDriverFactory: _ => new MyPluginSessionDriverFactory(),
            Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false, SupportsVision: false), // SupportsVision stays false until the driver can carry images (see API reference)
            CreateConfigView: existingConfigJson => new MyProviderConfigView(existingConfigJson),
            DefaultBaseUrl: "https://api.example.com/v1"));
    }

    public void Dispose() { }
}
```

You implement three pieces: `IPluginSessionDriverFactory.Create(configJson)` (deserialize the profile's opaque
config, build your driver), `IPluginSessionDriver` (start/send/interrupt/events — the actual conversation),
and `IPluginProviderConfigView` (the add/edit-profile panel that produces that config JSON). A profile created
against your provider persists `ProviderId` + the config JSON; the host's driver adapter (internal) wraps your
`IPluginSessionDriver` to satisfy its own full session-driver contract and no-ops whatever your capabilities
don't support.

## Session header items — status that belongs to one session

`host.AddSessionHeaderItem(session => control)` puts a small control in **every session's header bar**. It is
built once per session panel and handed that session's own `IPluginSessionContext`, so it shows the state of the
session it sits in — not of whichever session happens to be selected.

That distinction is the whole point. A cockpit shows several sessions at once; a sidebar section that follows
the selection says nothing about the other panes on screen.

```csharp
host.AddSessionHeaderItem(session => new MyIndicator(host, session));
```

The context gives you exactly two things — where that session works, and what it produces:

```csharp
public interface IPluginSessionContext
{
    string? WorkingDirectory { get; }                            // null until known
    event EventHandler? WorkingDirectoryChanged;                 // re-scope
    event EventHandler<SessionOutputText>? OutputProduced;       // this session's output, verbatim
}
```

**Keep it compact.** The header is a strip: an indicator with a tooltip, not a panel. The Git status plugin is
the worked example — a coloured dot and the branch, the counts on hover, re-reading itself when that session
runs a git command (it substring-scans `OutputProduced` for one) and clicking drops the summary into that
session. The same control renders in both session kinds (SDK chat and TTY terminal), so you write it once.

## Conversation pickers — let the operator choose a conversation to resume

The New-session dialog can resume an earlier conversation by id, and typing an id by hand is a poor way to find
one. But the cockpit cannot browse a provider's history itself: a transcript is one provider's own format, which
is precisely why it lives in a plugin.

So a plugin that *can* browse that history lends it to the dialog. Register a picker, and the dialog grows a
**Search…** button next to the id field that runs it:

```csharp
host.AddConversationPicker(new ConversationPickerRegistration("Search transcripts", async () =>
{
    string? picked = null;
    await host.ShowDialogAsync("Search transcripts", () => new MySearchControl(id => picked = id));
    return picked;   // null = the operator cancelled
}));
```

No plugin registers one → no button, and the id can still be typed. The core stays ignorant of transcripts and
of Claude; it only knows that *someone* offers a picker. The transcript-search plugin is the worked example.

## MCP server registration

A plugin that talks to a service with its own remote MCP server (JetBrains YouTrack's is the shipping
example) can register it into the **shared MCP registry** with `host.AddMcpServer(...)`, so both session
worlds — the local-model tool-loop and the Claude Code fan-out — pick up its tools without the user adding the
server by hand in the MCP-servers dialog:

```csharp
_ = host.AddMcpServer(new McpServerContribution(
    Name: "My Service: Prod",             // upsert key — re-registering the same Name updates it in place
    Url: "https://my-service.example.com/mcp",
    BearerToken: myToken,                 // null/empty for no auth
    Scope: McpContributionScope.All));    // All / LocalOnly / ClaudeOnly
```

Call it from `Initialize` (first registration) and again from an `host.OnSettingsSaved(...)` callback whenever
the underlying URL/token can change — see the YouTrack plugin's `YouTrackMcpRegistration` for the real
pattern, referenced in full in the [API reference](API-REFERENCE.md#the-mcp-namespace--mcp-server-registration).
It's a fire-and-forget `Task` (the upsert persists to disk); the registration never overrides a state the user
already changed by hand (enabled/disabled, rescoped, or deleted).

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
[example-store-index.json](example-store-index.json) — a real excerpt from the official store — for a
complete file using every field below.

```json
{
  "name": "My Cockpit plugin store",
  "plugins": [
    {
      "id": "github-issues",
      "name": "GitHub Issues",
      "description": "One line shown in the catalogue.",
      "author": "You",
      "category": "Issue trackers",
      "icon": "🐛",
      "homepage": "https://github.com/you/your-repo/tree/main/docs",
      "repository": "https://github.com/you/your-repo",
      "featured": true,
      "published": "2026-07-11",
      "latestVersion": "1.1.0",
      "versions": [
        {
          "version": "1.1.0",
          "path": "github-issues/github-issues-1.1.0.zip",
          "abstractionsVersion": 1,
          "minHostVersion": "1.0.0",
          "sha256": "<sha-256 of the zip, hex lowercase — optional but recommended>",
          "notes": "gh CLI support, cross-repo issues, searchable/sortable dialog."
        },
        {
          "version": "1.0.0",
          "path": "github-issues/github-issues-1.0.0.zip",
          "abstractionsVersion": 1,
          "minHostVersion": "1.0.0",
          "sha256": "<sha-256 of the 1.0.0 zip>",
          "notes": "Initial release."
        }
      ]
    }
  ]
}
```

**Top level:**

| Field | Required | Meaning |
|---|---|---|
| `name` | yes | The store's display name, shown as the catalogue title in the store dialog. |
| `plugins` | yes | Array of plugin entries, described below. |
| `templates` | no | Array of workflow templates the store offers — flows somebody already drew. Described below; omit it and the store simply offers none. |

### Workflow templates — `templates[]`

A template is a **flow as text**: the same JSON the flow editor exports a flow to. Unlike a plugin there is no
assembly, nothing is loaded and no code runs at install time — the cockpit writes the file, and the flow appears in
the editor's "From template…" picker after a restart. It arrives **switched off**: a flow you have not read is not one
that should already be running.

That is not the same as harmless. A flow can carry a shell command, so the store shows what a template needs before
you install it, and reading it before arming it is your own check.

```json
"templates": [
  {
    "id": "you.ticket-to-agent",
    "name": "Ticket → branch → agent",
    "description": "Pick a ticket, cut the branch, put an agent on it.",
    "author": "You",
    "version": "1.0",
    "category": "Your store",
    "path": "templates/ticket-to-agent.json",
    "sha256": "<sha-256 of the flow's json, hex lowercase — optional but recommended>",
    "requires": ["youtrack"]
  }
]
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable identity, so an update is recognised as the same template rather than a second copy. |
| `name` | yes | What the store and the template picker show. |
| `description` | no | One line: what the flow does. |
| `author` | no | Who published it. |
| `version` | no | The published version, so an update to a template is a thing that can be seen. |
| `category` | no | The heading the picker files it under; defaults to the store's name. |
| `path` | yes | Where the flow's JSON sits, **relative to the index**. |
| `sha256` | no | Checksum of that JSON. Published, it is verified: what arrives is then what was published. |
| `requires` | no | The plugins whose steps the flow uses (`["youtrack"]`). Shown before install — a flow built on YouTrack's steps is no use without it, and finding that out on the canvas is finding it out too late. |

**Exporting a template:** open a flow in the editor and press **Export** — the file it writes is exactly what goes at
`path`. A plugin can also ship templates in code (`host.AddWorkflowTemplate`), which is how YouTrack and GitHub Issues
offer theirs.

**Per plugin (`plugins[]`):**

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable id — should match the id inside each version's `plugin.json`. |
| `name` | yes | Display name shown on the catalogue card. |
| `description` | yes | One-line summary shown on the card and in the detail panel. |
| `author` | no | Shown on the card/detail panel. |
| `category` | no | Groups the plugin under a sidebar category in the store dialog (e.g. `"Issue trackers"`, `"AI providers"`). Plugins with no category still show under "All". |
| `icon` | no | A single emoji shown on the card and as the plugin's icon elsewhere in the UI. |
| `homepage` | no | Link shown in the detail panel — typically your docs or README section for the plugin. |
| `repository` | no | Link to the plugin's source repository, shown in the detail panel. |
| `featured` | no | `true` pins/highlights the card (e.g. in a "Discover" section); default `false`. |
| `published` | no | ISO date string (`"YYYY-MM-DD"`) of the latest version's publish date; informational, shown in the detail panel. |
| `latestVersion` | yes | Drives update detection — compared against the installed plugin's `version` to decide whether the store dialog shows "Install" / "Update" / "Installed". |
| `versions` | yes | Array, newest first, full version history — see below. |

**Per version (`plugins[].versions[]`):**

| Field | Required | Meaning |
|---|---|---|
| `version` | yes | This version's version string. |
| `path` | yes | Zip location, **relative to the index's own location** (e.g. `github-issues/github-issues-1.1.0.zip`). |
| `abstractionsVersion` | yes | The `AbstractionsContract.Version` major this build targets — checked the same as a manual zip install. |
| `minHostVersion` | yes | Informational (not currently enforced as a gate, same as the manifest field). |
| `sha256` | no (recommended) | Hex-lowercase SHA-256 of the zip. A mismatch on download is rejected before the zip is ever handed to the installer. |
| `notes` | no | Shown as this version's changelog line in the detail panel. |

Compute a zip's checksum with `(Get-FileHash plugin.zip -Algorithm SHA256).Hash.ToLower()` (PowerShell) or
`sha256sum plugin.zip`. A typical repo layout, one folder per plugin id:

```
index.json
github-issues/github-issues-1.0.0.zip
github-issues/github-issues-1.1.0.zip
youtrack/youtrack-1.0.0.zip
youtrack/youtrack-1.1.0.zip
youtrack/youtrack-1.2.0.zip
```

Note that the catalogue is advertising only: the zip's own `plugin.json` remains the source of truth at
install time, and consent + hash pinning still apply exactly as for a manual zip install. The cockpit's
plugin-store dialog (categories sidebar, cards, detail panel, search/sort), the pre-seeded default store, and
the periodic update check all read this same file — see the [README](../../README.md#plugins--plugin-store)
for how they fit into the app.

### The official store, as a worked reference

**[github.com/raymondkrahwinkel/AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)**
is a real store you can use as a template: its `index.json` lists the `plugins-dev/` plugins with every field
above filled in, laid out exactly as `<plugin-id>/<plugin-id>-<version>.zip`. Clone its layout for your own
store, or open a PR against it to list your plugin alongside the official ones.

## Plugins that ship with the app

Two plugins are **bundled**: they are built with the cockpit, copied into its `bundled-plugins/` output, and
installed into the operator's plugins directory on startup — enabled, and without the consent dialog (it asks
whether you trust third-party code, and these came out of the very build that is asking).

They exist because they *used* to be core features. Transcript search parses Claude's own JSONL format, and git
status describes the repo one session works in; neither belongs in a core that drives several providers. Making
them plugins kept the core honest, and bundling them means an operator does not have to know they exist to have
what they always had.

Bundling never overrides the operator: a plugin they disable stays disabled and untouched on disk, and a version
they updated past ours from the store is not rolled back — only a newer bundled version replaces an older
installed one, keeping it enabled and keeping its settings. Nothing about this is special to first-party code:
it is the ordinary plugin loader, with the files put in place beforehand.

## Examples

Eight complete, working plugins live under [`plugins-dev/`](../../plugins-dev), each built exactly as described
above (compile-only shared refs, code-built views, settings persisted via `host.Storage`). Between them they
exercise every contribution point:

**UI contribution plugins:**

- **[GitHub Issues](../../plugins-dev/Cockpit.Plugin.GitHubIssues)** — a settings view (GitHub CLI vs.
  single-repo HTTP mode, editable prompt template) plus a left-menu **button** that opens a searchable,
  sortable **dialog** (using the host's DataGrid) listing open issues across your repos (via `gh`) or one
  repo; clicking an issue injects a rendered prompt template into the active session.
- **[GitHub Pull Requests](../../plugins-dev/Cockpit.Plugin.GitHubPullRequests)** — the same settings
  pattern, but contributes an inline **side-menu section** (always visible under the session list, showing up
  to 5 open PRs) instead of a launcher button, plus a "view all" dialog. Demonstrates
  `AddSideMenuSection` end-to-end.
- **[YouTrack](../../plugins-dev/Cockpit.Plugin.YouTrack)** — a left-menu button + dialog like GitHub Issues,
  but **HTTP-only** (a permanent token per configured instance — YouTrack has no local CLI equivalent to
  `gh`), with instance/project/state filters. Also the reference implementation for **MCP server
  registration**: it registers each fully-configured instance's JetBrains remote MCP endpoint via
  `host.AddMcpServer(...)` on `Initialize` and again on every settings save (`OnSettingsSaved`), so a session
  gets YouTrack tools without the user adding the server by hand.

- **[Git status](../../plugins-dev/Cockpit.Plugin.GitStatus)** — the reference for a **session header item**:
  a coloured dot and the branch of the repo *that session* works in, counts on hover, clicking drops the
  summary into it. Re-reads itself when the session runs a git command (it substring-scans the session's own
  `OutputProduced`, debounced so a command printing progress over several lines does not trigger five reads).
  Also ships a dialog listing every configured repo. **Bundled with the app.**
- **[Claude Transcript Search](../../plugins-dev/Cockpit.Plugin.TranscriptSearch)** — the reference for a
  **conversation picker** and for `GetProfilesAsync`: it finds Claude's transcripts through the profiles the
  operator actually configured rather than guessing at the well-known directories, opens on your ten most
  recent conversations, and lends its search to the New-session dialog so resuming one is a click instead of a
  typed session id. Contributes a shortcut (`Ctrl+F`) as well. **Bundled with the app.**

**Provider plugins (`host.AddSessionProvider`, #45):**

- **[Gemini / OpenAI Provider](../../plugins-dev/Cockpit.Plugin.GeminiProvider)** — registers **two**
  providers (Gemini and OpenAI) from one `Initialize`, both backed by the same persistent-`IChatClient` driver
  factory over an OpenAI-compatible chat-completions endpoint, differing only in default base URL. Chat-only
  capabilities (no tools/permissions). Experimental (0.x).
- **[GitHub Models](../../plugins-dev/Cockpit.Plugin.GitHubModelsProvider)** — the same OpenAI-compatible
  driver against GitHub's own Models endpoint (`models.github.ai/inference`), configured with a GitHub PAT
  (`models:read` scope) instead of a raw API key. Experimental (0.x).
- **[CLI Agent Provider](../../plugins-dev/Cockpit.Plugin.CliAgentProvider)** — registers Codex CLI as a
  provider driven as a **subprocess per turn** (`codex exec --json`, resumed via `codex exec resume
  <threadId>` for follow-up turns) instead of a persistent chat client — the reference implementation for a
  non-HTTP driver. `SupportsTools: true`, `SupportsPermissions: false` (no in-band tool-permission channel;
  the sandbox/approval mode is fixed per profile). Experimental (0.x).

They all ship their `plugin.json`, use `ConfigureServices` as an empty no-op (their state lives in
`host.Storage` or is minted fresh per session instead of living in the DI container), and the UI-contribution
plugins fall back to `SetClipboardTextAsync` when `HasActiveSession` is false.
