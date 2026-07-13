# Cockpit Plugin API Reference

Every type and method a plugin can call, from the one assembly you reference:
**`Cockpit.Plugins.Abstractions`**. For the how-to (project setup, manifest, packaging, install, stores),
see the [Plugin SDK guide](PLUGIN-SDK.md); this page is the method-by-method reference.

- **Contract version:** `AbstractionsContract.Version` (currently **`1`**). Your `plugin.json`'s
  `abstractionsVersion` must equal the host's major, or the host refuses to load the plugin.
- **Threading:** contribution callbacks (`Func<Control>`, `Action onInvoke`, `Save()`) run on the **UI
  thread**. `ICockpitActions` methods are async and safe to `await` from the UI thread.
- **Nullability:** the assembly is nullable-annotated; honour it.

---

## `AbstractionsContract`

```csharp
public static class AbstractionsContract
{
    public const int Version = 1;
}
```

The plugin-contract major. The host loads a plugin only when its manifest `abstractionsVersion` equals
this. The contract grows **additively** within a major (new members arrive as default interface methods on
`ICockpitHost`); a breaking change bumps `Version`.

---

## `ICockpitPlugin`

The entry point your plugin implements (`: IDisposable`). The host discovers it in your entry assembly.

```csharp
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    void ConfigureServices(IServiceCollection services);
    void Initialize(ICockpitHost host);
}
```

### `PluginMetadata Metadata { get; }`
Identity shown in the Plugins manager. Return a `PluginMetadata` (see below). Read early — keep it a plain
property with no side effects.

### `void ConfigureServices(IServiceCollection services)`
**Phase 1**, *before* the host builds its DI container — register your own services here.
- **Parameter** `services` — the host's service collection (from `Microsoft.Extensions.DependencyInjection.Abstractions`).
- **Note:** only runs at startup for an *already-enabled* plugin. A plugin enabled *this* session contributes
  its **UI** immediately (via `Initialize`) but its **services only after the next restart** (the container is
  already built). Keep this optional where you can. Leave the body empty if you register nothing.

### `void Initialize(ICockpitHost host)`
**Phase 2**, once the host and UI exist — register your contribution points through `host` (below). This is
where the plugin actually wires itself into the cockpit.
- **Parameter** `host` — the facade described next.

### `void Dispose()` *(from `IDisposable`)*
Runs when the plugin is **disabled** or the app exits — release timers, `HttpClient`s, subscriptions, etc.
The assembly is **not** unloaded until the process restarts (a loaded plugin cannot be truly unloaded), so
"disable" means *UI removed + `Dispose` called*.

---

## `ICockpitHost`

Handed to you in `Initialize`. The contract's only intended growth surface.

```csharp
public interface ICockpitHost
{
    IServiceProvider Services { get; }
    ICockpitActions Actions { get; }
    IPluginStorage Storage { get; }
    void AddSettings(Func<Control> createView);
    void AddSideMenuButton(string title, Action onInvoke);
    void AddSideMenuSection(string title, Func<Control> createView);
    void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView);  // default no-op
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
    void OnSettingsSaved(Action callback);                       // default no-op
    void AddSessionProvider(SessionProviderRegistration registration); // default no-op
    Task AddMcpServer(McpServerContribution contribution);       // default no-op, returns Task.CompletedTask
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync();   // default returns []
}
```

### `IServiceProvider Services { get; }`
The built host container. Resolve services you (or the host) registered:
`host.Services.GetRequiredService<MyService>()`. Prefer resolving your own registered services over
reaching into host internals.

### `ICockpitActions Actions { get; }`
Actions on the cockpit/session — see [`ICockpitActions`](#icockpitactions).

### `IPluginStorage Storage { get; }`
Your per-plugin key/value store — see [`IPluginStorage`](#ipluginstorage).

### `void AddSettings(Func<Control> createView)`
Registers your **settings view**, opened from the **gear** next to your plugin in the Plugins manager (there
is no top-level Options tab per plugin).
- **Parameter** `createView` — a factory returning your settings `Control`, invoked on the UI thread when the
  gear is clicked.
- **Call at most once.**
- If your control implements [`IPluginSettingsView`](#ipluginsettingsview), the host's dialog shows a **Save**
  button; otherwise just **Close**.
```csharp
host.AddSettings(() => new MySettingsControl(host.Storage));
```

### `void AddSideMenuButton(string title, Action onInvoke)`
Adds a **launcher button** to the left sidebar.
- **Parameters:** `title` — the button label; `onInvoke` — runs (UI thread) when clicked, typically to open a
  dialog via `ShowDialogAsync`.
```csharp
host.AddSideMenuButton("GitHub Issues", () => _ = host.ShowDialogAsync("Issues", () => BuildIssuesView()));
```

### `void AddSideMenuSection(string title, Func<Control> createView)`
Adds an **inline accordion section** under the session list — for small, always-visible content (not a heavy
panel).
- **Parameters:** `title` — the section header; `createView` — factory for the section's `Control`.

### `Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560)`
Opens a **modal dialog** over the main window hosting your content; you own the content control.
- **Parameters:** `title` — window title; `createContent` — factory for the dialog body; `width`/`height` —
  size in DIPs (defaults 720×560).
- **Returns:** a `Task` that completes when the dialog closes.
- The host provides a themed **DataGrid** app-wide, so your content may use it.
```csharp
await host.ShowDialogAsync("Issues", () => BuildIssuesView(), width: 900, height: 600);
```

### `void OnSettingsSaved(Action callback)`
Registers `callback` to run (UI thread) after **this plugin's own** settings are saved from the manager's
gear (#52) — i.e. your `IPluginSettingsView.Save()` returned `true`. Enabling/disabling/installing a plugin
still needs a restart (its assembly can't be unloaded/loaded live), but a settings change doesn't have to.
- **When you need this:** a contribution that read settings once at construction and cached the result — e.g.
  a side-menu section's already-fetched list (`AddSideMenuSection`) — should subscribe and reload.
- **When you don't:** a contribution that reads `Storage`-backed settings fresh on every access already
  reflects a save. A dialog opened via `ShowDialogAsync`/`AddSideMenuButton` is rebuilt fresh (its
  `createContent`/`onInvoke` factory runs again) each time it's opened, so it too already picks up a save
  without this.
- Default implementation is a no-op, so this is safe to skip if it doesn't apply to your plugin.
```csharp
internal sealed class MySideSectionControl : UserControl
{
    public MySideSectionControl(MySettings settings, ICockpitHost host)
    {
        // ...build the list from settings...
        host.OnSettingsSaved(() => _ = ReloadAsync());
    }
}
```

### `void AddSessionProvider(SessionProviderRegistration registration)`
Registers a new **session provider** (#45) — the plugin equivalent of the built-in Claude-CLI/Ollama/LM-Studio
providers. Once registered, it appears in the New-session/Manage-profiles provider picker, backed by the
plugin's own driver and config view. See [`SessionProviderRegistration`](#sessionproviderregistration) and the
[Sessions namespace](#the-sessions-namespace---provider-plugins) below for the full contract.
- **Parameter** `registration` — the provider's id, display name, driver factory, capabilities and config-view
  factory.
- Default no-op, so existing `ICockpitHost` implementations (test fakes, older plugin builds) keep compiling
  untouched — only the app's own host overrides it.
```csharp
host.AddSessionProvider(new SessionProviderRegistration(
    ProviderId: "my-plugin.my-provider",
    DisplayName: "My Provider",
    CreateDriverFactory: _ => new MyPluginSessionDriverFactory(),
    Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
    CreateConfigView: existingConfigJson => new MyProviderConfigView(existingConfigJson)));
```

### `Task AddMcpServer(McpServerContribution contribution)`
Registers (or updates) an HTTP MCP server in the **shared registry** (#60) — e.g. a remote MCP endpoint your
plugin knows how to build a URL/token for — so both session worlds (the local tool-loop and the Claude
fan-out) can use its tools without the user adding it by hand in the MCP-servers dialog. See
[`McpServerContribution`](#mcpservercontribution) below.
- **Parameter** `contribution` — name, URL, optional bearer token, and scope.
- **Idempotent upsert-by-name:** calling this again with the same `Name` refreshes the URL/token of an
  existing entry instead of adding a duplicate. Never force-changes an entry's enabled state or scope — a
  server the user disabled, rescoped, or deleted from the dialog stays that way.
- Returns a `Task` because the upsert persists to disk; call it fire-and-forget (`_ = host.AddMcpServer(...)`)
  from a synchronous callback such as `Initialize` or an `OnSettingsSaved` handler.
- Default no-op, same compatibility rationale as `AddSessionProvider`.
```csharp
_ = host.AddMcpServer(new McpServerContribution(
    Name: "My Service: Prod",
    Url: "https://my-service.example.com/mcp",
    BearerToken: myToken));
```

### `void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView)`
Adds a small control to **every session's header bar**, built once per session and handed that session's own
[`IPluginSessionContext`](#ipluginsessioncontext) — for status that belongs to the session it describes (the git
state of the repo it is working in, say) rather than to the cockpit as a whole.
- **Keep it compact.** The header is a strip: an indicator with a tooltip, not a panel.
- The same control renders in both session kinds (SDK chat and TTY terminal), so you write it once.
- Prefer this over a side-menu section when the thing you show is *about one session*. A sidebar section that
  follows "whichever session is selected" says nothing about the other panes on screen.
- Default no-op, same compatibility rationale as `AddSessionProvider`.

```csharp
host.AddSessionHeaderItem(session => new MyIndicator(host, session));
```

#### `IPluginSessionContext`
One session, for as long as its panel exists — where [`ICockpitSessionObserver`](#the-sessions-namespace--provider-plugins)
follows whichever session is *selected*, this is bound to the one your control sits in.

| Member | Meaning |
|---|---|
| `string? WorkingDirectory` | The directory this session is working in; null until known (an SDK session before its init event). |
| `event EventHandler? WorkingDirectoryChanged` | The directory became known or changed — re-scope. |
| `event EventHandler<SessionOutputText>? OutputProduced` | Each chunk of text **this** session produced, verbatim. Substring-scan it for a signal (a git command, a pushed branch, …). |

Events are raised on the UI thread, so a handler can touch its controls directly.

### `Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync()`
The cockpit's configured **session profiles**: which identities exist and where each keeps its provider state
on disk. For a plugin that reads a provider's on-disk artefacts — the Claude CLI's transcripts, say — this is
how you find the directories the operator actually configured instead of guessing at the well-known ones.
- Read **fresh on every call**, so a profile added or edited after your plugin initialised is picked up without
  a restart. Call it per operation rather than caching it at construction.
- Default returns an empty list, same compatibility rationale as `AddSessionProvider`.

```csharp
public sealed record PluginProfileInfo(string Label, string Provider, string ConfigDirectory);
```

| Property | Meaning |
|---|---|
| `Label` | Display name, as shown in the profile picker. |
| `Provider` | The host's provider name — `ClaudeCli`, `Ollama`, `LmStudio`, `Plugin`. A string, not an enum, so the contract does not change every time the host gains a provider: match on the ones you care about and ignore the rest. |
| `ConfigDirectory` | The provider's per-profile config directory (a Claude-CLI profile's `CLAUDE_CONFIG_DIR`, holding that identity's credentials, config and `projects/` transcripts). Empty for a provider that keeps no such directory. |

```csharp
var profiles = await host.GetProfilesAsync();
var claudeConfigDirs = profiles
    .Where(profile => profile.Provider == "ClaudeCli" && profile.ConfigDirectory.Length > 0)
    .Select(profile => profile.ConfigDirectory);
```

---

## `ICockpitActions`

Act on the cockpit and the running session.

```csharp
public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);
    Task InjectIntoActiveSessionAsync(string text);
    bool HasActiveSession { get; }
}
```

### `Task SetClipboardTextAsync(string text)`
Puts `text` on the system clipboard. Use as a fallback when there is no active session to inject into.

### `Task InjectIntoActiveSessionAsync(string text)`
Injects `text` into the **currently selected** session — appended to the input box for an SDK/local session,
written to the pty for a TTY session. **No-op when `HasActiveSession` is false.**
```csharp
if (host.Actions.HasActiveSession)
    await host.Actions.InjectIntoActiveSessionAsync(prompt);
else
    await host.Actions.SetClipboardTextAsync(prompt);
```

### `bool HasActiveSession { get; }`
True when a session is selected (so `InjectIntoActiveSessionAsync` will land). Check it before injecting.

---

## `IPluginStorage`

Per-plugin key/value storage, persisted in a plugin-scoped slice of the host's `cockpit.json`. Values are
JSON-serialized.

```csharp
public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
}
```

### `T? Get<T>(string key)`
Reads and deserializes the value for `key`, or `default(T)` (e.g. `null`) if unset. Provide a fallback:
`host.Storage.Get<string>("repo") ?? ""`.

### `void Set<T>(string key, T value)`
Serializes and persists `value` under `key`. Works for primitives and your own DTO types.
```csharp
host.Storage.Set("repo", "owner/name");
host.Storage.Set("options", new MyOptions { Token = "…", Filter = "open" });
```

---

## `IPluginSettingsView`

Optional interface your **settings control** (the one passed to `AddSettings`) implements to get a standard
**Save** button in the host's settings dialog.

```csharp
public interface IPluginSettingsView
{
    bool Save();
}
```

### `bool Save()`
Persist the settings (typically via `host.Storage`). **Return `true`** to close the dialog, **`false`** to keep
it open (e.g. validation failed). A settings view that applies changes live can skip this interface and just
gets a Close button.
```csharp
public sealed class MySettingsControl : UserControl, IPluginSettingsView
{
    public bool Save()
    {
        if (string.IsNullOrWhiteSpace(_repo.Text)) return false; // keep open
        _storage.Set("repo", _repo.Text);
        return true;                                             // close
    }
}
```

---

## `PluginMetadata`

The identity you return from `ICockpitPlugin.Metadata`.

```csharp
public sealed record PluginMetadata(string Id, string DisplayName, string Version, string? Author, string? Description);
```

| Field | Type | Meaning |
|---|---|---|
| `Id` | `string` | Stable identity (match your `plugin.json` `id`). |
| `DisplayName` | `string` | Shown in the Plugins manager. |
| `Version` | `string` | Your plugin's version. |
| `Author` | `string?` | Optional. |
| `Description` | `string?` | Optional one-liner. |

```csharp
public PluginMetadata Metadata { get; } =
    new("github-issues", "GitHub Issues", "1.0.0", "You", "Browse and inject GitHub issues.");
```

---

## The `Sessions` namespace — provider plugins

Everything under `Cockpit.Plugins.Abstractions.Sessions`, used with `ICockpitHost.AddSessionProvider` (#45) to
register a plugin as a **new selectable session provider** — the same picker slot as the built-in Claude
CLI / Ollama / LM Studio providers. Three real plugins in [`plugins-dev/`](../../plugins-dev) exercise this:
**Gemini/OpenAI Provider** and **GitHub Models** (both a persistent `IChatClient` over an OpenAI-compatible
endpoint) and **CLI Agent Provider** (a subprocess-per-turn driver around the `codex` CLI).

This is a deliberately **narrow** contract — a trimmed mirror of the host's own internal `ISessionDriver` —
covering only what a third-party HTTP or subprocess provider can realistically support. There is no
Claude-CLI-only live model switch, plan mode, thinking-budget control, or always-allow rule persistence; the
host's own adapter (`PluginSessionDriverAdapter`, internal to the app) wraps your driver to satisfy the real
`ISessionDriver` contract and no-ops the members this interface has no equivalent for.

### `SessionProviderRegistration`

```csharp
public sealed record SessionProviderRegistration(
    string ProviderId,
    string DisplayName,
    Func<IServiceProvider, IPluginSessionDriverFactory> CreateDriverFactory,
    PluginSessionCapabilities Capabilities,
    Func<string?, IPluginProviderConfigView> CreateConfigView,
    string DefaultBaseUrl = "");
```

What you hand to `host.AddSessionProvider(...)` in `Initialize`.

| Field | Meaning |
|---|---|
| `ProviderId` | Stable id **namespaced by your plugin** (e.g. `"gemini-provider.gemini"`) so two plugins can never collide. Persisted on a profile — **must not change** once profiles exist under it. |
| `DisplayName` | Shown in the provider picker, e.g. `"Gemini (OpenAI-compatible)"`. |
| `CreateDriverFactory` | Builds your `IPluginSessionDriverFactory`, given the host's service provider. Usually `_ => new MyDriverFactory()` — most provider plugins keep no shared state. |
| `Capabilities` | What your driver supports — see [`PluginSessionCapabilities`](#pluginsessioncapabilities). |
| `CreateConfigView` | Builds the "add/edit profile" config view; argument is the existing config JSON (edit) or `null` (add). |
| `DefaultBaseUrl` | Pre-filled default base URL for your config view, when you have one. |

A plugin can register **more than one** provider from a single `Initialize` — the Gemini/OpenAI plugin
registers `"gemini-provider.gemini"` and `"gemini-provider.openai"` from the same `CreateDriverFactory`
implementation, differing only in `DefaultBaseUrl`:

```csharp
public void Initialize(ICockpitHost host)
{
    host.AddSessionProvider(new SessionProviderRegistration(
        ProviderId: "gemini-provider.gemini",
        DisplayName: "Gemini (OpenAI-compatible)",
        CreateDriverFactory: _ => new OpenAiCompatPluginSessionDriverFactory(),
        Capabilities: new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false),
        CreateConfigView: json => new OpenAiCompatProviderConfigView(json, GeminiDefaultBaseUrl),
        DefaultBaseUrl: GeminiDefaultBaseUrl));
}
```

### `IPluginSessionDriverFactory`

```csharp
public interface IPluginSessionDriverFactory
{
    IPluginSessionDriver Create(string configJson);
}
```

Creates the driver for one profile. `configJson` is the profile's opaque config string — **your own record's
shape**, serialized by your `IPluginProviderConfigView.TryGetConfigJson` and deserialized back here; the host
never inspects it.

### `IPluginSessionDriver`

```csharp
public interface IPluginSessionDriver : IAsyncDisposable
{
    PluginSessionCapabilities Capabilities { get; }
    string? SessionId { get; }
    Task StartAsync(string? model = null, CancellationToken cancellationToken = default);
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);
    Task InterruptAsync(CancellationToken cancellationToken = default);
    Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PluginSessionEvent> Events { get; }
    Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default); // default no-op
}
```

Drives a single, persistent, multi-turn conversation and exposes it as a typed event stream.

| Member | Meaning |
|---|---|
| `Capabilities` | What this instance supports (usually mirrors the registration's, but can vary per-config). |
| `SessionId` | The provider's own session id, once known; `null` before that. |
| `StartAsync` | Starts the underlying session. Call once before `SendUserMessageAsync`/`Events` produce anything. `model`, when set, selects the model for this session. |
| `SendUserMessageAsync` | Sends a user message; the session stays open for further turns. |
| `InterruptAsync` | Interrupts the current in-flight turn, if any. |
| `RespondToPermissionAsync` | Resolves an outstanding `PluginPermissionRequested` — the operator's allow/deny, correlated on `toolUseId`. Only relevant if `Capabilities.SupportsPermissions`. |
| `Events` | The live, ordered stream of typed events — see below. |
| `SetAutoApproveToolsAsync` | Toggles per-tool-call approval prompts on/off. Default no-op — a driver with no tool source of its own has nothing to gate. |
| `DisposeAsync` *(`IAsyncDisposable`)* | Tears down the subprocess/HTTP client/etc. |

### `PluginSessionCapabilities`

```csharp
public sealed record PluginSessionCapabilities(bool SupportsTools, bool SupportsPermissions, bool SupportsVision = false);
```

So the host's session UI renders or hides controls per provider instead of showing dead ones. `SupportsTools`
and `SupportsPermissions` gate the tool/approval affordances; `SupportsVision` gates image paste (a session
whose provider can't accept images shows a notice instead of silently dropping the pasted image). Leave
`SupportsVision: false` for now — the plugin-facing `IPluginSessionDriver.SendUserMessageAsync` has no images
parameter yet, so a plugin can't actually back it (setting it true would be an unbackable promise); it becomes
usable once that lands. There is deliberately nothing here for live model switch, plan mode, or thinking budget
— a plugin driver couldn't back those, so the host always reports them unsupported for a plugin-driven session.

### `IPluginProviderConfigView`

```csharp
public interface IPluginProviderConfigView
{
    Control View { get; }
    bool TryGetConfigJson(out string configJson);
}
```

Your provider's "add/edit profile" settings panel, parallel to `IPluginSettingsView`. Constructed with the
existing config JSON (edit) or `null` (add) — pre-fill your fields from it.

| Member | Meaning |
|---|---|
| `View` | The control hosting your config fields, embedded in the profile editor. |
| `TryGetConfigJson` | Validates the current field values and serializes them. Return `false` (and no JSON) on validation failure, keeping the editor open. |

```csharp
internal sealed class MyProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _apiKey = new();
    public Control View { get; }

    public MyProviderConfigView(string? existingConfigJson)
    {
        if (existingConfigJson is not null)
        {
            var existing = JsonSerializer.Deserialize<MyConfig>(existingConfigJson)!;
            _apiKey.Text = existing.ApiKey;
        }
        View = new StackPanel { Children = { new TextBlock { Text = "API key" }, _apiKey } };
    }

    public bool TryGetConfigJson(out string configJson)
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text)) { configJson = ""; return false; }
        configJson = JsonSerializer.Serialize(new MyConfig(_apiKey.Text));
        return true;
    }
}
```

### The event vocabulary — `PluginSessionEvent` and its subtypes

Every event `IPluginSessionDriver.Events` can yield derives from the abstract `PluginSessionEvent`
(`SessionId` on the base type, so every event carries it once known):

| Type | Fields | Meaning |
|---|---|---|
| `PluginSessionInitialized` | `Tools: IReadOnlyList<string>` | Reported once at the start of the stream — the tool names available, if any. |
| `PluginAssistantTextDelta` | `BlockIndex: int`, `Text: string` | An incremental chunk of assistant text while streaming a turn. |
| `PluginToolUseRequested` | `ToolUseId`, `ToolName`, `InputJson` | The model requested a tool call. |
| `PluginToolResult` | `ToolUseId`, `Content`, `IsError: bool` | The result of a previously requested tool call. |
| `PluginPermissionRequested` | `ToolUseId`, `ToolName`, `InputJson` | The driver is asking the host to allow or deny a tool call (only if `SupportsPermissions`). |
| `PluginTurnCompleted` | `Subtype`, `Result: string?`, `IsError: bool`, `StopReason: string?` | A turn finished. |
| `PluginSessionError` | `Message: string` | Something went wrong in the driver itself (request failure, parse failure, ...). |

The host's driver adapter maps each of these to its internal `SessionEvent` counterpart, so the rest of
the app sees one event vocabulary regardless of which driver produced it.

---

## The `Mcp` namespace — MCP server registration

Everything under `Cockpit.Plugins.Abstractions.Mcp`, used with `ICockpitHost.AddMcpServer` (#60) to register
an **HTTP MCP server** into the shared registry — e.g. the YouTrack plugin registering each configured
instance's JetBrains remote MCP endpoint so sessions get YouTrack tools without the user adding the server by
hand.

### `McpServerContribution`

```csharp
public sealed record McpServerContribution(
    string Name,
    string Url,
    string? BearerToken = null,
    McpContributionScope Scope = McpContributionScope.All);
```

| Field | Meaning |
|---|---|
| `Name` | Unique display name / registry key, e.g. `"YouTrack: Prod"`. Drives the idempotent upsert-by-name — calling `AddMcpServer` again with the same `Name` refreshes the existing entry's URL/token instead of adding a duplicate. |
| `Url` | The server's HTTP endpoint, e.g. `https://x.youtrack.cloud/mcp`. |
| `BearerToken` | Static bearer token sent as `Authorization: Bearer …`, or `null`/empty for no auth. |
| `Scope` | Which session worlds this server fans out to **on first registration** — see below. |

### `McpContributionScope`

```csharp
public enum McpContributionScope
{
    All,        // every session — both the local-model tool-loop and Claude Code
    LocalOnly,  // only local models (Ollama/LM Studio); never fanned out to Claude Code
    ClaudeOnly, // only fanned out to Claude Code; never hosted in the local-model tool-loop
}
```

The YouTrack plugin's own registration helper (real code from
[`plugins-dev/Cockpit.Plugin.YouTrack/YouTrackMcpRegistration.cs`](../../plugins-dev/Cockpit.Plugin.YouTrack/YouTrackMcpRegistration.cs)),
building one contribution per fully-configured instance and re-registering on every settings save via
`OnSettingsSaved`:

```csharp
internal static class YouTrackMcpRegistration
{
    public static IReadOnlyList<McpServerContribution> BuildContributions(IReadOnlyList<YouTrackInstance> instances) =>
        instances
            .Where(i => !string.IsNullOrWhiteSpace(i.InstanceUrl) && !string.IsNullOrWhiteSpace(i.Token))
            .Select(i => new McpServerContribution(Name: $"YouTrack: {i.Label}", Url: DeriveMcpEndpoint(i.InstanceUrl), BearerToken: i.Token))
            .ToList();
}

// in YouTrackPlugin.Initialize:
_RegisterMcpServers(host, settings);
host.OnSettingsSaved(() => _RegisterMcpServers(host, settings));

private static void _RegisterMcpServers(ICockpitHost host, YouTrackSettings settings)
{
    foreach (var contribution in YouTrackMcpRegistration.BuildContributions(settings.Instances))
        _ = host.AddMcpServer(contribution); // fire-and-forget: persists to disk
}
```

---

## Minimal plugin

```csharp
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

public sealed class MyPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new("my-plugin", "My Plugin", "1.0.0", "You", "Does a thing.");

    public void ConfigureServices(IServiceCollection services) { /* optional */ }

    public void Initialize(ICockpitHost host)
    {
        host.AddSideMenuButton("My Plugin", () =>
            _ = host.ShowDialogAsync("My Plugin", () => new TextBlock { Text = "Hello from a plugin!" }));
    }

    public void Dispose() { }
}
```

See the [Plugin SDK guide](PLUGIN-SDK.md) for the project file, manifest, packaging and install steps, and the
[GitHub Issues plugin](../../plugins-dev/Cockpit.Plugin.GitHubIssues) for a full example.
