using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// What the host offers a plugin during <see cref="ICockpitPlugin.Initialize"/>: the built service
/// provider, cockpit actions, per-plugin storage, and the contribution points — a settings view (opened
/// from the plugin manager's gear), a left-menu launcher button and/or an inline left-menu section, and a
/// helper to open a modal dialog. This facade is the contract's only intended growth surface — new
/// capabilities are added here (as default interface methods) rather than by widening the other interfaces.
/// </summary>
public interface ICockpitHost
{
    IServiceProvider Services { get; }

    ICockpitActions Actions { get; }

    IPluginStorage Storage { get; }

    /// <summary>Registers the plugin's settings view, opened from the gear next to the plugin in the plugin manager. Call at most once.</summary>
    void AddSettings(Func<Control> createView);

    /// <summary>Adds a launcher button to the left menu; clicking runs <paramref name="onInvoke"/> — typically opening a dialog via <see cref="ShowDialogAsync"/>.</summary>
    void AddSideMenuButton(string title, Action onInvoke);

    /// <summary>Adds an inline accordion section to the left menu, under the session list — for small, always-visible content.</summary>
    void AddSideMenuSection(string title, Func<Control> createView);

    /// <summary>
    /// Adds a small control to <em>every session's header bar</em>, built once per session and handed that
    /// session's own <see cref="IPluginSessionContext"/> — for status that belongs to the session it describes
    /// (the git state of the repo it is working in, say) rather than to the cockpit as a whole. Keep it compact:
    /// the header is a strip, so this is the place for an indicator with a tooltip, not for a panel. Default
    /// no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep
    /// compiling untouched — only the app's own host renders it.
    /// </summary>
    /// <param name="createView">Builds the control for one session; invoked once per session panel, on the UI thread.</param>
    void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView)
    {
    }

    /// <summary>
    /// Adds an action to the menu in <em>every session's header</em> — "Track a YouTrack issue…", "Open this repo on
    /// GitHub". Handed the session it was invoked from, so it acts on that pane rather than on whichever one happens
    /// to be selected.
    /// <para>
    /// Prefer this to <see cref="AddSessionHeaderItem"/> for anything the operator <em>does</em>. A header item is a
    /// control that is always there; two plugins offering the same act meant two buttons in a strip that has room for
    /// neither. Keep header items for what a session has to <em>say</em> — a badge, an indicator — and let it hide
    /// itself when it has nothing.
    /// </para>
    /// Default no-op so existing hosts keep compiling untouched.
    /// </summary>
    void AddSessionHeaderAction(PluginSessionAction action)
    {
    }

    /// <summary>
    /// Registers a source of long-running, agent-started background activities shown in the app status bar (AC-82) —
    /// a counter next to "Delegated tasks" that appears only while something is running, and opens a panel listing
    /// each activity with its details and a Kill button. The host owns that Kill: an agent cannot start or stop
    /// through it, only the operator can. The plugin supplies the list and the stop callback. This is the
    /// operator-facing kill-switch that a port-forward — or any other supervised background work — needs to be safe.
    /// Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep
    /// compiling untouched — only the app's own host renders it.
    /// </summary>
    void AddSupervisedActivityProvider(ISupervisedActivitySource source)
    {
    }

    /// <summary>
    /// Adds a button to the Sessions toolbar (AC-91) — a global, cockpit-wide quick action next to the workspace
    /// gear, for something the operator reaches often regardless of which session is selected: opening this plugin's
    /// settings (<see cref="ShowSettingsAsync"/>), say, or any other action. Keep it to an icon with a tooltip; the
    /// strip is narrow, and when several plugins contribute the host collapses them into an overflow menu. Provider-
    /// neutral by design — any plugin drops a quick action here the same way. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched — only
    /// the app's own host renders it.
    /// </summary>
    void AddToolbarAction(ToolbarAction action)
    {
    }

    /// <summary>
    /// Registers a way to pick an earlier conversation to resume — see <see cref="ConversationPickerRegistration"/>.
    /// The New-session dialog can resume a conversation by id; with a picker registered it also shows a search
    /// button that runs yours, so the operator chooses a conversation instead of typing an id. Default no-op so
    /// existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host wires it up.
    /// </summary>
    void AddConversationPicker(ConversationPickerRegistration picker)
    {
    }

    /// <summary>
    /// Contributes a step to the workflow editor (#69) — "Move a ticket to In Progress", "Comment on a pull request".
    /// The step appears in the picker under its own category and runs like any other. Without this, what a flow can do
    /// is limited to what the workflows plugin itself was built to do, and every integration the cockpit ever grows
    /// would have to be built there, by someone who does not have your API client in front of them. Default no-op so
    /// existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched.
    /// </summary>
    void AddWorkflowStep(IWorkflowStep step)
    {
    }

    /// <summary>
    /// The steps every plugin has contributed — what the workflows plugin reads to build its picker. A plugin that is
    /// not the workflows plugin has no reason to call this. Default empty.
    /// </summary>
    IReadOnlyList<IWorkflowStep> WorkflowSteps => [];

    /// <summary>
    /// Contributes a ready-made flow (#69) — "a ticket you pick becomes a branch, an agent and a status change". A
    /// plugin that contributes steps knows how they fit together; a template is that knowledge, offered instead of an
    /// empty canvas. Shown in the workflows plugin's "New flow" picker under this plugin's name. Default no-op so
    /// existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched.
    /// </summary>
    void AddWorkflowTemplate(WorkflowTemplate template)
    {
    }

    /// <summary>The templates every plugin has contributed — what the workflows plugin reads to build its "New flow" picker. Default empty.</summary>
    IReadOnlyList<WorkflowTemplate> WorkflowTemplates => [];

    /// <summary>
    /// Fires a trigger this plugin contributed (an <see cref="IWorkflowStep"/> whose <see cref="IWorkflowStep.IsTrigger"/>
    /// is true): a ticket was picked for a session, a review was requested. Every active flow that begins with that
    /// trigger runs, starting with <paramref name="data"/>.
    /// <para>
    /// Fire it when the thing actually happened, not when it might have. A trigger that fires on a poll which saw the
    /// same state as last time turns an automation into a machine that repeats itself.
    /// </para>
    /// </summary>
    void RaiseWorkflowTrigger(string typeId, IReadOnlyDictionary<string, string> data)
    {
    }

    /// <summary>Raised when any plugin fires a trigger — what the workflows plugin listens to. No other plugin has a reason to.</summary>
    event EventHandler<WorkflowTriggerFired>? WorkflowTriggerRaised
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Registers a handler for an intent other plugins can send to this one (AC-95), under <paramref name="action"/> —
    /// the receiving half of <see cref="SendIntent"/>. The host stamps the calling plugin's id on every intent it
    /// delivers, so <paramref name="handler"/> can trust <see cref="PluginIntent.CallerPluginId"/>. Registering the
    /// same action twice from one plugin throws — one handler per action, so which one runs is never a question of
    /// load order. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin
    /// builds) keep compiling untouched — only the app's own host wires it up.
    /// </summary>
    void RegisterIntentHandler(string action, Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> handler)
    {
    }

    /// <summary>
    /// Sends an intent to the plugin with id <paramref name="targetPluginId"/> and returns its handler's result, or
    /// <see langword="null"/> when that plugin is not installed or registered no handler for <paramref name="action"/>
    /// (AC-95). Addressing is by manifest id and an agreed action string, so the caller need not reference the
    /// target's types — the same loose coupling the workflow steps use. The host stamps this plugin's own id as
    /// <see cref="PluginIntent.CallerPluginId"/>; a plugin cannot send under another's name. Default returns
    /// <see langword="null"/> so existing <see cref="ICockpitHost"/> implementations keep compiling untouched — only
    /// the app's own host dispatches.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>
    /// Whether the plugin with id <paramref name="targetPluginId"/> has registered a handler for
    /// <paramref name="action"/> (AC-95) — what a plugin checks before offering a menu item ("Start in Autopilot")
    /// that would otherwise dispatch to nobody, the same way <see cref="HasSettings"/> gates a Configure button.
    /// The id and action are matched case-sensitively (see <see cref="PluginIntent"/>). Check it when the operator is
    /// about to act (building a context menu, a button click) rather than from your own
    /// <see cref="ICockpitPlugin.Initialize"/>: handlers are registered during each plugin's Initialize, so a target
    /// that loads after you has not registered yet when yours runs. Default <see langword="false"/> so existing
    /// <see cref="ICockpitHost"/> implementations keep compiling untouched — only the app's own host reports the real answer.
    /// </summary>
    bool CanSendIntent(string targetPluginId, string action) => false;

    /// <summary>
    /// Registers an Autopilot goal/brief template this plugin contributes (AC-189) — the template equivalent of
    /// <see cref="AddWorkflowTemplate"/>. The Autopilot plugin collects every registered template (with the host
    /// stamping this plugin's own id as its owner, the same way <see cref="RegisterIntentHandler"/> does) into the
    /// list an operator picks a run's brief from. Registrations live only in memory — call this from
    /// <see cref="ICockpitPlugin.Initialize"/> on every start. Default no-op so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host wires it up.
    /// </summary>
    void RegisterAutopilotTemplate(PluginAutopilotTemplate template)
    {
    }

    /// <summary>The Autopilot templates every plugin has contributed — what the Autopilot plugin reads to build its template picker. Default empty.</summary>
    IReadOnlyList<RegisteredAutopilotTemplate> RegisteredAutopilotTemplates => [];

    /// <summary>Opens a modal dialog over the main window hosting <paramref name="createContent"/>; the plugin owns the content control.</summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);

    /// <summary>
    /// Opens this plugin's own settings — the view it registered with <see cref="AddSettings"/>, in the same
    /// dialog the plugin manager's gear opens, saved the same way. It is what a plugin calls from the place the
    /// operator is when they discover something is missing: a dialog that has to say "no instances configured"
    /// can offer the way to configure one instead of naming a screen elsewhere in the app. Does nothing when the
    /// plugin registered no settings view. Default no-op so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host opens it.
    /// </summary>
    Task ShowSettingsAsync() => Task.CompletedTask;

    /// <summary>
    /// Whether this plugin registered a settings view (<see cref="AddSettings"/>) — what a plugin checks before
    /// offering a "Configure…" button that would otherwise do nothing. A plugin that always calls
    /// <see cref="AddSettings"/> in <see cref="ICockpitPlugin.Initialize"/> already knows the answer and has no
    /// reason to ask.
    /// </summary>
    bool HasSettings => false;

    /// <summary>
    /// Registers <paramref name="callback"/> to run (on the UI thread) after this plugin's own settings are
    /// saved from the plugin manager's gear (#52) — i.e. <see cref="IPluginSettingsView.Save"/> returned
    /// true. A contribution that read settings once at construction and cached the result (e.g. a side-menu
    /// section's already-fetched list) should subscribe here and reload, so a settings change takes effect
    /// immediately instead of requiring an app restart. A contribution that reads <see cref="IPluginStorage"/>-backed
    /// settings fresh on every access (the common case — see <see cref="Storage"/>) already reflects a save
    /// without this. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older
    /// plugin builds) keep compiling untouched — only the app's own host overrides it.
    /// </summary>
    void OnSettingsSaved(Action callback)
    {
    }

    /// <summary>
    /// Registers a new session provider (#45) — the plugin equivalent of the built-in Claude-CLI/Ollama/LM-Studio
    /// providers: it becomes selectable in the New-session/Manage-profiles provider picker, backed by the
    /// plugin's own <see cref="IPluginSessionDriver"/> and config view. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host overrides it.
    /// </summary>
    void AddSessionProvider(SessionProviderRegistration registration)
    {
    }

    /// <summary>
    /// Registers the provider's CLI as one that can run as the real interactive TUI in a pane — the plugin
    /// equivalent of the built-in <c>claude</c> TTY mode.
    /// <para>
    /// Separate from <see cref="AddSessionProvider"/> rather than a field on it, because a provider offers what
    /// it can: a local model has no TUI, a TUI-only agent has no headless driver, and Claude and Codex have both.
    /// A provider that registers both uses the same <see cref="TtyProviderRegistration.ProviderId"/> for each —
    /// a profile names a provider, and what that provider can do is what it registered.
    /// </para>
    /// Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds)
    /// keep compiling untouched — only the app's own host overrides it.
    /// </summary>
    void AddTtyProvider(TtyProviderRegistration registration)
    {
    }

    /// <summary>
    /// Registers (or updates) an HTTP MCP server in the shared registry (#60) — e.g. a YouTrack/JetBrains
    /// remote MCP endpoint — so both session worlds (the local tool-loop and the Claude fan-out) can use its
    /// tools without the user having to add it by hand in the MCP-servers dialog. Idempotent upsert-by-name:
    /// calling this again with the same <see cref="McpServerContribution.Name"/> refreshes the URL/token of
    /// an existing entry rather than adding a duplicate, and never force-changes an entry's enabled state or
    /// scope — a server the user disabled, rescoped, or deleted from the dialog stays that way (deleted
    /// means "absent", so it is treated like a first-time registration and re-added; see the host's own
    /// implementation for the exact rule). Returns a <see cref="Task"/> (not suffixed <c>Async</c> to match
    /// the requested #60 contract name) because the upsert persists to disk; call it fire-and-forget
    /// (<c>_ = host.AddMcpServer(...)</c>) from a synchronous callback such as <see cref="ICockpitPlugin.Initialize"/>,
    /// same as other async host operations invoked from sync contribution points. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host overrides it.
    /// </summary>
    Task AddMcpServer(McpServerContribution contribution) => Task.CompletedTask;

    /// <summary>
    /// Removes an MCP server from the shared registry by name (#60, AC-11), if it is there. A plugin that now
    /// owns its MCP servers through <see cref="Mcp.IPluginMcpProvider"/> uses this to reclaim the entries an
    /// earlier version pushed into the registry, so they stop appearing in the MCP-servers manager and are the
    /// plugin's to manage from here on. A no-op when no entry of that name exists. Returns a <see cref="Task"/>
    /// because it persists to disk; call it fire-and-forget from a synchronous contribution point, same as
    /// <see cref="AddMcpServer"/>. Default no-op so existing host implementations keep compiling untouched.
    /// </summary>
    Task RemoveMcpServer(string name) => Task.CompletedTask;

    /// <summary>
    /// Sets the short free-text statusline shown under a session's title, in its header and the sidebar (#AC-13) —
    /// what a workflow or plugin uses to say what that session is working on (a ticket it picked up from YouTrack or
    /// GitHub, a phase), or clears it with an empty string. The session is named by its <c>IPluginSessionContext.PaneId</c>
    /// (also <see cref="ICockpitSessionObserver.ActivePaneId"/>); a paneId that matches no live session is a no-op.
    /// Marshals to the UI thread itself, so call it fire-and-forget from any context. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched.
    /// </summary>
    Task SetSessionStatusline(string paneId, string statusline) => Task.CompletedTask;

    /// <summary>
    /// Renames a session — the title shown in its header and the sidebar — named by its <c>IPluginSessionContext.PaneId</c>
    /// (#AC-13), so a workflow can label a session after the ticket or task it just started on it. A blank name is
    /// ignored; a paneId that matches no live session is a no-op. Marshals to the UI thread itself. Default no-op so
    /// existing <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </summary>
    Task SetSessionName(string paneId, string name) => Task.CompletedTask;

    /// <summary>
    /// Sends <paramref name="text"/> to the session named by <paramref name="paneId"/> as a submitted turn — the seam
    /// a plugin uses to hand a started session (including one it embedded in its own workspace) a prompt without a
    /// human turn, e.g. an Autopilot run's work brief once the operator has approved the run. A paneId that matches no
    /// live session is a no-op, never an error. Marshals to the UI thread itself. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched.
    /// </summary>
    Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

    /// <summary>
    /// Creates one git worktree for a multi-session run (AC-174, Raymond 2026-07-22) and returns its path and branch, or
    /// null when <paramref name="repositoryDirectory"/> is not a git repository or the host has no worktree manager. An
    /// Autopilot run creates one at its start and passes the returned <see cref="Workspaces.PluginWorktreeInfo.Path"/> to
    /// every step's <see cref="Workspaces.EmbeddedSessionRequest.WorktreePath"/>, so the steps share it and their work
    /// accumulates on the one branch instead of a throwaway worktree per step. The worktree persists after the run — it
    /// is the merge-ready deliverable — and is managed from the Worktrees panel like any other. Default null so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older builds) keep compiling untouched.
    /// </summary>
    Task<Workspaces.PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Workspaces.PluginWorktreeInfo?>(null);

    /// <summary>
    /// Reports whether <paramref name="directory"/> is a git repository (AC-174), so a plugin can decide up front whether
    /// work there can be isolated in a worktree — a run in a real repo isolates each step, a run in a plain folder (an
    /// admin task with no repo) cannot, and must be handled deliberately rather than failing at the first step. The
    /// default is <see cref="Workspaces.GitDirectoryStatus.Unknown"/>, not a bool, so the decision stays fail-closed: an
    /// older host (or a failed probe) returns Unknown, which a caller treats as "isolate / do not run free", and only a
    /// host that positively answers <see cref="Workspaces.GitDirectoryStatus.NotARepository"/> licenses running without
    /// isolation. Default Unknown so existing hosts (test fakes, older builds) keep compiling untouched.
    /// </summary>
    Task<Workspaces.GitDirectoryStatus> DetectGitDirectoryStatusAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.FromResult(Workspaces.GitDirectoryStatus.Unknown);

    /// <summary>
    /// The working directories the cockpit remembers for its New-session quick-pick (AC-174), so a plugin that asks the
    /// operator to name a working directory can offer the same pinned favorites and recents instead of a blank field.
    /// Default <see cref="Workspaces.PluginRememberedWorkingPaths.Empty"/> so existing hosts keep compiling untouched.
    /// </summary>
    Task<Workspaces.PluginRememberedWorkingPaths> GetRememberedWorkingPathsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Workspaces.PluginRememberedWorkingPaths.Empty);

    /// <summary>
    /// Records <paramref name="directory"/> as most-recently-used in the shared working-directory history (AC-174), so a
    /// folder the operator picked in a plugin (Autopilot's plan) shows up in the same quick-pick next time — here and in
    /// the New-session dialog. A blank path is a no-op. Default no-op so existing hosts keep compiling untouched.
    /// </summary>
    Task RememberWorkingPathAsync(string directory, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Opens the cockpit's own New-session dialog (#AC-96), optionally pre-filled from <paramref name="prefill"/>, and
    /// starts the session the operator confirms — the plugin equivalent of the operator pressing "New session", with
    /// the fields it knows already offered. The operator keeps full control: they see and can change every field
    /// (profile, MCP selection, working tree, resume) before anything starts, and cancelling starts nothing.
    /// <para>
    /// <paramref name="onStarted"/> is invoked with the new session's <c>IPluginSessionContext.PaneId</c> — the pane
    /// becomes the active session the moment it starts, so it is <see cref="ICockpitSessionObserver.ActivePaneId"/>
    /// then, though the operator may select another pane afterwards. The id stays valid to act on that exact pane —
    /// set its statusline, track an issue against it. <paramref name="onCancelled"/> fires instead when the
    /// operator dismisses the dialog (or no session could be started), so a workflow waiting on the session can stop
    /// rather than hang. Exactly one of the two runs. Unlike <see cref="ICockpitActions.StartSessionAsync"/>, which
    /// launches a named profile headlessly, this always shows the dialog — it is the path for "let the operator decide,
    /// then tell me which session they made".
    /// </para>
    /// Default no-op (and no callback) so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin
    /// builds) keep compiling untouched — only the app's own host shows the dialog.
    /// </summary>
    /// <param name="prefill">The fields to seed the dialog with, or <see langword="null"/> to open it on its own defaults.</param>
    /// <param name="onStarted">Invoked with the started session's pane id when the operator confirms; not called if they cancel.</param>
    /// <param name="onCancelled">Invoked when the operator cancels or no session could be started; not called once a session starts.</param>
    Task ShowNewSessionDialogAsync(
        NewSessionPrefill? prefill = null,
        Action<string>? onStarted = null,
        Action? onCancelled = null) => Task.CompletedTask;

    /// <summary>
    /// Adds an in-process MCP server to the cockpit (#AC-12): the host mounts <paramref name="tools"/> — an already-
    /// built class whose <c>[McpServerTool]</c> methods are the tools, constructed by the plugin with its own
    /// dependencies — on a loopback address under <paramref name="serverName"/>. This is how a plugin gives agents
    /// its own tools (workflows, say) without any Kestrel code. The endpoint is the cockpit's own and is not written
    /// to the operator's MCP-servers registry (AC-40); the session fan-out sees it live. Idempotent per name.
    /// <paramref name="isEnabled"/> gates it on the plugin's own setting — read each time servers are gathered, so a
    /// toggle takes effect live; <see langword="null"/> means always on. Call it fire-and-forget from
    /// <see cref="ICockpitPlugin.Initialize"/>. Default no-op so existing host implementations keep compiling.
    /// </summary>
    Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled = null) => Task.CompletedTask;

    /// <summary>
    /// The read/observe surface over the cockpit's sessions (the contract's first "read-as" capability):
    /// the active session's working directory and a stream of session output, so a plugin can react to what
    /// a session is doing rather than only writing into it. Default returns
    /// <see cref="NullCockpitSessionObserver.Instance"/> so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host supplies a live one.
    /// </summary>
    ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

    /// <summary>
    /// The pane id of the session behind the in-process MCP call currently being handled — the transport-verified
    /// caller (AC-128), so a plugin's own MCP tool can act on the calling session rather than on a session id the
    /// agent hands it (a confused deputy). Null outside an MCP call, and on an older host that predates this — so a
    /// plugin uses the agent-supplied id only as a fallback when this is null. Default null keeps existing
    /// <see cref="ICockpitHost"/> implementations compiling untouched; only the app's own host supplies it.
    /// </summary>
    string? CurrentMcpCallerPaneId => null;

    /// <summary>
    /// The cockpit's configured session profiles (#9): what identities exist and where each keeps its
    /// provider state on disk. Read fresh on every call, so a profile added or edited after the plugin
    /// initialised is picked up without a restart. Default returns an empty list so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host reads the real store.
    /// </summary>
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([]);

    /// <summary>
    /// Shows a transient in-app toast in the cockpit (#61) — how a plugin tells the operator that something
    /// happened while they were working elsewhere in the app (a review was requested on a pull request, say).
    /// <paramref name="actionLabel"/> and <paramref name="onAction"/> are supplied together to give the toast a
    /// single button ("Open in browser"); the toast auto-dismisses, so it announces rather than blocks — the
    /// plugin's own surface (its side-menu section) stays the place where the thing itself lives. Default no-op
    /// so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host shows it.
    /// </summary>
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null)
    {
    }

    /// <summary>
    /// Asks the operator to approve a single action before the plugin performs it (#AC-47) — the shared consent
    /// gate for anything a plugin does with the operator's rights on an agent's say-so: a workflow's shell/egress
    /// step, taking over a terminal pane. The host shows an Approve/Deny surface built from <paramref name="request"/>
    /// and returns what the operator chose; the plugin acts only on <see cref="ConsentDecision.IsApproved"/>.
    /// <para>
    /// The gate belongs to the host, never to the plugin — a plugin cannot approve its own action, and the surface
    /// renders <see cref="ConsentRequest.Action"/> verbatim rather than any wording the plugin composes, so a
    /// prompt-injected caller cannot describe a hostile action as a benign one (see <see cref="ConsentRequest"/>).
    /// </para>
    /// Default denies — a host that does not implement consent must fail closed, never silently approve. Only the
    /// app's own host shows the real prompt.
    /// </summary>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => Task.FromResult(ConsentDecision.Denied);

    /// <summary>
    /// Registers a dashboard widget type (see <see cref="WidgetRegistration"/>) — the widget equivalent of
    /// <see cref="AddSessionProvider"/>: it becomes available in a Dashboard workspace's "Add widget" gallery,
    /// and each placed instance is built by the registration's own view factory. The core hosts the grid and the
    /// pane chrome; what a widget shows is the plugin's business. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host renders it.
    /// </summary>
    void AddWidget(WidgetRegistration registration)
    {
    }

    /// <summary>
    /// The widget types every plugin has contributed — what a Dashboard workspace's "Add widget" gallery reads.
    /// A plugin that is not building that gallery has no reason to call this. Default empty.
    /// </summary>
    IReadOnlyList<WidgetRegistration> Widgets => [];

    /// <summary>
    /// Registers a full-surface workspace type (see <see cref="WorkspaceTypeRegistration"/>) — the plugin owns
    /// the whole workspace body, where <see cref="AddWidget"/> owns only one grid cell. It becomes an entry in the
    /// tab strip's "+" menu, and choosing it creates a workspace of that type whose body the registration's own
    /// factory builds. The host draws the tab and the frame; what the body shows, and any session it embeds
    /// (<see cref="IWorkspaceContext.EmbedSession"/>), is the plugin's business. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host renders it.
    /// </summary>
    void AddWorkspaceType(WorkspaceTypeRegistration registration)
    {
    }

    /// <summary>
    /// The workspace types every plugin has contributed — what the tab strip's "+" menu reads. A plugin that is
    /// not building that menu has no reason to call this. Default empty.
    /// </summary>
    IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes => [];

    /// <summary>
    /// Brings the workspace of type <paramref name="workspaceTypeId"/> — one the plugin registered with
    /// <see cref="AddWorkspaceType"/> — to the front, opening one when none is present, and makes it the active
    /// workspace. The programmatic half of the operator picking that type from the "+" menu: a plugin that has just
    /// received an intent (say "Start in Autopilot", AC-150) uses it to surface its own workspace so the operator
    /// lands on the run instead of having to open it by hand. An existing workspace of the type is activated in
    /// place rather than duplicated. What the body then shows is the plugin's business; this only puts it on screen.
    /// Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep
    /// compiling untouched — only the app's own host opens a workspace.
    /// </summary>
    Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

    /// <summary>
    /// Registers a tracker a plugin can post back to (AC-154) — the writing half of an issue tracker (YouTrack, GitHub
    /// Issues), so a consumer (Autopilot) can leave evidence and move an issue's stage tracker-neutrally. First
    /// registration for a <see cref="Tracking.ITrackerProvider.TrackerId"/> wins; a later one for the same id is
    /// ignored. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds)
    /// keep compiling untouched — only the app's own host records it.
    /// </summary>
    void AddTrackerProvider(Tracking.ITrackerProvider provider)
    {
    }

    /// <summary>The trackers every plugin has contributed — what a consumer reads to find the one for an issue's tracker id. Default empty.</summary>
    IReadOnlyList<Tracking.ITrackerProvider> TrackerProviders => [];

    /// <summary>
    /// Registers a keyboard shortcut (e.g. YouTrack on <c>Shift+Y</c>): the host binds
    /// <see cref="PluginShortcut.DefaultGesture"/> and runs <see cref="PluginShortcut.OnInvoke"/> when it is
    /// pressed, shown alongside the built-in shortcuts in Options. Only fires when the operator is not typing
    /// into a text field or the terminal. Default no-op so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host wires it up.
    /// </summary>
    void AddShortcut(PluginShortcut shortcut)
    {
    }

    /// <summary>
    /// Registers a managed-CLI install recipe (#AC-20): the host can then download the provider's CLI into its own
    /// location (<c>&lt;StateRoot&gt;/cli/&lt;name&gt;/&lt;version&gt;/</c>), verify it, keep it up to date, and hand
    /// its path back through <see cref="ResolveManagedCliPath"/> — so a profile need not rely on the CLI being on
    /// PATH. The <paramref name="descriptor"/> is the only place provider-specific download knowledge lives; the
    /// installer itself is generic. A convenience, never a dependency: a pinned absolute path still wins, and a
    /// machine with no managed copy (offline, or the operator removed it) falls back to PATH untouched. Default
    /// no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host installs anything.
    /// </summary>
    void AddManagedCli(ManagedCliDescriptor descriptor)
    {
    }

    /// <summary>
    /// The path to the newest managed copy of <paramref name="cliName"/> the host has installed, or
    /// <see langword="null"/> when none is installed (#AC-20) — what a provider's executable resolver consults
    /// <em>after</em> a pinned absolute path but <em>before</em> PATH, so a managed install is preferred yet a
    /// download failure or a removed copy simply leaves it null and resolution falls through to PATH. Default
    /// <see langword="null"/> so existing <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </summary>
    string? ResolveManagedCliPath(string cliName) => null;

    /// <summary>
    /// Downloads and installs the latest version of a registered managed CLI (#AC-20), returning where it landed or
    /// why it could not — what a config view's "Install / Update" button calls. Never throws: a checksum mismatch,
    /// an offline machine or an unregistered name comes back as an unsuccessful <see cref="ManagedCliInstallResult"/>
    /// the caller can show, because installing a CLI is a convenience that must not crash the app. Default returns a
    /// failure so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host installs anything.
    /// </summary>
    Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ManagedCliInstallResult.Fail("This host does not install managed CLIs."));

    /// <summary>
    /// Removes the cockpit-managed copy of a CLI (#AC-20 "uitzetbaar") — what a config view's "Remove" button calls,
    /// so resolution falls back to a pinned path or PATH. Returns whether anything was removed. Default
    /// <see langword="false"/> so existing <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </summary>
    bool RemoveManagedCli(string cliName) => false;

    /// <summary>
    /// Reports the installed and latest-available versions of a managed CLI (#AC-20), so a config view can offer
    /// "Update to X" only when a newer version actually exists and say "up to date" otherwise, instead of an Update
    /// button that may do nothing. Reaches the provider's channel for the latest version (a lightweight check, no
    /// download); a channel it cannot reach comes back as a null <see cref="ManagedCliStatus.LatestVersion"/> rather
    /// than a thrown error. Default returns both-null so existing <see cref="ICockpitHost"/> implementations keep
    /// compiling untouched — only the app's own host performs the check.
    /// </summary>
    Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManagedCliStatus(null, null));
}
