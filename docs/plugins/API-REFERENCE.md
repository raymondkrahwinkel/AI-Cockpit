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
    void AddSupervisedActivityProvider(ISupervisedActivitySource source);        // default no-op
    void AddConversationPicker(ConversationPickerRegistration picker);           // default no-op
    void AddWorkflowStep(IWorkflowStep step);                                   // default no-op
    IReadOnlyList<IWorkflowStep> WorkflowSteps { get; }                         // default []
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
    void OnSettingsSaved(Action callback);                       // default no-op
    void AddSessionProvider(SessionProviderRegistration registration); // default no-op
    void AddWidget(WidgetRegistration registration);             // default no-op
    IReadOnlyList<WidgetRegistration> Widgets { get; }           // default []
    Task AddMcpServer(McpServerContribution contribution);       // default no-op, returns Task.CompletedTask
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync();   // default returns []
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information,
                   string? actionLabel = null, Action? onAction = null);        // default no-op
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

### `void AddWidget(WidgetRegistration registration)`
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

### `IReadOnlyList<WidgetRegistration> Widgets { get; }`
Every widget type all plugins have contributed — what a Dashboard workspace's "Add widget" gallery reads. A
plugin that is not building that gallery has no reason to touch it. Default empty.

### `WidgetRegistration`
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

### `IWidgetContext`
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

### `void AddSupervisedActivityProvider(ISupervisedActivitySource source)`
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
| `string PaneId` | Identifies this session pane for as long as it exists. Match it against `ICockpitSessionObserver.ActivePaneId` to know whether an action taken *outside* a session (in a dialog, say) was meant for this one. **Not** the provider's conversation id — panes come and go with the window, and two panes can resume the same conversation. Empty on a host that predates it. |
| `string? WorkingDirectory` | The directory this session is working in; null until known (an SDK session before its init event). |
| `event EventHandler? WorkingDirectoryChanged` | The directory became known or changed — re-scope. |
| `event EventHandler<SessionOutputText>? OutputProduced` | Each chunk of text **this** session produced, verbatim. Substring-scan it for a signal (a git command, a pushed branch, …). |

Events are raised on the UI thread, so a handler can touch its controls directly.

A dialog belongs to no session, so an action it takes "for the current session" needs naming: read
`host.Sessions.ActivePaneId`, hand that to your own state, and let the header item whose `PaneId` matches pick
it up. That is how the YouTrack plugin starts an issue from its dialog and has it appear in the right session's
header — with four panes open, "the session" is not obvious, and guessing would put the ticket on the wrong one.

### `void AddWorkflowStep(IWorkflowStep step)`
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
        ["ticket"] = "EVE-14",
        ["state"] = "In Progress",
    };

    public async Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
    {
        var ticket = context.Parameter("Ticket");  // already resolved: {ticket} became EVE-14 before you saw it
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
on ("EVE-14 cannot go to 'Done'. Its board allows: Review, Reopened."). Returning success without having done the work
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

### `IReadOnlyList<IWorkflowStep> WorkflowSteps { get; }`
Every step all plugins contributed. Only the workflows plugin has a reason to read this; it does so when its editor
opens, not at startup, because plugins initialise in an order nobody controls.

### `void AddConversationPicker(ConversationPickerRegistration picker)`
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

### `void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction)`
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

### `Task<ConsentDecision> RequestConsentAsync(ConsentRequest request)`
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

## `ICockpitActions`

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
    Task<string> DelegateAsync(string profileLabel, string prompt,
                               string? workingDirectory = null, TimeSpan? timeout = null);   // default throws
}
```

### `Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null)`
Hands work to another profile as a **background task** and waits for what it produces (#67) — the cockpit's own
delegation, done for a plugin. Returns the profile's answer.

It goes through the same delegation service an agent's `delegate` tool goes through, so it is refused by the same
rules and it appears in the delegated-tasks view: a plugin does not get a quieter way to run an agent than an agent
has. Throws when the profile refused the work, when it failed, and when the timeout passes — a caller that got no
answer must not be handed an empty string and left to treat it as one. On timeout the task keeps running; it is real
work, and discarding it because the caller grew impatient would throw away whatever it had done.

### `Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null)`
Opens a session on the profile with that label and hands it `prompt` as its first input — the New-session dialog's act,
without the dialog. The profile's own defaults decide model, permissions and effort: naming a profile means "the way I
set that one up". `workingDirectory` overrides the profile's, for the flow that has just cut a branch in one repo.

Returns the name the session was given. Throws when no profile carries that label, listing the ones that do — guessing
between profiles would run someone's work on the wrong model, in the wrong directory, with the wrong permissions, and
the caller would never learn that it had guessed. The default implementation throws `NotSupportedException`, so a
plugin on a host too old to start sessions finds out rather than silently getting none.

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

    void SetSecret(string key, string value);   // default: Set(key, value)
    string? GetSecret(string key);              // default: Get<string>(key)
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

### `void SetSecret(string key, string value)` / `string? GetSecret(string key)`

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
