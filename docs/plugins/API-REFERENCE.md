---
title: API reference
category: extending
order: 20
summary: Every type and method in Cockpit.Plugins.Abstractions, with signatures and examples.
icon: 📐
---

# Cockpit Plugin API Reference

Every type and method a plugin can call, from the one assembly you reference:
**`Cockpit.Plugins.Abstractions`**. For the how-to (project setup, manifest, packaging, install, stores),
see the [Plugin SDK guide](PLUGIN-SDK.md); this page is the method-by-method reference.

- **Contract version:** `AbstractionsContract.Version` (currently **`2`**). Your `plugin.json`'s
  `abstractionsVersion` must equal the host's major, or the host refuses to load the plugin. Coming from `1`?
  See [Migrating from `bool Save()`](PLUGIN-SDK.md#migrating-from-bool-save--contract-1--2) — the settings
  contract is the only thing that changed.
- **Threading:** contribution callbacks (`Func<Control>`, `Action onInvoke`, `TryStage()` and the `commit` it
  hands back) run on the **UI thread**. `ICockpitActions` methods are async and safe to `await` from the UI
  thread.
- **Nullability:** the assembly is nullable-annotated; honour it.

---

## `AbstractionsContract` {#abstractionscontract}

```csharp
public static class AbstractionsContract
{
    public const int Version = 2;
}
```

The plugin-contract major. The host loads a plugin only when its manifest `abstractionsVersion` equals
this. The contract grows **additively** within a major (new members arrive as default interface methods on
`ICockpitHost`); a breaking change bumps `Version`.

**`2`** (host 0.26.0) — `IPluginSettingsView` no longer persists its own settings: `bool Save()` became
[`bool TryStage(out Action? commit, out string? error)`](#ipluginsettingsview). The only break so far, and the
only reason a contract-`1` plugin is refused.

---

## Capabilities — what a plugin can ask for {#capabilities}

`Cockpit.Plugins.Abstractions.Capabilities.CapabilityCatalog.All` is the fixed list of what this SDK offers,
with every member below grouped into the unit a manifest declares and an operator grants. It is also the
discovery list: if a contribution point is not in this table, it does not exist.

**Host-only.** The catalogue describes the host's own surface, so only the host can add to it — see
[AC-474](https://raymondkrahwinkel.myjetbrains.com/youtrack/issue/AC-474).

- **Risk** is `CapabilityRisk`: `Ambient` adds to the cockpit's surface and reads nothing of the operator's,
  `Sensitive` reads or writes state the plugin did not create, `Dangerous` acts with the operator's rights or
  opens egress. Only `Dangerous` lines up with `ConsentRisk.Dangerous`.
- **Since** is the host version the *capability* first existed in. An individual member added later still has
  its own `minHostVersion` — that stays `Directory.Build.props`' job.
- **Scope** names the keys a grant can be narrowed along; `—` means all-or-nothing.
- Each capability's one-line operator-facing summary lives on `PluginCapability.Summary`, so it is in the SDK's
  IntelliSense and in exactly one place.

| ID | Capability | Risk | Since | Scope | Contribution points |
|---|---|---|---|---|---|
| `ui.settings` | Its own settings screen | Ambient | 0.3.0 | — | `ICockpitHost.AddSettings`, `ICockpitHost.ShowSettingsAsync`, `ICockpitHost.HasSettings`, `ICockpitHost.OnSettingsSaved` |
| `ui.side-menu` | A button in the left menu | Ambient | 0.3.0 | — | `ICockpitHost.AddSideMenuButton`, `ICockpitHost.AddSideMenuSection`, `ICockpitHost.AddSideMenuButtonWithBadge` |
| `ui.commands` | Toolbar buttons and keyboard shortcuts | Ambient | 0.3.0 | — | `ICockpitHost.AddToolbarAction`, `ICockpitHost.AddShortcut` |
| `ui.panels` | Panels on the dashboard and the dock rail | Ambient | 0.3.0 | — | `ICockpitHost.AddWidget`, `ICockpitHost.Widgets`, `ICockpitHost.AddDockPanel`, `ICockpitHost.AddCompanionTool`, `ICockpitHost.CompanionTools` |
| `ui.session-chrome` | Controls around a session | Ambient | 0.3.0 | — | `ICockpitHost.AddSessionHeaderItem`, `ICockpitHost.AddSessionBanner`, `ICockpitHost.AddSessionHeaderAction`, `ICockpitHost.AddConversationPicker` |
| `ui.status-bar` | A line in the status bar | Ambient | 0.3.0 | — | `ICockpitHost.AddSupervisedActivityProvider` |
| `ui.dialogs` | Windows, toasts and confirmations | Ambient | 0.3.0 | — | `ICockpitHost.ShowDialogAsync`, `ICockpitHost.ShowToast`, `ICockpitActions.ConfirmAsync` |
| `ui.host-views` | Host-rendered read-only views | Ambient | 0.7.0 | — | `ICockpitHost.CreateMarkdownView`, `ICockpitHost.CreateHelpHint`, `ICockpitHost.OpenHelp`, `ICockpitHost.HasHelp` |
| `consent.request` | Asking the operator to approve an action | Ambient | 0.3.0 | — | `ICockpitHost.RequestConsentAsync` |
| `storage.settings` | Its own settings storage | Ambient | 0.3.0 | — | `IPluginStorage.Get`, `IPluginStorage.Set` |
| `workspaces.types` | Its own kind of workspace | Ambient | 0.3.0 | — | `ICockpitHost.AddWorkspaceType`, `ICockpitHost.WorkspaceTypes`, `ICockpitHost.OpenWorkspaceAsync` |
| `storage.secrets` | Storing credentials | Sensitive | 0.3.0 | `key` | `IPluginStorage.SetSecret`, `IPluginStorage.GetSecret` |
| `clipboard.write` | Writing the clipboard | Sensitive | 0.3.0 | — | `ICockpitActions.SetClipboardTextAsync` |
| `plugins.inventory` | Listing the installed plugins | Sensitive | 0.5.0 | — | `ICockpitHost.InstalledPlugins` |
| `profiles.read` | Reading the configured profiles | Sensitive | 0.3.0 | — | `ICockpitHost.GetProfilesAsync` |
| `sessions.observe` | Watching the running sessions | Sensitive | 0.3.0 | `paneId` | `ICockpitHost.Sessions`, `ICockpitHost.CurrentMcpCallerPaneId` |
| `sessions.annotate` | Naming a session | Sensitive | 0.3.0 | `paneId` | `ICockpitHost.SetSessionStatusline`, `ICockpitHost.SetSessionName`, `ICockpitHost.SuggestSessionName`, `ICockpitActions.SetActiveSessionStatusAsync` |
| `sessions.compose` | Proposing a new session | Sensitive | 0.3.0 | — | `ICockpitHost.ShowNewSessionDialogAsync` |
| `workflows.steps` | Steps and templates for workflows | Sensitive | 0.3.0 | — | `ICockpitHost.AddWorkflowStep`, `ICockpitHost.WorkflowSteps`, `ICockpitHost.AddWorkflowTemplate`, `ICockpitHost.WorkflowTemplates` |
| `workflows.trigger-observe` | Watching workflow triggers | Sensitive | 0.3.0 | `typeId` | `ICockpitHost.WorkflowTriggerRaised` |
| `autopilot.templates` | Autopilot templates | Sensitive | 0.5.0 | — | `ICockpitHost.RegisterAutopilotTemplate`, `ICockpitHost.RegisteredAutopilotTemplates` |
| `projects.fields` | Fields on a project | Sensitive | 0.7.0 | — | `ICockpitHost.AddProjectField`, `ICockpitHost.ProjectFields`, `ICockpitHost.ClaimProjectOwnership`, `ICockpitHost.GetProjectFieldOwnership` |
| `projects.read` | Reading project field values | Sensitive | 0.7.0 | `key` | `ICockpitHost.GetProjectFieldValueAsync`, `ICockpitHost.GetProjectFieldValuesAsync` |
| `projects.memory-source` | Offering a project memory source | Sensitive | 0.10.0 | `scheme` | `ICockpitHost.AddProjectMemorySource`, `ICockpitHost.RemoveProjectMemorySource`, `ICockpitHost.ProjectMemorySources`, `ICockpitHost.AddProjectMemorySourceFamily` |
| `projects.memory-read` | Reading project memory | Sensitive | 0.22.0 | — | `ICockpitHost.GetProjectMemoryRowsAsync` |
| `projects.shared-source` | Offering shared projects | Sensitive | 0.19.0 | `key` | `ICockpitHost.AddSharedProjectSource`, `ICockpitHost.RemoveSharedProjectSource`, `ICockpitHost.SharedProjectSources` |
| `tracking.providers` | Being an issue tracker | Sensitive | 0.3.0 | — | `ICockpitHost.AddTrackerProvider`, `ICockpitHost.TrackerProviders` |
| `workspaces.git` | Reading and preparing git working copies | Sensitive | 0.3.0 | `directory` | `ICockpitHost.CreateRunWorktreeAsync`, `ICockpitHost.DetectGitDirectoryStatusAsync` |
| `workspaces.paths` | The remembered working directories | Sensitive | 0.4.0 | — | `ICockpitHost.GetRememberedWorkingPathsAsync`, `ICockpitHost.RememberWorkingPathAsync` |
| `host.services` | The host's service provider | Dangerous | 0.3.0 | — | `ICockpitHost.Services` |
| `plugins.intents` | Calling other plugins | Dangerous | 0.3.0 | `targetPluginId` | `ICockpitHost.RegisterIntentHandler`, `ICockpitHost.SendIntent`, `ICockpitHost.CanSendIntent` |
| `workflows.trigger-raise` | Starting a workflow | Dangerous | 0.3.0 | `typeId` | `ICockpitHost.RaiseWorkflowTrigger` |
| `sessions.start` | Starting sessions | Dangerous | 0.3.0 | `profileLabel` | `ICockpitActions.StartSessionAsync` |
| `sessions.delegate` | Handing work to a profile | Dangerous | 0.3.0 | `profileLabel`, `permission` | `ICockpitActions.DelegateAsync` |
| `sessions.drive` | Typing into a running session | Dangerous | 0.3.0 | `paneId` | `ICockpitHost.SendToSessionAsync`, `ICockpitHost.BindToSession`, `ICockpitActions.InjectIntoActiveSessionAsync`, `ICockpitActions.HasActiveSession` |
| `sessions.provide` | Being a session provider | Dangerous | 0.3.0 | — | `ICockpitHost.AddSessionProvider`, `ICockpitHost.AddTtyProvider` |
| `sessions.resources` | Putting content into a session's context | Dangerous | 0.7.0 | — | `ICockpitHost.AddSessionResourceProvider`, `ICockpitHost.SessionResourceProviders` |
| `mcp.contribute` | Adding MCP servers | Dangerous | 0.3.0 | `serverName` | `ICockpitHost.AddMcpServer`, `ICockpitHost.RemoveMcpServer`, `ICockpitHost.GetMcpServerAuthStateAsync`, `ICockpitHost.SignInMcpServerAsync` |
| `mcp.call` | Calling MCP tools | Dangerous | 0.14.0 | `serverName`, `toolName` | `ICockpitHost.CallMcpToolAsync`, `ICockpitHost.ProbeMcpToolAsync` |
| `mcp.expose` | Serving its own MCP tools | Dangerous | 0.3.0 | `serverName` | `ICockpitHost.AddMcpEndpoint` |
| `cli.managed` | Installing and running a managed CLI | Dangerous | 0.3.0 | `cliName` | `ICockpitHost.AddManagedCli`, `ICockpitHost.ResolveManagedCliPath`, `ICockpitHost.InstallManagedCliAsync`, `ICockpitHost.RemoveManagedCli`, `ICockpitHost.GetManagedCliStatusAsync`, `ICockpitHost.GetManagedCliAutoUpdateAsync`, `ICockpitHost.SetManagedCliAutoUpdateAsync` |
| `channels.assistant` | A chat channel onto the assistant | Dangerous | 0.27.0 | — | `ICockpitHost.OpenAssistantChannel` |

This table is verified against `CapabilityCatalog` by `CapabilityCatalogTests`; editing one without the other
fails the build.

> This list says what a plugin *can ask for*. Declaring, granting and enforcing it are separate work
> (AC-107) — today every member below is simply callable.

---

## `ICockpitPlugin` {#icockpitplugin}

The entry point your plugin implements (`: IDisposable`). The host discovers it in your entry assembly.

```csharp
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    void ConfigureServices(IServiceCollection services);
    void Initialize(ICockpitHost host);
}
```

### `PluginMetadata Metadata { get; }` {#pluginmetadata-metadata--get}
Identity shown in the Plugins manager. Return a `PluginMetadata` (see below). Read early — keep it a plain
property with no side effects.

### `void ConfigureServices(IServiceCollection services)` {#void-configureservicesiservicecollection-services}
**Phase 1**, *before* the host builds its DI container — register your own services here.
- **Parameter** `services` — the host's service collection (from `Microsoft.Extensions.DependencyInjection.Abstractions`).
- **Note:** only runs at startup for an *already-enabled* plugin. A plugin enabled *this* session contributes
  its **UI** immediately (via `Initialize`) but its **services only after the next restart** (the container is
  already built). Keep this optional where you can. Leave the body empty if you register nothing.

### `void Initialize(ICockpitHost host)` {#void-initializeicockpithost-host}
**Phase 2**, once the host and UI exist — register your contribution points through `host` (below). This is
where the plugin actually wires itself into the cockpit.
- **Parameter** `host` — the facade described next.

### `void Dispose()` *(from `IDisposable`)* {#void-dispose-from-idisposable}
Runs when the plugin is **disabled** or the app exits — release timers, `HttpClient`s, subscriptions, etc.
The assembly is **not** unloaded until the process restarts (a loaded plugin cannot be truly unloaded), so
"disable" means *UI removed + `Dispose` called*.

---

## `ICockpitHost` {#icockpithost}

Handed to you in `Initialize`. The contract's only intended growth surface.

```csharp
public interface ICockpitHost
{
    IServiceProvider Services { get; }
    ICockpitActions Actions { get; }
    IPluginStorage Storage { get; }
    void AddSettings(Func<Control> createView);
    void AddSettings(Func<Control> createView, string category);                  // default forwards above
    void AddSideMenuButton(string title, Action onInvoke);
    void AddSideMenuSection(string title, Func<Control> createView);
    void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView);  // default no-op
    void AddSupervisedActivityProvider(ISupervisedActivitySource source);        // default no-op
    void AddConversationPicker(ConversationPickerRegistration picker);           // default no-op
    void AddWorkflowStep(IWorkflowStep step);                                   // default no-op
    IReadOnlyList<IWorkflowStep> WorkflowSteps { get; }                         // default []
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
    void OnSettingsSaved(Action callback);                       // default no-op
    void AddSessionProvider(SessionProviderRegistration registration); // default no-op
    void AddWidget(WidgetRegistration registration);             // default no-op
    IReadOnlyList<WidgetRegistration> Widgets { get; }           // default []
    void AddCompanionTool(CompanionToolRegistration registration); // default no-op
    IReadOnlyList<CompanionToolRegistration> CompanionTools { get; } // default []
    void AddWorkspaceType(WorkspaceTypeRegistration registration);   // default no-op
    IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes { get; } // default []
    Task AddMcpServer(McpServerContribution contribution);       // default no-op, returns Task.CompletedTask
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync();   // default returns []
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information,
                   string? actionLabel = null, Action? onAction = null);        // default no-op
    void AddSessionHeaderAction(PluginSessionAction action);                     // default no-op
    void AddToolbarAction(ToolbarAction action);                                 // default no-op
    void AddShortcut(PluginShortcut shortcut);                                   // default no-op
    void AddWorkflowTemplate(WorkflowTemplate template);                         // default no-op
    void AddTtyProvider(TtyProviderRegistration registration);                   // default no-op
    void AddManagedCli(ManagedCliDescriptor descriptor);                         // default no-op
    Task<bool> GetManagedCliAutoUpdateAsync(string cliName,
                                            CancellationToken cancellationToken = default); // default true
    Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled,
                                      CancellationToken cancellationToken = default); // default no-op
    Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled = null); // default no-op
    void AddProjectField(ProjectFieldRegistration registration);                 // default no-op
    IReadOnlyList<ProjectFieldRegistration> ProjectFields { get; }               // default []
    Task<string?> GetProjectFieldValueAsync(string key, string? paneId = null,
                                            CancellationToken cancellationToken = default); // default null
    Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null,
                                            CancellationToken cancellationToken = default); // default []
    void AddProjectMemorySource(ProjectMemorySourceRegistration registration);    // default no-op
    IReadOnlyList<ProjectMemorySourceRegistration> ProjectMemorySources { get; } // default []
    void AddSessionResourceProvider(ISessionResourceProvider provider);          // default no-op
    IReadOnlyList<ISessionResourceProvider> SessionResourceProviders { get; }     // default []
    Task SetSessionStatusline(string paneId, string statusline);                 // default no-op
    Task SetSessionName(string paneId, string name);                             // default no-op
    Task SuggestSessionName(string paneId, string name);                         // default no-op
}
```

### `IServiceProvider Services { get; }` {#iserviceprovider-services--get}
The built host container. Resolve services you (or the host) registered:
`host.Services.GetRequiredService<MyService>()`. Prefer resolving your own registered services over
reaching into host internals.

### `ICockpitActions Actions { get; }` {#icockpitactions-actions--get}
Actions on the cockpit/session — see [`ICockpitActions`](#icockpitactions).

### `IPluginStorage Storage { get; }` {#ipluginstorage-storage--get}
Your per-plugin key/value store — see [`IPluginStorage`](#ipluginstorage).

### `void AddSettings(Func<Control> createView)` {#void-addsettingsfunccontrol-createview}
Registers your **settings view**, opened from the **gear** next to your plugin in the Plugins manager (there
is no top-level Options tab per plugin).
- **Parameter** `createView` — a factory returning your settings `Control`, invoked on the UI thread when the
  gear is clicked.
- **Call at most once.**
- If your control implements [`IPluginSettingsView`](#ipluginsettingsview), the host's screen shows a **Save**
  button; otherwise just **Close**. The host performs the write your view hands it — your view never persists
  by itself.
```csharp
host.AddSettings(() => new MySettingsControl(host.Storage));
```

### `void AddSettings(Func<Control> createView, string category)` {#void-addsettingsfunccontrol-createview-string-category}
Same as above, but declares which Options sidebar group your row lands in — e.g. `"Assistant Plugins"` for a
chat-channel plugin. Not calling this (or calling the one-argument overload) leaves you in the default
**PLUGINS** group, unchanged from before this overload existed.
```csharp
host.AddSettings(() => new MySettingsControl(host.Storage), "Assistant Plugins");
```

### `void AddSideMenuButton(string title, Action onInvoke)` {#void-addsidemenubuttonstring-title-action-oninvoke}
Adds a **launcher button** to the left sidebar.
- **Parameters:** `title` — the button label; `onInvoke` — runs (UI thread) when clicked, typically to open a
  dialog via `ShowDialogAsync`.
```csharp
host.AddSideMenuButton("GitHub Issues", () => _ = host.ShowDialogAsync("Issues", () => BuildIssuesView()));
```

### `void AddSideMenuSection(string title, Func<Control> createView)` {#void-addsidemenusectionstring-title-funccontrol-createview}
Adds an **inline accordion section** under the session list — for small, always-visible content (not a heavy
panel).
- **Parameters:** `title` — the section header; `createView` — factory for the section's `Control`.

### `Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560)` {#task-showdialogasyncstring-title-funccontrol-createcontent-double-width--720-double-height--560}
Opens a **window beside the cockpit** hosting your content; you own the content control. It is not modal:
the operator can keep working in a running session while it is open, and can open a second one — each call
builds its content afresh, because a title is not enough for the host to tell two of them apart.
- **Parameters:** `title` — window title; `createContent` — factory for the dialog body; `width`/`height` —
  size in DIPs (defaults 720×560).
- **Returns:** a `Task` that completes when the window closes.
- Because it does not block, do not assume the cockpit is frozen behind it. If your content acts on "the
current session", capture that session when you open the window rather than reading the selected one later —
the operator may have moved on.
- The host provides a themed **DataGrid** app-wide, so your content may use it.
```csharp
await host.ShowDialogAsync("Issues", () => BuildIssuesView(), width: 900, height: 600);
```

### `void OnSettingsSaved(Action callback)` {#void-onsettingssavedaction-callback}
Registers `callback` to run (UI thread) after **this plugin's own** settings are saved from the manager's
gear (#52) — i.e. the host committed what your `IPluginSettingsView.TryStage()` handed it. Enabling/disabling/installing a plugin
still needs a restart (its assembly can't be unloaded/loaded live), but a settings change doesn't have to.
- **Never at staging time, always after the write.** Your callback runs once the commit you handed back has
  run, so a cache you drop here is rebuilt from the new values — not from the ones being replaced. A settings
  screen the operator then cancels never staged a write and never calls you either.
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

### `void AddSessionProvider(SessionProviderRegistration registration)` {#void-addsessionprovidersessionproviderregistration-registration}
Registers a new **session provider** (#45) — the plugin equivalent of the built-in Claude-CLI/Ollama/LM-Studio
providers. Once registered, it appears in the New-session/Manage-profiles provider picker, backed by the
plugin's own driver and config view. See [`SessionProviderRegistration`](#sessionproviderregistration) and the
[Sessions namespace](#the-sessions-namespace--provider-plugins) below for the full contract.
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

### `void AddWidget(WidgetRegistration registration)` {#void-addwidgetwidgetregistration-registration}
Registers a **dashboard widget type** — the widget equivalent of `AddSessionProvider`. It becomes available in
a Dashboard workspace's "Add widget" gallery, and each placed instance is built by the registration's own view
factory. The core hosts the grid and the pane chrome; what a widget shows is the plugin's business. See
[`WidgetRegistration`](#widgetregistration) and [`IWidgetContext`](#iwidgetcontext).
- **Parameter** `registration` — the widget type's id, title, view factory, and optional icon/description/
  default span/config-view factory.
- Default no-op, so existing `ICockpitHost` implementations (test fakes, older plugin builds) keep compiling
  untouched — only the app's own host renders it.
```csharp
host.AddWidget(new WidgetRegistration("my-plugin.cpu", "CPU", context => new CpuWidget(context))
{
    Icon = "📈",
    Description = "Processor usage.",
    DefaultColumnSpan = 6,
    DefaultRowSpan = 4,
    CreateConfigView = context => new CpuWidgetSettings(context),   // omit → the pane has no ⚙
});
```

### `IReadOnlyList<WidgetRegistration> Widgets { get; }` {#ireadonlylistwidgetregistration-widgets--get}
Every widget type all plugins have contributed — what a Dashboard workspace's "Add widget" gallery reads. A
plugin that is not building that gallery has no reason to touch it. Default empty.

### `WidgetRegistration` {#widgetregistration}
```csharp
public sealed record WidgetRegistration(string Id, string Title, Func<IWidgetContext, Control> CreateView)
{
    public string Icon { get; init; } = "🧩";
    public string Description { get; init; } = string.Empty;
    public int DefaultColumnSpan { get; init; } = 1;
    public int DefaultRowSpan { get; init; } = 1;
    public Func<IWidgetContext, Control>? CreateConfigView { get; init; }
    public bool HasConfig => CreateConfigView is not null;
}
```
- `Id` — stable, unique id for the widget **type**, namespaced by your plugin. It is persisted with every
  placed instance so a saved dashboard rebuilds after a restart; **changing it orphans existing instances**, so
  treat it as an API surface. Unique across installed plugins too: the first to claim an id keeps it, and a
  later claim is refused and logged — two plugins offering one type would put it in the gallery twice and leave
  the host resolving instances to whichever loaded first.
- `CreateView` — builds one instance's control on the UI thread, handed that instance's own `IWidgetContext`.
  Called once per instance; a widget needing periodic updates owns its timer or listens to `RefreshRequested`.
- `DefaultColumnSpan`/`DefaultRowSpan` — the size of a freshly placed instance; the operator resizes after.
  The 1×1 default is tiny on the default 24-column grid, so set real numbers.
- `CreateConfigView` — the instance's settings form, or **null when there is nothing to configure**. Null is
  what hides the ⚙ on the pane header, so a widget can never show a gear that opens an empty dialog. You supply
  the content; the host wraps it with the Save/Close footer, as it does for `AddSettings`. Saving raises
  `RefreshRequested` on that instance.
- `HasConfig` — derived from `CreateConfigView` rather than declared next to it, so no flag can claim settings
  the widget cannot build.

### `IWidgetContext` {#iwidgetcontext}
Handed to one placed instance's view and config-view factories — everything that instance needs and nothing
it does not.
```csharp
public interface IWidgetContext
{
    string InstanceId { get; }                 // this instance — not the widget type
    IPluginStorage Storage { get; }            // scoped to InstanceId, under your plugin's storage
    ICockpitSessionObserver Sessions { get; }  // same surface as host.Sessions
    event EventHandler RefreshRequested;       // the pane's ↻, or a dashboard-wide refresh
}
```
- `InstanceId` — the key this instance's config is stored under, distinct from the widget *type* id.
- `Storage` — per-instance, so two "System Monitor" widgets on one dashboard keep separate config and neither
  collides with the other.
- `Sessions` — the same read/observe surface as [`ICockpitSessionObserver`](#the-sessions-namespace--provider-plugins),
  so a widget can follow the active session's working directory or output without the core knowing what it is.
- `RefreshRequested` — raised when the host asks this instance to refresh, including after its settings are
  saved. A widget polling on its own timer can ignore it; one showing a snapshot should re-read.

### `void AddCompanionTool(CompanionToolRegistration registration)` {#void-addcompaniontoolcompaniontoolregistration-registration}
Registers a mini-tool in the pop-out **companion window** — an icon, a title and a control your own factory
builds, one level below a widget: where `AddWidget` fills a Dashboard cell, this fills a slot in a window
docked outside any workspace. See [`CompanionToolRegistration`](#companiontoolregistration) and
[`ICompanionToolContext`](#icompaniontoolcontext).
- **Parameter** `registration` — the tool's id, title, view factory, and optional icon/tooltip.
- Default no-op, so existing `ICockpitHost` implementations (test fakes, older plugin builds) keep compiling
  untouched — only the app's own host renders it.
```csharp
host.AddCompanionTool(new CompanionToolRegistration(
    "my-plugin.hello", "My Tool", context => new MyToolView(context))
{
    IconKind = MaterialIconKind.HandWave,
    Tooltip = "My tool",
});
```

### `IReadOnlyList<CompanionToolRegistration> CompanionTools { get; }` {#ireadonlylistcompaniontoolregistration-companiontools--get}
Every companion tool all plugins have contributed — what the companion window reads. A plugin not building
that window has no reason to touch it. Default empty.

### `CompanionToolRegistration` {#companiontoolregistration}
In `Cockpit.Plugins.Abstractions.CompanionTools`.
```csharp
public sealed record CompanionToolRegistration(string Id, string Title, Func<ICompanionToolContext, Control> CreateView)
{
    public string Tooltip { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public MaterialIconKind? IconKind { get; init; }
}
```
- `Id` — stable, unique id for the tool, namespaced by your plugin (`"system-monitor.usage"`). Treat it as an
  API surface: changing it orphans anything that referenced the old id. Unique across installed plugins too:
  the first to claim an id keeps it, and a later claim is refused and logged rather than listed beside it.
- `CreateView` — builds the tool's control on the UI thread, handed its own `ICompanionToolContext`. Called
  once; a tool needing periodic updates owns its timer or listens to `RefreshRequested`.
- `Icon` / `IconKind` — a glyph or bundled vector icon for the tool's compact icon action; `IconKind` wins over
  `Icon` when set. Defaults: empty string / none.
- `Tooltip` — hover text for the icon action. Empty by default.

### `ICompanionToolContext` {#icompaniontoolcontext}
Handed to a companion tool's view factory — everything one tool needs and nothing it does not, the same role
[`IWidgetContext`](#iwidgetcontext) plays for a placed widget instance.
```csharp
public interface ICompanionToolContext
{
    ICockpitSessionObserver SelectedSession { get; } // same read/observe surface as host.Sessions
    IPluginStorage Storage { get; }                  // scoped to this tool's own id
    event EventHandler RefreshRequested;             // the host asking this tool to refresh
}
```
- `SelectedSession` — the same selection-following surface as
  [`ICockpitSessionObserver`](#the-sessions-namespace--provider-plugins): the active session's working
  directory and its output stream, so a tool can follow what the cockpit is doing without the core knowing
  what the tool shows.
- `Storage` — per-tool, scoped to this tool's id rather than the whole plugin, so its state survives a restart
  and never collides with a sibling tool's.
- `RefreshRequested` — raised when the host asks this tool to refresh. A tool polling on its own timer can
  ignore it; one showing a snapshot should re-read.

### `void AddWorkspaceType(WorkspaceTypeRegistration registration)` {#void-addworkspacetypeworkspacetyperegistration-registration}
Registers a **full-surface workspace type** — the workspace equivalent of `AddWidget`, one level up. Where a
widget fills one cell of a Dashboard's grid, a workspace type owns its **whole body**: the host draws the tab
and the frame and persists the workspace's namespaced type id, and your `CreateBody` draws everything inside.
It appears in the tab strip's "+" menu beside **Sessions** and **Dashboard**; choosing it creates a workspace
of that type. See [`WorkspaceTypeRegistration`](#workspacetyperegistration) and
[`IWorkspaceContext`](#iworkspacecontext).
- **Parameter** `registration` — the type's id, title, body factory, and optional icon/description.
- Default no-op, so existing `ICockpitHost` implementations (test fakes, older plugin builds) keep compiling
  untouched — only the app's own host renders it.
```csharp
host.AddWorkspaceType(new WorkspaceTypeRegistration("my-plugin.pipeline", "Pipeline", context => new PipelineBody(context))
{
    Icon = "🚀",
    Description = "A whole workspace my plugin draws and drives.",
});
```

### `IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes { get; }` {#ireadonlylistworkspacetyperegistration-workspacetypes--get}
Every workspace type all plugins have contributed — what the tab strip's "+" menu reads. A plugin not building
that menu has no reason to touch it. Default empty.

### `WorkspaceTypeRegistration` {#workspacetyperegistration}
In `Cockpit.Plugins.Abstractions.Workspaces`.
```csharp
public sealed record WorkspaceTypeRegistration(string Id, string Title, Func<IWorkspaceContext, Control> CreateBody)
{
    public string Icon { get; init; } = "🧩";
    public MaterialIconKind? IconKind { get; init; }
    public string Description { get; init; } = string.Empty;
}
```
- `Id` — stable, unique id for the workspace **type**, namespaced by your plugin (`"autopilot.run"`). It is
  persisted with every workspace of this type so a saved desk rebuilds after a restart; **changing it orphans
  existing workspaces** — they render as a placeholder until the id comes back — so treat it as an API surface.
  Unique across installed plugins too: the first to claim an id keeps it, and a later claim is refused and
  logged. An unknown type (its plugin uninstalled) shows a placeholder rather than crashing the workspace.
- `CreateBody` — builds the whole workspace body on the UI thread, handed that workspace's own
  `IWorkspaceContext`. Called once per workspace; the body owns its layout and lifetime from there.
- `IconKind` — a bundled vector icon for the "+" menu and the tab, preferred over `Icon` when set so it reads
  as part of the theme; `Icon` is the emoji fallback.
- `Description` — one line for the "+" menu.

### `IWorkspaceContext` {#iworkspacecontext}
Handed to a workspace's body factory — what one full-surface workspace needs that its plugin cannot reach on
its own.
```csharp
public interface IWorkspaceContext
{
    string WorkspaceId { get; }                 // this workspace instance
    IPluginStorage Storage { get; }             // scoped to WorkspaceId, under your plugin's storage
    ICockpitSessionObserver Sessions { get; }   // same surface as host.Sessions
    IEmbeddedSession EmbedSession(EmbeddedSessionRequest request);  // a live host session in your layout
    event EventHandler RefreshRequested;
}
```
- `WorkspaceId` — the key this workspace's state is stored under, distinct from the workspace *type* id.
- `Storage` — per-workspace, so two workspaces of the same type keep separate state.
- `Sessions` — the same observe surface as `host.Sessions`.
- `EmbedSession` — starts a real host session and returns a control embedding its live view, for your body to
  place wherever it wants (see [`IEmbeddedSession`](#iembeddedsession)). **The host owns the session's
  lifetime** — it keeps it out of the session grid and ends it when the workspace (or the app) closes; your body
  owns only the place, never the lifetime.
- `RefreshRequested` — raised when the host asks this workspace to refresh.
- **Deliberately narrow, like `IWidgetContext`.** Cross-plugin intents (`host.SendIntent`), dialogs and the
  theme are already yours to reach: the body factory is a closure created in `Initialize`, where you captured
  the `ICockpitHost`, and the theme is app resources any control binds with `DynamicResource` — so they are not
  repeated here.

### `IEmbeddedSession` {#iembeddedsession}
```csharp
public interface IEmbeddedSession
{
    Control View { get; }    // the session's live view — drop it into your body's layout
    string PaneId { get; }   // act on this exact session through ICockpitHost (statusline, intents, name)
}
```
The host owns the session, so there is nothing here to dispose — you hold the place, not the lifetime.

### `EmbeddedSessionRequest` {#embeddedsessionrequest}
```csharp
public sealed record EmbeddedSessionRequest
{
    public string? ProfileId { get; init; }         // the profile to run — matched by its Label; null → the first configured profile
    public string? WorkingDirectory { get; init; }  // null → the app's own working directory
}
```

### `Task AddMcpServer(McpServerContribution contribution)` {#task-addmcpservermcpservercontribution-contribution}
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

### `void AddSupervisedActivityProvider(ISupervisedActivitySource source)` {#void-addsupervisedactivityproviderisupervisedactivitysource-source}
Registers a source of long-running, agent-started background activities shown in the **app status bar** (a counter
next to "Delegated tasks"). The counter appears only while something is running and opens a panel listing each
activity with its details and a **Kill button per item**. The host owns the Kill — an agent has no path to start or
stop through it, only the operator does. This is the operator-facing kill-switch that a port-forward, an open watch,
or any other supervised background work needs to be safe.
- `ISupervisedActivitySource`: `string Label` (the counter label, e.g. `"Port-forwards"`), `IReadOnlyList<SupervisedActivity> Snapshot()` (a fresh list each call), and `event Action? Changed` (raise it when the set changes so the counter and an open panel refresh).
- `SupervisedActivity(string Id, string Title, IReadOnlyList<ActivityDetail> Details, Func<Task> StopAsync)` — `Details` are `ActivityDetail(Label, Value)` facts shown verbatim (source, target, cluster); `StopAsync` is what the Kill button calls.
- Default no-op, same compatibility rationale as `AddSessionProvider`.

```csharp
// A manager that implements ISupervisedActivitySource, exposing its active tunnels:
host.AddSupervisedActivityProvider(myPortForwardManager);
```

### `void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView)` {#void-addsessionheaderitemfuncipluginsessioncontext-control-createview}
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

#### `IPluginSessionContext` {#ipluginsessioncontext}
One session, for as long as its panel exists — where [`ICockpitSessionObserver`](#the-sessions-namespace--provider-plugins)
follows whichever session is *selected*, this is bound to the one your control sits in.

| Member | Meaning |
|---|---|
| `string PaneId` | Identifies this session pane for as long as it exists. Match it against `ICockpitSessionObserver.ActivePaneId` to know whether an action taken *outside* a session (in a dialog, say) was meant for this one. **Not** the provider's conversation id — panes come and go with the window, and two panes can resume the same conversation. Empty on a host that predates it. |
| `string? WorkingDirectory` | The directory this session is working in; null until known (an SDK session before its init event). |
| `event EventHandler? WorkingDirectoryChanged` | The directory became known or changed — re-scope. |
| `event EventHandler<SessionOutputText>? OutputProduced` | Each chunk of text **this** session produced, verbatim. Substring-scan it for a signal (a git command, a pushed branch, …). |

Events are raised on the UI thread, so a handler can touch its controls directly.

A dialog belongs to no session, so an action it takes "for the current session" needs naming: read
`host.Sessions.ActivePaneId`, hand that to your own state, and let the header item whose `PaneId` matches pick
it up. That is how the YouTrack plugin starts an issue from its dialog and has it appear in the right session's
header — with four panes open, "the session" is not obvious, and guessing would put the ticket on the wrong one.

### `void AddWorkflowStep(IWorkflowStep step)` {#void-addworkflowstepiworkflowstep-step}
Contributes a **step to the workflow editor** (#69) — "Start a ticket", "Comment on a pull request". It appears in
the step picker under your own category, is wired on the canvas like any other step, and runs as part of the flow.

Without this, what a flow can do is whatever the workflows plugin was built to do — and every integration the cockpit
ever grows would have to be built *there*, by someone who does not have your API client in front of them.

```csharp
internal sealed class StartIssueStep(YouTrackSettings settings) : IWorkflowStep
{
    public string TypeId => "youtrack.start";      // stored in the flow — never change it once flows use it
    public string Name => "Start a ticket";
    public string Description => "Move a ticket to the state its board calls in progress, and assign it to you.";
    public string Icon => "▶";
    public string Category => "YouTrack";          // the picker's heading: your plugin's own name reads best
    public IReadOnlyList<string> Parameters => ["Ticket", "Instance"];

    // #AC-38: it moves a real ticket with the operator's token, so it acts with their rights → Dangerous. A
    // non-trigger step MUST declare this; leaving it null leaves the step OUT of the editor rather than run ungated.
    public WorkflowStepConsent? RequiredConsent => WorkflowStepConsent.Dangerous;

    // Shown before a flow has ever run, so the next step can be configured against your output rather than a guess.
    public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>
    {
        ["ticket"] = "WEB-14",
        ["state"] = "In Progress",
    };

    public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var ticket = context.Parameter("Ticket");  // already resolved: {ticket} became WEB-14 before you saw it
        // ... do the work; throw with a sentence the operator can act on if it cannot be done ...
        return WorkflowStepResult.Of("state", "In Progress", $"{ticket} → In Progress");
    }
}

// in Initialize:
foreach (var step in YouTrackWorkflowSteps.All(settings))
{
    host.AddWorkflowStep(step);
}
```

Three things the host does for you, so you never write workflow code:

- **Placeholders are resolved before you see them.** A parameter the operator wrote as `{ticket}` or
  `{Run a command.output}` arrives as the value. You never learn the syntax exists.
- **Producing nothing means passing on what came in.** A step that only *acts* (a comment, a notification) returns
  `WorkflowStepResult.Done("…")` and the data flowing through the flow is untouched behind it.
- **Several `Outputs` make it a decision.** Name them (`["yes", "no"]`), say in `WorkflowStepResult.Branch` which one
  you took, and only that wire is followed.

Throwing fails the step, and your message is what the operator reads in the run — write it as a sentence they can act
on ("WEB-14 cannot go to 'Done'. Its board allows: Review, Reopened."). Returning success without having done the work
is invisible to the run, so don't.

**Declare `RequiredConsent` (#AC-38).** A non-trigger step **must** say whether running it needs the operator's
consent, in its own code — the workflows plugin cannot override it, and an agent building a flow over the MCP cannot
either:

- `WorkflowStepConsent.None` — genuinely safe (a read, a pure computation, a decision). Runs without asking.
- `WorkflowStepConsent.Dangerous` — acts with the operator's rights: runs a command, hands off a session, or sends
  data out (a comment, a push, a webhook). Put to the operator for **Approve/Deny before every run** (never
  remembered), and an agent may not create or arm a flow containing it — only the operator can, in the editor.
- `WorkflowStepConsent.LowRisk` — needs consent but is idempotent enough to be **remembered** once approved, and stays
  agent-buildable (gated at run time instead).
- **Leaving it `null` (the default) is not "safe" — it is undeclared**, and the editor **leaves the step out** and
  names it, so a step that acts with the operator's rights cannot slip through ungated. Declare `None` explicitly for
  a safe step.

Triggers (`IsTrigger => true`) are fired, never run, so their value is ignored — leave it at the default.

`TypeId` must be unique across all plugins — prefix it with your plugin's id. Registering a duplicate throws at
startup rather than letting load order decide which of two steps a stored flow means.

### `IReadOnlyList<IWorkflowStep> WorkflowSteps { get; }` {#ireadonlylistiworkflowstep-workflowsteps--get}
Every step all plugins contributed. Only the workflows plugin has a reason to read this; it does so when its editor
opens, not at startup, because plugins initialise in an order nobody controls.

### `void AddConversationPicker(ConversationPickerRegistration picker)` {#void-addconversationpickerconversationpickerregistration-picker}
Registers a way to **pick an earlier conversation to resume**. The New-session dialog can resume a conversation
by id; with a picker registered it also shows a **Search…** button that runs yours, so the operator chooses a
conversation instead of typing an id by hand.

The cockpit knows nothing about any provider's history — the transcripts are one provider's own format — so this
is how a plugin that *can* browse that history lends it to the dialog without the core depending on the plugin.

```csharp
public sealed record ConversationPickerRegistration(string Title, Func<Task<string?>> PickAsync)
{
    public Func<Task<PickedConversation?>>? PickWithLocationAsync { get; init; }
}

public sealed record PickedConversation(string SessionId, string? WorkingDirectory = null);
```

| Member | Meaning |
|---|---|
| `Title` | What the picker does; shown as the button's tooltip, e.g. "Search transcripts". |
| `PickAsync` | Runs when the operator asks to pick one — typically opening your own dialog. Return the chosen conversation's id, or `null` when they cancelled. |
| `PickWithLocationAsync` | Optional richer form for a provider whose history is scoped to a folder: return the chosen conversation's id **and** the directory it ran in (`PickedConversation`), so the resumed session starts there rather than wherever the operator last was. When set, the dialog prefers it over `PickAsync`; leave it `null` if you cannot tell the directory. |

```csharp
host.AddConversationPicker(new ConversationPickerRegistration("Search transcripts", async () =>
{
    string? picked = null;
    await host.ShowDialogAsync("Search transcripts", () => new MySearchControl(id => picked = id));
    return picked;   // null = cancelled
}));
```

If your provider scopes its history to a folder — the way the Claude CLI keeps each session's transcript under
the directory it was started in — set `PickWithLocationAsync` too, so the resumed session starts in the right
place instead of wherever the operator last was:

```csharp
async Task<PickedConversation?> Search()
{
    PickedConversation? picked = null;
    await host.ShowDialogAsync("Search transcripts",
        () => new MySearchControl(hit => picked = new PickedConversation(hit.SessionId, hit.WorkingDirectory)));
    return picked;
}

host.AddConversationPicker(new ConversationPickerRegistration(
    "Search transcripts",
    async () => (await Search())?.SessionId)   // id-only fallback
{
    PickWithLocationAsync = Search,
});
```

### `Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync()` {#taskireadonlylistpluginprofileinfo-getprofilesasync}
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

### `void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction)` {#void-showtoaststring-message-plugintoastseverity-severity-string-actionlabel-action-onaction}
A transient **in-app notification** in the cockpit — how you tell the operator that something happened while
they were working somewhere else in the app. `actionLabel` and `onAction` are supplied together to give the
toast one button.
- The toast **auto-dismisses**, so it announces; it does not hold the news. Whatever it is about should still be
  findable in your own surface (your side-menu section, say) after the toast is gone.
- Safe to call from any thread — the host marshals onto the UI thread itself.
- `PluginToastSeverity` is `Success` / `Warning` / `Information` / `Error`; it drives the colour and how long
  the toast stays. Default no-op, same compatibility rationale as `AddSessionProvider`.

```csharp
host.ShowToast(
    $"Review requested — #{pullRequest.Number} {pullRequest.Title}",
    PluginToastSeverity.Information,
    "Open in browser",
    () => OpenInBrowser(pullRequest.Url));
```

### `Task<ConsentDecision> RequestConsentAsync(ConsentRequest request)` {#taskconsentdecision-requestconsentasyncconsentrequest-request}
Ask the operator to **approve a single action before you perform it** — the shared consent gate for anything
your plugin does with the operator's rights on an agent's say-so: a workflow's shell or egress step, taking over
a terminal pane. The host shows an Approve/Deny banner on the session it belongs to and returns what the operator
chose; act only on `decision.IsApproved`. *(Added in SDK 1.4.0; default implementation denies — see below.)*

- **Show ground truth, not a summary.** Put the literal action in `ConsentRequest.Action` — the actual command
  and working directory, the actual URL, the pane. It is rendered **verbatim**. A prompt-injected agent controls
  the words it feeds you, so a friendly description of a hostile command is a gate that approves the command. The
  gate belongs to the host, not the plugin: you supply the truth, the host shows it.
- **Risk drives "remember".** A `ConsentRisk.Dangerous` action (shell, starting/steering a session, arbitrary
  egress) is asked **every time** — never remembered. A `ConsentRisk.LowRisk` action may set `AllowRemember` to
  offer the operator "remember for this session". The host **enforces** this — you cannot make a dangerous action
  rememberable by setting the flag. A remembered approval is bound to the **exact action** from your plugin: a
  request with a different `Action` (or from a different plugin) re-prompts, so the operator always sees the new
  ground truth — "remember" skips a repeat of the same approved action, never a new one riding under the same scope.
- **Fails closed.** A host that does not implement consent, or a request that cannot be shown (no pane, cancelled),
  returns `Denied` — never a silent approval. The default interface implementation returns `Denied`.
- Set `Source.PaneId` to the session the request belongs to (from `IPluginSessionContext.PaneId`) so the banner
  appears on that pane. Leave `Source.PluginId` null — the host stamps your plugin's identity itself.
- Every decision is written to an append-only audit trail (`consent-audit.jsonl`) the operator can review.

```csharp
var decision = await host.RequestConsentAsync(new ConsentRequest(
    Title: "Workflow wants to run a command",
    Action: $"{command}\nin {workingDirectory}",          // ground truth — shown verbatim
    Source: new ConsentSource(session.PaneId, PluginId: null, Label: "Workflows"),
    Scope: "workflow.command",
    Risk: ConsentRisk.Dangerous));

if (!decision.IsApproved)
{
    return StepOutcome.Stop("You did not approve the command.");
}
// approved — run it
```

The consent types (namespace `Cockpit.Plugins.Abstractions.Consent`):

```csharp
public sealed record ConsentRequest(
    string Title,            // host-framed line, e.g. "Workflow wants to run a command"
    string Action,           // GROUND TRUTH — the literal command+cwd / URL / pane, shown verbatim
    ConsentSource Source,    // who is asking (pane + label)
    string Scope,            // stable key for "remember", e.g. "workflow.http:GET"
    ConsentRisk Risk,        // LowRisk (rememberable) | Dangerous (asked every time)
    bool AllowRemember = false);

public sealed record ConsentSource(string? PaneId, string? PluginId, string Label);
public enum ConsentRisk { LowRisk, Dangerous }
public enum ConsentOutcome { Approved, Denied }
public sealed record ConsentDecision(ConsentOutcome Outcome, bool Remembered = false)
{
    public bool IsApproved { get; }   // Outcome == Approved
}
```

---

### `void AddSessionHeaderAction(PluginSessionAction action)` {#void-addsessionheaderactionpluginsessionaction-action}

A quick action button on a session's header bar (AC-37) — hides itself when it has nothing to show. Used by the Session Review plugin. Default no-op.

### `void AddToolbarAction(ToolbarAction action)` {#void-addtoolbaractiontoolbaraction-action}

Drops a quick action onto the app toolbar, provider-neutral — any plugin adds one the same way. Used by the Docker and Kubernetes plugins. Default no-op.

### `void AddShortcut(PluginShortcut shortcut)` {#void-addshortcutpluginshortcut-shortcut}

Registers a keyboard shortcut, shown alongside the built-in ones in Options and rebindable there. Only fires when the operator is not typing into a text field or the terminal. Default no-op.

### `void AddWorkflowTemplate(WorkflowTemplate template)` {#void-addworkflowtemplateworkflowtemplate-template}

Offers a prebuilt flow — a template — in the Workflows plugin's "New flow" picker under this plugin's name, instead of an empty canvas. Default no-op.

### `void AddTtyProvider(TtyProviderRegistration registration)` {#void-addttyproviderttyproviderregistration-registration}

Registers a terminal (TTY) provider, so a plugin can back a terminal pane with its own process or shell. Default no-op.

### `void AddManagedCli(ManagedCliDescriptor descriptor)` {#void-addmanagedclimanagedclidescriptor-descriptor}

Registers a CLI the host downloads and unpacks on demand; a machine with no managed copy falls back to the one on `PATH`. Default no-op.

### `Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default)` {#taskbool-getmanagedcliautoupdateasyncstring-cliname-cancellationtoken-cancellationtoken--default}

Whether the host's background update check installs a newer version of `cliName` itself rather than only toasting that one exists (AC-767) — what the shared `ManagedCliConfigSection`'s "Update automatically" checkbox reads. Default `true`.

### `Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default)` {#task-setmanagedcliautoupdateasyncstring-cliname-bool-enabled-cancellationtoken-cancellationtoken--default}

Turns auto-update for `cliName` on or off — what the checkbox writes. Default no-op.

### `Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled = null)` {#task-addmcpendpointstring-servername-object-tools-funcbool-isenabled--null}

Registers an **in-process** MCP server (AC-12) exposing the methods on `tools` — distinct from `AddMcpServer`, which points the cockpit at an external MCP process. `isEnabled` gates it live on the plugin's own setting (read each time servers are gathered, so a toggle takes effect at once; `null` = always on). Call it fire-and-forget from `Initialize`. Used by the Docker and Kubernetes plugins. Default no-op.

### `void AddProjectField(ProjectFieldRegistration registration)` {#void-addprojectfieldprojectfieldregistration-registration}

Adds a field to the **project editor** (AC-317) — "which YouTrack project is this project", "which repository" — so a
project carries the identifier your plugin resolves, picked from a list you supply instead of typed into a free-text
box where a misspelling silently finds nothing. Default no-op.

You describe the field; the host draws it and stores the answer on the project. That is deliberate: a plugin drawing
its own row would have to remember the editor's label, hint and spacing, and a description also survives a project
staying linked to a tracker that is not installed on this machine.

```csharp
host.AddProjectField(new ProjectFieldRegistration(
    "youtrack.project",                        // stable key — already-linked projects are stored under it
    "YouTrack project",
    async cancellationToken => [.. (await client.GetProjectsAsync(cancellationToken))
        .Select(project => new ProjectFieldOption(project.ShortName, $"{project.Name} — {project.ShortName}"))])
{
    Hint = "Which project in YouTrack this one is tracked in.",
    Placeholder = "AC",
});
```

`ProjectFieldOption(Value, Display)` keeps the two apart on purpose: `Display` is what the operator picks by,
`Value` is what is stored and handed back to you. The box filters as the operator types and **keeps what they type**
even when the source never returned it — a repository they cannot read is not in the list, and refusing it would be
refusing the only way to link it.

`LoadOptionsAsync` runs off the UI thread while the editor is already open and usable, so it may reach the network or
shell out. Return an empty list when there is genuinely nothing to offer, and **throw when the fetch failed** — the
two say different things to an operator deciding whether their project points at the right place.

Two plugins may register the **same key**: that is agreement, not a clash (a repository is a repository), and the
first registration wins, so either plugin alone still offers the field. Different keys that differ only in case are
different fields, because a link is read back case-sensitively.

### `void AddProjectMemorySource(ProjectMemorySourceRegistration registration)` {#void-addprojectmemorysourceprojectmemorysourceregistration-registration}

Names a place a project's memory can live **other than a folder** (AC-165/166) — a Depot project, say. The project
editor's memory picker offers your source beside "Folder", and a session started on a project whose `MemoryRef` is
`<scheme>:<value>` for your registered scheme has its standing instructions say, in one sentence, where its memory
is *and* how to reach it — instead of the plain, unexplained sentence a bare path gets. Default no-op.

```csharp
host.AddProjectMemorySource(new ProjectMemorySourceRegistration(
    Scheme: "depot",                       // the prefix a MemoryRef carries this source under: "depot:cockpit"
    Title: "Depot project",                 // named back to the operator and the session
    Instruction: "Read and write it through the Depot MCP: look the project up by that slug before you start, "
        + "and write back what you learn as you go. If the Depot MCP is not available in this session, say so "
        + "rather than working from memory you cannot see."));
```

`Title` and `Instruction` are each trimmed, then read into the standing-instructions sentence — not carried
verbatim: if your trimmed `Instruction` does not already end in `.`, `!` or `?`, the host appends a period so the
sentence that follows it still reads as one (an instruction you wrote ending in `)`, say, becomes `…).` in the
prompt):

> This project's memory lives in {Title} "{value}". {Instruction}[.]

Declared rather than resolved eagerly, the same as `AddProjectField`: there is no network call or credential at
registration time, because the host only ever quotes `Title`/`Instruction` into a prompt — reaching or writing the
memory itself happens inside the session, through whatever your `Instruction` points it at.

A blank `Scheme`, `Title` or `Instruction` is refused, and unlike the first two, a blank `Instruction` is not a
cosmetic gap: a source named but not explained leaves a session no better off than the bare reference it would
otherwise have gotten, so such a source is not offered at all. A scheme another plugin already registered is kept
as it was and this registration ignored (matched case-insensitively, the same agreement `AddProjectField` makes
for a key two plugins both offer — a project's stored `MemoryRef` is itself matched case-insensitively when read
back).

**What this does not do:**

- **It does not mount an MCP server.** This only supplies the sentence a session reads; if your `Instruction`
  points at an MCP the session also needs put in front of it, register that separately with `AddMcpServer` (which
  itself only carries HTTP + bearer auth, no OAuth — see [MCP server registration](PLUGIN-SDK.md#mcp-server-registration)).
- **It does not offer a live list of options.** Unlike `AddProjectField`'s `LoadOptionsAsync`, there is nothing
  here to fetch — the operator picks your source by `Title` from the editor's small dropdown, but still types the
  bare identifier (`cockpit` in `depot:cockpit`) into a free-text box themselves.

### `IReadOnlyList<ProjectMemorySourceRegistration> ProjectMemorySources { get; }` {#ireadonlylistprojectmemorysourceregistration-projectmemorysources--get}

Every memory source plugins have contributed, in registration order — what the project editor's picker and a
starting session's standing instructions both read. A plugin that is neither has no reason to call this. Default `[]`.

### `void AddSessionResourceProvider(ISessionResourceProvider provider)` {#void-addsessionresourceproviderisessionresourceprovider-provider}

Registers something your plugin gives a session **as it starts** (AC-165) — today, environment variables its process
runs with. The host asks every registered provider once per launch, merges the answers, and hands the result to
whichever provider is starting, so you contribute once and reach a Claude CLI, a Codex app-server, a Kimi ACP
connection and a TTY alike without knowing any of them. Default no-op.

The request names the session and the project it belongs to, which is what lets a contribution differ per project —
the natural partner to a project field you registered above:

```csharp
internal sealed class RepositorySessionResources(ICockpitHost host) : ISessionResourceProvider
{
    public async Task<SessionResourceContribution> GetSessionResourcesAsync(
        SessionResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.ProjectId))
        {
            return SessionResourceContribution.None;
        }

        var repository = await host.GetProjectFieldValueAsync("github.repository", request.PaneId, cancellationToken);
        return string.IsNullOrWhiteSpace(repository)
            ? SessionResourceContribution.None
            : new SessionResourceContribution
            {
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GH_REPO"] = repository.Trim(),
                },
            };
    }
}
```

Pass `request.PaneId` when you read a project field here: the session being started is not necessarily the selected
one, so leaving it null reads whichever pane the operator happens to be looking at instead of the one you are being
asked about.

Rules worth knowing before you rely on it:

- **The operator's profile is the floor, not the ceiling.** A contribution is applied over the profile's own
  variables, so a project's answer beats a profile default. It is applied *under* the cockpit's own variables
  (`COCKPIT_PANE_ID`, `COCKPIT_MCP_KEY`) and the provider's, which carry isolation you must not be able to break.
- **Host-controlled keys are refused.** An `ANTHROPIC_*` credential or a nested-agent marker is dropped and logged by
  name — the same rule a profile's variables meet, applied whether or not you scrubbed first.
- **First contributor wins a key.** Two plugins setting the same variable is not an error; the one registered first
  keeps it, so what a session carries does not depend on plugin load order.
- **Keep it short and do not throw.** It runs while the operator waits for the session to open. A call that throws is
  logged and treated as `None` — one plugin's bad day does not stop a session starting.
- **This is not how you tell a session something.** A sentence for the agent to read belongs in the project's own
  behaviour prompt or information rows; this puts a value in a process.
- **Every session has a project where one can be worked out, not only the ones a person starts.** The operator names
  it in the New-session routes; a delegated task inherits it from the session that delegated it; and a session nobody
  named a project for — an embedded run (an Autopilot step, a workflow), a session your plugin starts — is placed on
  the project that owns the folder it runs in, the folder itself or anything inside it. A run pointed straight at a
  worktree the cockpit made is placed on the project of the repository that worktree was cut from (AC-320). A folder
  no project claims, or one two projects claim equally, still answers `null` — a session without a project is an
  ordinary session, and guessing between two would be worse than saying nothing.
- **A project-dependent contribution reaches autonomous runs too, so weigh what it carries.** An Autopilot step runs
  with its tool calls pre-approved and its brief taken from an issue you did not write. What you contribute is in that
  process: `GH_REPO` on a project linked to a repository other than the one its folder clones points that run's `gh`
  commands at the linked repository. That is the point of the field — but the run's worktree bounds its files, not its
  reach, so contribute what a project genuinely decides and nothing more.

### `IReadOnlyList<ProjectFieldRegistration> ProjectFields { get; }` {#ireadonlylistprojectfieldregistration-projectfields--get}

Every field plugins have contributed — what the project editor reads to draw them. A plugin that is not the project
editor has no reason to call this. Default `[]`.

### `Task<string?> GetProjectFieldValueAsync(string key, string? paneId = null, CancellationToken cancellationToken = default)` {#taskstring-getprojectfieldvalueasyncstring-key-string-paneid--null-cancellationtoken-cancellationtoken--default}

The reading half: what the operator picked for `key` on the project a session belongs to, or `null` when that session
has no project, the project is not linked, or `paneId` matches nothing. A plugin may read a key it did not register —
that is the point of two plugins agreeing on one.

A null `paneId` means the **selected** session, which is what a dialog opened from the side menu is acting for; a
contribution that belongs to one session passes that session's own `IPluginSessionContext.PaneId` instead of relying
on which pane happens to be selected. Default `null`.

```csharp
// The issues dialog opens on the project this session is tracked in, falling back to the instance-wide default.
var linked = await host.GetProjectFieldValueAsync("youtrack.project");
```

### `Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default)` {#taskireadonlylistprojectmemoryrow-getprojectmemoryrowsasyncstring-paneid--null-cancellationtoken-cancellationtoken--default}

The project's own `ProjectResourceRole.Memory` rows (AC-483/AC-827) — 0, 1 or several, read-only. The missing read
half of `AddProjectMemorySource`/`ProjectMemorySources` below: those register where a *scheme* resolves to, this
reads which rows a project actually stored. `paneId` resolves exactly like `GetProjectFieldValueAsync`'s: null means
the selected session, and a pane with no linked project answers `[]`, never an error. Default `[]`.

Each row's `ReachesSessions` is reported, not filtered here — that flag is `SessionStartDefaults`' own rule for what
a *starting session* is told; a plugin reading rows directly is a different consumer and decides for itself whether
the same rule applies to what it is about to do with them.

```csharp
var rows = await host.GetProjectMemoryRowsAsync();
```

### `Task SetSessionStatusline(string paneId, string statusline)` / `Task SetSessionName(string paneId, string name)` / `Task SuggestSessionName(string paneId, string name)` {#task-setsessionstatuslinestring-paneid-string-statusline--task-setsessionnamestring-paneid-string-name--task-suggestsessionnamestring-paneid-string-name}

How a plugin labels a session it just gave work to (AC-13, AC-310). The statusline is the accented line under a
session's title in its header and the sidebar; an empty string clears it. All three take a pane id — the session's own
`IPluginSessionContext.PaneId`, or `ICockpitSessionObserver.ActivePaneId` from a dialog acting on the selected
session — and a pane id matching nothing is a no-op, never an error. They marshal to the UI thread themselves, so call
them fire-and-forget (`_ = host.SetSessionStatusline(...)`) from anywhere. Default no-op.

The two naming calls differ in who wins. `SetSessionName` renames regardless: use it when the caller is the authority
on the name, as a workflow step naming the session it just started is. `SuggestSessionName` renames **only** a session
still carrying the name the cockpit made up ("default - 3"), and leaves alone one named in the New-session dialog, by
an inline rename, or by an earlier `SetSessionName`. Tying a ticket to a session that is already running is the
suggesting case: the session should become recognisable, but not by taking away a name the operator chose.

```csharp
// Linking a ticket to a running session: say what it is on, and offer the ticket as its name.
_ = host.SetSessionStatusline(paneId, $"{issue.IdReadable} — {issue.Summary}");
_ = host.SuggestSessionName(paneId, issue.IdReadable);
```

## `ICockpitActions` {#icockpitactions}

Act on the cockpit and the running session.

```csharp
public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);
    Task InjectIntoActiveSessionAsync(string text);
    bool HasActiveSession { get; }
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm");  // default true
    Task<string> StartSessionAsync(string profileLabel, string? prompt = null,
                                   string? workingDirectory = null);                         // default throws
    Task<string> StartSessionAsync(string profileLabel, string? prompt,
                                   string? workingDirectory, string? sessionName);           // default throws
    Task<string> DelegateAsync(string profileLabel, string prompt,
                               string? workingDirectory = null, TimeSpan? timeout = null);   // default throws
    Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory,
                               TimeSpan? timeout, string? permission);                       // default: the above
}
```

### `Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null)` {#taskstring-delegateasyncstring-profilelabel-string-prompt-string-workingdirectory--null-timespan-timeout--null}
Hands work to another profile as a **background task** and waits for what it produces (#67) — the cockpit's own
delegation, done for a plugin. Returns the profile's answer.

It goes through the same delegation service an agent's `delegate` tool goes through, so it is refused by the same
rules and it appears in the delegated-tasks view: a plugin does not get a quieter way to run an agent than an agent
has. Throws when the profile refused the work, when it failed, and when the timeout passes — a caller that got no
answer must not be handed an empty string and left to treat it as one. On timeout the task keeps running; it is real
work, and discarding it because the caller grew impatient would throw away whatever it had done.

**The task runs read-only.** It may read and report; its file writes and shell commands are refused by the host, and
the refusal reaches the model as the tool's result. Use the five-argument overload with `permission:` —
`"acceptEdits"` to let it change files, `"bypassPermissions"` to also let it run commands — for work that is meant to
change something. Anything above the target profile's own ceiling is put to the operator as a one-time approval
rather than granted. A plugin compiled against the older four-argument overload keeps working and gets the read-only
default, which is the safe end of that choice.

### `Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null)` {#taskstring-startsessionasyncstring-profilelabel-string-prompt--null-string-workingdirectory--null}
Opens a session on the profile with that label and hands it `prompt` as its first input — the New-session dialog's act,
without the dialog. The profile's own defaults decide model, permissions and effort: naming a profile means "the way I
set that one up". `workingDirectory` overrides the profile's, for the flow that has just cut a branch in one repo.

Returns the name the session was given. Throws when no profile carries that label, listing the ones that do — guessing
between profiles would run someone's work on the wrong model, in the wrong directory, with the wrong permissions, and
the caller would never learn that it had guessed. The default implementation throws `NotSupportedException`, so a
plugin on a host too old to start sessions finds out rather than silently getting none.

### `Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName)` {#taskstring-startsessionasyncstring-profilelabel-string-prompt-string-workingdirectory-string-sessionname}
The same act, with the session's name said up front — what the New-session dialog's name field does, for a caller that
has no dialog. A flow opening a session on a ticket can call it `AC-312` from the start instead of opening
`Claude — 14:22` and renaming it a step later.

A name passed here counts as a name somebody chose, so a ticket linked to that session afterwards will not replace it.
Leave it null and the profile and the clock name it, and that composed name stays open to being relabelled later.

It is a separate overload rather than a fourth optional parameter on the three-argument form, because adding one would
change that method's signature and every plugin zip already published calls it — so it needed no
`abstractionsVersion` bump of its own.

That protects an old plugin on a new host. The other direction is on you: plugins reference this assembly compile-only
and bind to the host's copy, so a host older than this member loads an SDK that does not have it and the call fails
before any default body runs. **A plugin calling this overload must raise its manifest's `minHostVersion`** — no
interface default can stand in for that.

### `Task SetClipboardTextAsync(string text)` {#task-setclipboardtextasyncstring-text}
Puts `text` on the system clipboard. Use as a fallback when there is no active session to inject into.

### `Task InjectIntoActiveSessionAsync(string text)` {#task-injectintoactivesessionasyncstring-text}
Injects `text` into the **currently selected** session — appended to the input box for an SDK/local session,
written to the pty for a TTY session. **No-op when `HasActiveSession` is false.**
```csharp
if (host.Actions.HasActiveSession)
    await host.Actions.InjectIntoActiveSessionAsync(prompt);
else
    await host.Actions.SetClipboardTextAsync(prompt);
```

### `bool HasActiveSession { get; }` {#bool-hasactivesession--get}
True when a session is selected (so `InjectIntoActiveSessionAsync` will land). Check it before injecting.

---

## `IPluginStorage` {#ipluginstorage}

Per-plugin key/value storage, persisted in a plugin-scoped slice of the host's `cockpit.json`. Values are
JSON-serialized.

```csharp
public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);

    void SetSecret(string key, string value);   // default: Set(key, value)
    string? GetSecret(string key);              // default: Get<string>(key)
}
```

### `T? Get<T>(string key)` {#t-gettstring-key}
Reads and deserializes the value for `key`, or `default(T)` (e.g. `null`) if unset. Provide a fallback:
`host.Storage.Get<string>("repo") ?? ""`.

### `void Set<T>(string key, T value)` {#void-settstring-key-t-value}
Serializes and persists `value` under `key`. Works for primitives and your own DTO types.
```csharp
host.Storage.Set("repo", "owner/name");
host.Storage.Set("options", new MyOptions { Token = "…", Filter = "open" });
```

### `void SetSecret(string key, string value)` / `string? GetSecret(string key)` {#void-setsecretstring-key-string-value--string-getsecretstring-key}

Stores a credential: a token, an API key, a webhook URL — anything that would be a problem in someone else's
hands. What is stored this way is **encrypted at rest** whenever the operator has turned that on (Options →
Security), and is emptied from a backup that says it carries no credentials.

You may not need it. The host already recognises the usual field names — `token`, `apiKey`, `api_key`, `secret`,
`password`, `webhook` — anywhere in the settings, including inside your own JSON, so a plain `Set("token", …)` is
covered. This is for the names it cannot guess:

```csharp
host.Storage.SetSecret("pat", token);          // "pat" is not a name the host would recognise
var token = host.Storage.GetSecret("pat");
```

Or declare them in `plugin.json`, which also covers values written before you added this, and lets the store show
at install time which credentials your plugin intends to keep:

```json
{ "secretKeys": ["pat"] }
```

Both carry default implementations, so an existing plugin keeps compiling and keeps working. Declare when in
doubt: a field that is not really a secret costs nothing by being treated as one, while one that is — and is not
declared — sits in the clear in a config the operator believes is encrypted.

**What this does not do:** it protects the file, not a running cockpit. Your plugin runs inside the host process
with the operator's full rights, and so does every other plugin they installed. The boundary is the install, not
the runtime.

---

## `IPluginSettingsView` {#ipluginsettingsview}

Optional interface your **settings control** (the one passed to `AddSettings`) implements to get a standard
**Save** button in the host's settings screen.

```csharp
public interface IPluginSettingsView
{
    bool TryStage(out Action? commit, out string? error);
}
```

### `bool TryStage(out Action? commit, out string? error)` {#bool-trystageout-action-commit-out-string-error}
Validate the current field values **without writing anything**, and hand the host the write to perform.

| Member | Meaning |
|---|---|
| `commit` | Runs your persistence — `host.Storage` writes and whatever else saving means for you (registering an MCP server, dropping an entry that is now orphaned). The host calls it at most once, when the operator confirms, and not at all when they cancel. |
| `error` | One line the operator can act on, shown by the host when you return `false`. Say what is wrong and what to do about it. |

**Why you don't write yourself:** your settings may sit inside the cockpit's Options screen, which is one
staged transaction — Cancel there has to take your change back too, and it cannot take back a write that has
already happened. A standalone settings window (your own gear, a widget's gear) stages and commits in the same
click, so one implementation serves both.

A settings view that applies changes live can skip this interface and just gets a Close button.

```csharp
public sealed class MySettingsControl : UserControl, IPluginSettingsView
{
    public bool TryStage(out Action? commit, out string? error)
    {
        if (string.IsNullOrWhiteSpace(_repo.Text))
        {
            commit = null;
            error = "Fill in the repository (owner/name) first.";  // screen stays open, host shows this
            return false;
        }

        var repo = _repo.Text.Trim();
        commit = () => _storage.Set("repo", repo);                 // the host runs this, not you
        error = null;
        return true;
    }
}
```

Migrating from `bool Save()`? See [the migration steps](PLUGIN-SDK.md#migrating-from-bool-save--contract-1--2).

---

## `IPluginSettingsSections` {#ipluginsettingssections}

Optional interface your **settings control** implements when it has grown past one screenful: name your
sections and the host draws the same left navigation rail the cockpit's own Options dialog uses, instead of
stacking everything into one scroll.

```csharp
public interface IPluginSettingsSections
{
    IReadOnlyList<string> SectionTitles { get; }
    void ShowSection(int index);
}
```

Your control stays the one thing the host renders — it is not replaced or taken apart. The host asks it to
show a section; swapping its own content is your business, so everything a settings view already relies on
(its attach/detach lifetime, the fields `TryStage()` reads) is untouched. Save stays one shared footer across
all sections: a section is a page of the same form, not a form of its own.

The rail appears **from two sections up** — beside a single page it costs width and navigates nothing — and a
control that does not implement this gets exactly the dialog it has today.

```csharp
public sealed class MySettingsControl : UserControl, IPluginSettingsView, IPluginSettingsSections
{
    private readonly List<string> _titles = [];
    private readonly List<Control> _pages = [];

    public MySettingsControl()
    {
        var connection = _Section("Connection");
        connection.Children.Add(/* … */);

        var advanced = _Section("Advanced");
        advanced.Children.Add(/* … */);

        ShowSection(0);
    }

    public IReadOnlyList<string> SectionTitles => _titles;

    public void ShowSection(int index) => Content = _pages[index];

    private StackPanel _Section(string title)
    {
        var page = new StackPanel { Spacing = 10 };
        _titles.Add(title);
        _pages.Add(page);
        return page;
    }
}
```

Make `ShowSection` **idempotent**: the host shows section 0 when the dialog opens, so a control that also picks
its opening section in its constructor (as above) is asked for that one twice.

> **Set `minHostVersion` to `0.7.0`** when you implement this. Implementing an interface is not the safer half of
> the contract it looks like: your plugin does not ship `Cockpit.Plugins.Abstractions` — it binds to the host's own
> copy — so a host that predates this interface cannot load your settings control's type at all. That is a
> `TypeLoadException` the moment the operator clicks the gear, and a silent one, because opening the settings
> dialog is fire-and-forget: the gear simply does nothing. The version gate is what keeps the plugin off those
> hosts.

---

## `PluginMetadata` {#pluginmetadata}

The identity you return from `ICockpitPlugin.Metadata`.

```csharp
public sealed record PluginMetadata(string Id, string DisplayName, string Version = "", string? Author = null, string? Description = null);
```

| Field | Type | Meaning |
|---|---|---|
| `Id` | `string` | Stable identity (match your `plugin.json` `id`). |
| `DisplayName` | `string` | Shown in the Plugins manager. |
| `Version` | `string` | **Do not set this.** Your version lives in `plugin.json`; the host fills this in from the manifest when it reports your plugin through `InstalledPlugins`. |
| `Author` | `string?` | Optional. |
| `Description` | `string?` | Optional one-liner. |

```csharp
public PluginMetadata Metadata { get; } =
    new(Id: "github-issues", DisplayName: "GitHub Issues", Author: "You", Description: "Browse and inject GitHub issues.");
```

---

## The `Sessions` namespace — provider plugins {#the-sessions-namespace--provider-plugins}

Everything under `Cockpit.Plugins.Abstractions.Sessions`, used with `ICockpitHost.AddSessionProvider` (#45) to
register a plugin as a **new selectable session provider** — the same picker slot as the built-in Claude
CLI / Ollama / LM Studio providers. Three real plugins in [`plugins-dev/`](https://github.com/raymondkrahwinkel/AI-Cockpit/tree/main/plugins-dev) exercise this:
**Gemini/OpenAI Provider** and **GitHub Models** (both a persistent `IChatClient` over an OpenAI-compatible
endpoint) and **CLI Agent Provider** (a subprocess-per-turn driver around the `codex` CLI).

This is a deliberately **narrow** contract — a trimmed mirror of the host's own internal `ISessionDriver` —
covering only what a third-party HTTP or subprocess provider can realistically support. There is no
Claude-CLI-only live model switch, plan mode, thinking-budget control, or always-allow rule persistence; the
host's own adapter (`PluginSessionDriverAdapter`, internal to the app) wraps your driver to satisfy the real
`ISessionDriver` contract and no-ops the members this interface has no equivalent for.

### `SessionProviderRegistration` {#sessionproviderregistration}

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

### `IPluginSessionDriverFactory` {#ipluginsessiondriverfactory}

```csharp
public interface IPluginSessionDriverFactory
{
    IPluginSessionDriver Create(string configJson);
}
```

Creates the driver for one profile. `configJson` is the profile's opaque config string — **your own record's
shape**, serialized by your `IPluginProviderConfigView.TryGetConfigJson` and deserialized back here; the host
never inspects it.

### `IPluginSessionDriver` {#ipluginsessiondriver}

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
| `RespondToPermissionAsync` | Resolves an outstanding `PluginPermissionRequested` — the operator's allow/deny, correlated on `toolUseId`. Only relevant if `Capabilities.SupportsPermissions`. An overload also carries a `denyReason`: on a delegated session the host answers these itself against the task's permission ceiling, and no operator denied anything, so a driver that can pass a reason to its agent should implement it. The default drops it. |
| `Events` | The live, ordered stream of typed events — see below. |
| `SetAutoApproveToolsAsync` | Toggles per-tool-call approval prompts on/off. Default no-op — a driver with no tool source of its own has nothing to gate. |
| `DisposeAsync` *(`IAsyncDisposable`)* | Tears down the subprocess/HTTP client/etc. |

### `PluginSessionCapabilities` {#pluginsessioncapabilities}

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

`DeclaredOptions` is the schema behind the otherwise opaque options map: which keys this driver reads, what they
mean and which values they take. It is init-only and empty by default, so an existing plugin keeps its constructor
and simply declares nothing.

```csharp
Capabilities = new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true)
{
    DeclaredOptions =
    [
        new("sandbox", "Sandbox", [new("read-only", "Read only"), new("workspace-write", "Workspace write")], "read-only"),
        new("model", "Model"),   // no known values = free-form
    ],
};
```

Declare only what your driver actually reads. `PluginSessionLaunchOption` (on the registration) asks the
New-session dialog to render a control; this one renders nothing and only states what exists.

### `IPluginProviderConfigView` {#ipluginproviderconfigview}

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

### The event vocabulary — `PluginSessionEvent` and its subtypes {#the-event-vocabulary--pluginsessionevent-and-its-subtypes}

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

## The `Mcp` namespace — MCP server registration {#the-mcp-namespace--mcp-server-registration}

Everything under `Cockpit.Plugins.Abstractions.Mcp`, used with `ICockpitHost.AddMcpServer` (#60) to register
an **HTTP MCP server** into the shared registry — e.g. the YouTrack plugin registering each configured
instance's JetBrains remote MCP endpoint so sessions get YouTrack tools without the user adding the server by
hand.

### `McpServerContribution` {#mcpservercontribution}

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

### `McpContributionScope` {#mcpcontributionscope}

```csharp
public enum McpContributionScope
{
    All,        // every session — both the local-model tool-loop and Claude Code
    LocalOnly,  // only local models (Ollama/LM Studio); never fanned out to Claude Code
    ClaudeOnly, // only fanned out to Claude Code; never hosted in the local-model tool-loop
}
```

The YouTrack plugin's own registration helper (real code from
[`plugins-dev/Cockpit.Plugin.YouTrack/YouTrackMcpRegistration.cs`](https://github.com/raymondkrahwinkel/AI-Cockpit/blob/main/plugins-dev/Cockpit.Plugin.YouTrack/YouTrackMcpRegistration.cs)),
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

## Minimal plugin {#minimal-plugin}

```csharp
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

public sealed class MyPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(Id: "my-plugin", DisplayName: "My Plugin", Author: "You", Description: "Does a thing.");

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
[GitHub Issues plugin](https://github.com/raymondkrahwinkel/AI-Cockpit/tree/main/plugins-dev/Cockpit.Plugin.GitHubIssues) for a full example.
