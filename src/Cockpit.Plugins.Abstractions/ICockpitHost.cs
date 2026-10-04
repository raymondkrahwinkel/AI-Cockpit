using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// What the host offers a plugin during <see cref="ICockpitPlugin.Initialize"/>: the built service provider,
/// cockpit actions, per-plugin storage, and every contribution point.
/// </summary>
/// <remarks>
/// This facade is the contract's only intended growth surface — new capabilities are added here (as default
/// interface methods) rather than by widening the other interfaces.
/// </remarks>
public interface ICockpitHost
{
    IServiceProvider Services { get; }

    ICockpitActions Actions { get; }

    IPluginStorage Storage { get; }

    /// <summary>
    /// Where what the plugin can rebuild goes: fetched data, run history, anything it would refetch rather than
    /// miss (AC-1294). Its own file, outside the settings and outside a backup — see <see cref="IPluginCache"/>
    /// for why it has no <c>SetSecret</c>.
    /// </summary>
    /// <remarks>
    /// A default implementation, so this is an addition to the contract rather than a break of it: a host
    /// implementation that predates it — a plugin's test double, say — keeps compiling and gets a cache that
    /// lives as long as the process does.
    /// </remarks>
    IPluginCache Cache => InMemoryPluginCache.Shared;

    /// <summary>
    /// Registers a source of long-running, agent-started background activities shown in the app status bar
    /// (AC-82), with a Kill button the operator controls — never the agent. The plugin supplies the list and the
    /// stop callback.
    /// </summary>
    /// <remarks>
    /// Default no-op so existing implementations keep compiling untouched — only the app's own host renders it.
    /// </remarks>
    void AddSupervisedActivityProvider(ISupervisedActivitySource source)
    {
    }

    /// <summary>
    /// Contributes a step to the workflow editor (#69) — "Move a ticket to In Progress", "Comment on a pull
    /// request". The step appears in the picker under its own category and runs like any other.
    /// </summary>
    /// <remarks>
    /// Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
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

    /// <summary>
    /// The templates every plugin has contributed — what the workflows plugin reads to build its "New flow" picker. Default empty.
    /// </summary>
    IReadOnlyList<WorkflowTemplate> WorkflowTemplates => [];

    /// <summary>
    /// Fires a trigger this plugin contributed (an <see cref="IWorkflowStep"/> whose
    /// <see cref="IWorkflowStep.IsTrigger"/> is true). Every active flow that begins with that trigger runs,
    /// starting with <paramref name="data"/>.
    /// </summary>
    /// <remarks>
    /// Fire it when the thing actually happened, not when it might have.
    /// </remarks>
    void RaiseWorkflowTrigger(string typeId, IReadOnlyDictionary<string, string> data)
    {
    }

    /// <summary>
    /// Raised when any plugin fires a trigger — what the workflows plugin listens to. No other plugin has a reason to.
    /// </summary>
    event EventHandler<WorkflowTriggerFired>? WorkflowTriggerRaised
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Registers a handler for an intent other plugins can send to this one (AC-95), under
    /// <paramref name="action"/> — the receiving half of <see cref="SendIntent"/>. Registering the same action
    /// twice from one plugin throws.
    /// </summary>
    /// <remarks>
    /// The host stamps the calling plugin's id on every intent it delivers, so <paramref name="handler"/> can
    /// trust <see cref="PluginIntent.CallerPluginId"/>. Default no-op so existing implementations keep compiling
    /// untouched.
    /// </remarks>
    void RegisterIntentHandler(string action, Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> handler)
    {
    }

    /// <summary>
    /// Sends an intent to the plugin with id <paramref name="targetPluginId"/> and returns its handler's result,
    /// or <see langword="null"/> when that plugin is not installed or registered no handler for
    /// <paramref name="action"/> (AC-95).
    /// </summary>
    /// <remarks>
    /// Addressing is by manifest id and an agreed action string, so the caller need not reference the target's
    /// types. The host stamps this plugin's own id as <see cref="PluginIntent.CallerPluginId"/>; a plugin cannot
    /// send under another's name. Default returns <see langword="null"/> so existing implementations keep
    /// compiling untouched.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    /// <summary>
    /// Whether the plugin with id <paramref name="targetPluginId"/> has registered a handler for
    /// <paramref name="action"/> (AC-95) — what a plugin checks before offering a menu item that would otherwise
    /// dispatch to nobody.
    /// </summary>
    /// <remarks>
    /// The id and action are matched case-sensitively (see <see cref="PluginIntent"/>). Check it when the
    /// operator is about to act rather than from <see cref="ICockpitPlugin.Initialize"/>: a target that loads
    /// after you has not registered yet when yours runs. Default <see langword="false"/> so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    bool CanSendIntent(string targetPluginId, string action) => false;

    /// <summary>
    /// This plugin's own channel to its UI part (AC-1389): register the actions the UI may invoke, and publish
    /// the events it subscribes to. The only route between the two halves of a plugin — they share no objects.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SendIntent"/> it never reaches another plugin. Default is a channel that keeps nothing,
    /// so existing implementations keep compiling untouched.
    /// </remarks>
    IPluginBackendChannel Channel => NullPluginBackendChannel.Instance;

    /// <summary>
    /// The plugins the host has loaded, each with its manifest id and human-readable name (AC-189) — used to turn
    /// another plugin's id into a name to show, instead of the bare id.
    /// </summary>
    /// <remarks>
    /// <see cref="PluginMetadata.Id"/> is the same host-stamped id carried by
    /// <see cref="RegisteredAutopilotTemplate.OwnerPluginId"/> and <see cref="PluginIntent.CallerPluginId"/>.
    /// Default empty so existing implementations keep compiling untouched.
    /// </remarks>
    IReadOnlyList<PluginMetadata> InstalledPlugins => [];

    /// <summary>
    /// Registers an Autopilot goal/brief template this plugin contributes (AC-189) — the template equivalent of
    /// <see cref="AddWorkflowTemplate"/>, for the list an operator picks a run's brief from.
    /// </summary>
    /// <remarks>
    /// Registrations live only in memory — call this from <see cref="ICockpitPlugin.Initialize"/> on every start.
    /// Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void RegisterAutopilotTemplate(PluginAutopilotTemplate template)
    {
    }

    /// <summary>
    /// The Autopilot templates every plugin has contributed — what the Autopilot plugin reads to build its template picker. Default empty.
    /// </summary>
    IReadOnlyList<RegisteredAutopilotTemplate> RegisteredAutopilotTemplates => [];

    /// <summary>
    /// Registers a new session provider (#45) — the plugin equivalent of the built-in Claude-CLI/Ollama/LM-Studio
    /// providers — backed by the plugin's own <see cref="IPluginSessionDriver"/> and config view.
    /// </summary>
    /// <remarks>
    /// Default no-op so existing implementations keep compiling untouched — only the app's own host overrides it.
    /// </remarks>
    void AddSessionProvider(SessionProviderRegistration registration)
    {
    }

    /// <summary>
    /// Registers the provider's CLI as one that can run as the real interactive TUI in a pane — the plugin
    /// equivalent of the built-in <c>claude</c> TTY mode.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddSessionProvider"/>, since a provider offers what it can — a local model has
    /// no TUI, a TUI-only agent has no headless driver. Default no-op so existing implementations keep compiling
    /// untouched.
    /// </remarks>
    void AddTtyProvider(TtyProviderRegistration registration)
    {
    }

    /// <summary>
    /// Registers (or updates) an HTTP MCP server in the shared registry (#60), so both session worlds can use
    /// its tools without the operator adding it by hand. Idempotent upsert-by-name.
    /// </summary>
    /// <remarks>
    /// Never force-changes an entry's enabled state or scope — a server the operator disabled, rescoped, or
    /// deleted stays that way. Returns a <see cref="Task"/> because it persists to disk; call it fire-and-forget
    /// from a synchronous callback such as <see cref="ICockpitPlugin.Initialize"/>. Default no-op so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    Task AddMcpServer(McpServerContribution contribution) => Task.CompletedTask;

    /// <summary>
    /// Removes an MCP server from the shared registry by name (#60, AC-11), if it is there — what a plugin that
    /// now owns its servers through <see cref="Mcp.IPluginMcpProvider"/> uses to reclaim entries an earlier
    /// version pushed there.
    /// </summary>
    /// <remarks>
    /// A no-op when no entry of that name exists. Returns a <see cref="Task"/> because it persists to disk; call
    /// it fire-and-forget, same as <see cref="AddMcpServer"/>.
    /// </remarks>
    Task RemoveMcpServer(string name) => Task.CompletedTask;

    /// <summary>
    /// Where the cockpit's OAuth standing is for the MCP server this plugin contributed under <paramref name="name"/>
    /// via <see cref="AddMcpServer"/> (AC-243/AC-355) — no network, no browser, just what is stored.
    /// </summary>
    /// <remarks>
    /// <see cref="PluginMcpAuthState.Unknown"/> covers a name with no OAuth server registered as well as a host
    /// that predates this member. Default <see cref="PluginMcpAuthState.Unknown"/> so existing implementations
    /// keep compiling untouched.
    /// </remarks>
    Task<Mcp.PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpAuthState.Unknown);

    /// <summary>
    /// The operator's own "sign in" act (AC-243/AC-355) for the MCP server this plugin contributed under
    /// <paramref name="name"/> via <see cref="AddMcpServer"/> — opens a browser if needed and reports a named
    /// outcome.
    /// </summary>
    /// <remarks>
    /// Never returns a bearer token (Iron Law #8), only whether asking for one worked. Default
    /// <see cref="Mcp.PluginMcpSignInOutcome.Unavailable"/> so existing implementations keep compiling untouched.
    /// </remarks>
    Task<Mcp.PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpSignInOutcome.Unavailable);

    /// <summary>
    /// Calls <paramref name="toolName"/> on the MCP server this plugin contributed under <paramref name="name"/>
    /// via <see cref="AddMcpServer"/> — on the app's own behalf, not a session's (AC-502), before any session
    /// exists.
    /// </summary>
    /// <remarks>
    /// Refuses any <paramref name="name"/> this plugin did not itself register. Never opens a browser — a server
    /// with no usable token answers <see cref="Mcp.PluginMcpToolCallOutcome.AuthorizationRequired"/>; offer
    /// <see cref="SignInMcpServerAsync"/> instead. Never returns a bearer token (Iron Law #8).
    /// <paramref name="projectId"/> (AC-218) scopes to a server contributed only for one project;
    /// <see langword="null"/> reaches only servers pushed into the shared registry via <see cref="AddMcpServer"/>.
    /// Default <see cref="Mcp.PluginMcpToolCallResult.Unavailable"/> so existing implementations keep compiling
    /// untouched.
    /// </remarks>
    Task<Mcp.PluginMcpToolCallResult> CallMcpToolAsync(
        string name,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpToolCallResult.Unavailable);

    /// <summary>
    /// Makes one tool call against an MCP server this plugin contributed under <paramref name="serverName"/> via
    /// <see cref="AddMcpServer"/>, outside any running session (AC-503) — e.g. to confirm a value the operator
    /// just typed actually resolves to something.
    /// </summary>
    /// <remarks>
    /// A server that is not signed in reports <see cref="Mcp.McpProbeOutcome.NotSignedIn"/> without attempting a
    /// connection; this never opens a browser. A timeout, a network failure, or any other exception answers
    /// <see cref="Mcp.McpProbeOutcome.Failed"/> — never <see cref="Mcp.McpProbeOutcome.NotFound"/>, which is
    /// reported only when the tool itself ran and said so. Iron Law #8:
    /// <see cref="Mcp.McpProbeResult.Detail"/> never carries the bearer token or any other credential, only the
    /// tool's own response text on <see cref="Mcp.McpProbeOutcome.Success"/>. Default
    /// <see cref="Mcp.McpProbeOutcome.Failed"/> (no detail) so existing implementations keep compiling untouched.
    /// </remarks>
    /// <param name="serverName">
    /// The name this plugin registered the server under via <see cref="AddMcpServer"/> (<see cref="Mcp.McpServerContribution.Name"/>).
    /// </param>
    /// <param name="toolName">
    /// The MCP tool to call.
    /// </param>
    /// <param name="arguments">
    /// The tool's arguments, or null for none.
    /// </param>
    Task<Mcp.McpProbeResult> ProbeMcpToolAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.McpProbeResult.Failed);

    /// <summary>
    /// Sets the short free-text statusline shown under a session's title, in its header and the sidebar (#AC-13),
    /// or clears it with an empty string. The session is named by its <c>IPluginSessionContext.PaneId</c>.
    /// </summary>
    /// <remarks>
    /// A paneId that matches no live session is a no-op. Marshals to the UI thread itself. Default no-op so
    /// existing implementations keep compiling untouched.
    /// </remarks>
    Task SetSessionStatusline(string paneId, string statusline) => Task.CompletedTask;

    /// <summary>
    /// Sets or clears the statusline of the session named by <paramref name="paneId"/> like
    /// <see cref="SetSessionStatusline"/> does, and answers whether a live session took it (AC-1419).
    /// </summary>
    /// <remarks>
    /// Answers false when no live session has that paneId or it closed before the act. Default answers false, the
    /// honest answer of a host that predates this member.
    /// </remarks>
    Task<bool> SetSessionStatuslineAsync(string paneId, string statusline) => Task.FromResult(false);

    /// <summary>
    /// Renames a session — the title shown in its header and the sidebar — named by its
    /// <c>IPluginSessionContext.PaneId</c> (#AC-13).
    /// </summary>
    /// <remarks>
    /// A blank name is ignored; a paneId that matches no live session is a no-op. Marshals to the UI thread
    /// itself. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    Task SetSessionName(string paneId, string name) => Task.CompletedTask;

    /// <summary>
    /// Renames the session named by <paramref name="paneId"/> like <see cref="SetSessionName"/> does, and answers
    /// whether a live session took the name (AC-1419).
    /// </summary>
    /// <remarks>
    /// Answers false for a blank name, when no live session has that paneId, or when it closed before the act.
    /// Default answers false, the honest answer of a host that predates this member.
    /// </remarks>
    Task<bool> SetSessionNameAsync(string paneId, string name) => Task.FromResult(false);

    /// <summary>
    /// Names a session the way <see cref="SetSessionName"/> does, but leaves alone a session whose name somebody
    /// chose already — in the New-session dialog or by renaming it in the sidebar (#AC-310). Use
    /// <see cref="SetSessionName"/> when the caller means it regardless.
    /// </summary>
    /// <remarks>
    /// Blank names, unknown pane ids and already-named sessions are all no-ops, never errors. Marshals to the UI
    /// thread itself. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    Task SuggestSessionName(string paneId, string name) => Task.CompletedTask;

    /// <summary>
    /// Sends <paramref name="text"/> to the session named by <paramref name="paneId"/> as a submitted turn — the
    /// seam a plugin uses to hand a started session a prompt without a human turn.
    /// </summary>
    /// <remarks>
    /// A paneId that matches no live session is a no-op, never an error. Marshals to the UI thread itself.
    /// Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

    /// <summary>
    /// Places <paramref name="text"/> in the input of the session named by <paramref name="paneId"/> without sending
    /// it, so the operator can still edit it (AC-1399) — what <see cref="ICockpitActions.InjectIntoActiveSessionAsync"/>
    /// did for the selected session, for a named one.
    /// </summary>
    /// <remarks>
    /// Answers false when no live session has that paneId, or when it has no input to place text in (a headless
    /// session): the caller decides whether that is an error. Marshals to the UI thread itself. Default answers
    /// false, the honest answer of a host that predates this member.
    /// </remarks>
    Task<bool> InsertIntoSessionAsync(string paneId, string text) => Task.FromResult(false);

    /// <summary>
    /// Puts a message in the cockpit-assistant's inbox (AC-1338) — the same channel a session's <c>notify</c> to
    /// <c>cockpit-assistant</c> uses — so a plugin can hand the assistant evidence to judge without a toast or a desk.
    /// </summary>
    /// <remarks>
    /// The assistant reads it as data from a stated origin (this plugin), never as an operator instruction. Never
    /// throws; false on a host with no assistant inbox or when that inbox is full, and the default body returns false.
    /// </remarks>
    Task<bool> NotifyAssistantAsync(string kind, string body) => Task.FromResult(false);

    /// <summary>
    /// Opens a chat channel — a Discord or Slack bot — as a second door onto the assistant's own conversation
    /// (AC-1023). Null on a host with no assistant; dispose the gateway to close the channel.
    /// </summary>
    IAssistantChannelGateway? OpenAssistantChannel(AssistantChannelContribution contribution) => null;

    /// <summary>
    /// Binds a plugin surface — a diagram or whiteboard window (AC-832) — to the session already running behind
    /// <paramref name="paneId"/>. The binding starts nothing and ends nothing.
    /// </summary>
    /// <remarks>
    /// Never null: a pane id no session is running behind comes back as a <see cref="DetachedSessionBinding"/>,
    /// the same not-<see cref="IPluginSessionBinding.IsLive"/> state reached when its session ends. A binding
    /// holds no view, so any number of surfaces may bind to one session. Default returns the detached binding.
    /// </remarks>
    IPluginSessionBinding BindToSession(string paneId) => new DetachedSessionBinding(paneId);

    /// <summary>
    /// Creates one git worktree for a multi-session run (AC-174) and returns its path and branch, or null when
    /// <paramref name="repositoryDirectory"/> is not a git repository or the host has no worktree manager.
    /// </summary>
    /// <remarks>
    /// The worktree persists after the run and is managed from the Worktrees panel like any other. Default null
    /// so existing implementations keep compiling untouched.
    /// </remarks>
    Task<Workspaces.PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Workspaces.PluginWorktreeInfo?>(null);

    /// <summary>
    /// Same as <see cref="CreateRunWorktreeAsync(string, string?, CancellationToken)"/>, but forks the worktree from
    /// <paramref name="baseRef"/>'s remote tip (AC-1337) instead of the checkout's own branch.
    /// </summary>
    /// <remarks>
    /// A separate overload rather than a third parameter, for binary compatibility (AC-40): calling it raises the
    /// manifest's minHostVersion. Null behaves exactly like the two-argument overload. Default forwards to it, so
    /// an older host keeps compiling.
    /// </remarks>
    Task<Workspaces.PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label, string? baseRef, CancellationToken cancellationToken = default) =>
        CreateRunWorktreeAsync(repositoryDirectory, label, cancellationToken);

    /// <summary>
    /// Starts a host-owned session embedded in the workspace <paramref name="workspaceId"/>, exactly as that
    /// workspace's <see cref="Workspaces.IWorkspaceContext.EmbedSession"/> would (AC-1398): the host ties it to that
    /// workspace and ends it when the workspace closes. Null when this host cannot embed sessions.
    /// </summary>
    /// <remarks>
    /// How a backend part drives sessions a workspace shows: the UI part hands over
    /// <see cref="Workspaces.IWorkspaceContext.WorkspaceId"/>, the backend holds the returned session by its
    /// <see cref="Workspaces.IEmbeddedSession.PaneId"/>, and the UI part places its view with
    /// <c>ICockpitUiHost.CreateEmbeddedSessionView</c>. From host 0.86.0 any thread will do: the host marshals to its UI
    /// thread itself, as it does for the returned session's members; an older host needs the UI thread. Default null,
    /// so existing hosts keep compiling.
    /// </remarks>
    Workspaces.IEmbeddedSession? EmbedSession(string workspaceId, Workspaces.EmbeddedSessionRequest request) => null;

    /// <summary>
    /// Reports whether <paramref name="directory"/> is a git repository (AC-174), so a plugin can decide up front
    /// whether work there can be isolated in a worktree.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="Workspaces.GitDirectoryStatus.Unknown"/>, not a bool, so the decision stays
    /// fail-closed: only a positive <see cref="Workspaces.GitDirectoryStatus.NotARepository"/> licenses running
    /// without isolation.
    /// </remarks>
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
    /// Adds an in-process MCP server to the cockpit (#AC-12): the host mounts <paramref name="tools"/> — an
    /// already-built class whose <c>[McpServerTool]</c> methods are the tools — on a loopback address under
    /// <paramref name="serverName"/>. Idempotent per name.
    /// </summary>
    /// <remarks>
    /// The endpoint is not written to the operator's MCP-servers registry (AC-40). <paramref name="isEnabled"/>
    /// is read each time servers are gathered, so a toggle takes effect live; <see langword="null"/> means always
    /// on. <paramref name="isInternal"/> marks the endpoint internal-only (AC-204): hidden from user-facing MCP
    /// selection, but still mountable when a launch names it explicitly. Call it fire-and-forget from
    /// <see cref="ICockpitPlugin.Initialize"/>. This overload is kept binary-separate from the three-argument one
    /// below so pre-AC-204 plugin binaries keep resolving their call against a real method. Default no-op.
    /// </remarks>
    Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled, bool isInternal) => Task.CompletedTask;

    /// <summary>
    /// Three-argument <see cref="AddMcpEndpoint(string, object, Func{bool}?, bool)"/> — the original signature,
    /// preserved verbatim for binary compatibility with plugins compiled before the <c>isInternal</c> flag
    /// existed. Registers a non-internal endpoint (visible to user-facing MCP selection).
    /// </summary>
    Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled = null) =>
        AddMcpEndpoint(serverName, tools, isEnabled, isInternal: false);

    /// <summary>
    /// The read/observe surface over the cockpit's sessions: the active session's working directory and a stream
    /// of session output, so a plugin can react to what a session is doing rather than only writing into it.
    /// </summary>
    /// <remarks>
    /// Default returns <see cref="NullCockpitSessionObserver.Instance"/> so existing implementations keep
    /// compiling untouched — only the app's own host supplies a live one.
    /// </remarks>
    ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

    /// <summary>
    /// The pane id of the session behind the in-process MCP call currently being handled — the transport-verified
    /// caller (AC-128), so a plugin's own MCP tool acts on the calling session rather than an agent-supplied id.
    /// </summary>
    /// <remarks>
    /// Null outside an MCP call, and on an older host that predates this — use the agent-supplied id only as a
    /// fallback when this is null.
    /// </remarks>
    string? CurrentMcpCallerPaneId => null;

    /// <summary>
    /// The cockpit's configured session profiles (#9): what identities exist and where each keeps its provider
    /// state on disk. Read fresh on every call.
    /// </summary>
    /// <remarks>
    /// Default returns an empty list so existing implementations keep compiling untouched.
    /// </remarks>
    Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([]);

    /// <summary>
    /// Shows a transient in-app toast in the cockpit (#61) — how a plugin tells the operator that something
    /// happened while they were working elsewhere in the app.
    /// </summary>
    /// <remarks>
    /// <paramref name="actionLabel"/> and <paramref name="onAction"/> are supplied together to give the toast a
    /// single button; it auto-dismisses, so it announces rather than blocks. Default no-op so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null)
    {
    }

    /// <summary>
    /// Asks the operator to approve a single action before the plugin performs it (#AC-47) — the shared consent
    /// gate for anything a plugin does with the operator's rights on an agent's say-so.
    /// </summary>
    /// <remarks>
    /// The gate belongs to the host, never to the plugin: the surface renders
    /// <see cref="ConsentRequest.Action"/> verbatim rather than any wording the plugin composes, so a
    /// prompt-injected caller cannot describe a hostile action as a benign one. Default denies — fail closed.
    /// </remarks>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) => Task.FromResult(ConsentDecision.Denied);

    /// <summary>
    /// <see cref="RequestConsentAsync(ConsentRequest)"/> with a deadline: when <paramref name="cancellationToken"/> is
    /// cancelled before anyone answers, the prompt is taken down everywhere it shows and the answer is
    /// <see cref="ConsentOutcome.Denied"/> — never an approval.
    /// </summary>
    /// <remarks>
    /// Default denies, the same fail-closed answer as the overload without a deadline. A plugin that relies on the
    /// prompt closing sets minHostVersion 0.38.0.
    /// </remarks>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ConsentDecision.Denied);

    /// <summary>
    /// Adds a field to the project editor (AC-317) — "which YouTrack project is this project" — so a project
    /// carries an identifier this plugin resolves, picked from a list the plugin supplies.
    /// </summary>
    /// <remarks>
    /// The value is the host's to store, on the project itself; read it back with
    /// <see cref="GetProjectFieldValueAsync"/>. A key another plugin already registered is kept as it was and
    /// this registration ignored. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void AddProjectField(Projects.ProjectFieldRegistration registration)
    {
    }

    /// <summary>
    /// The project fields every plugin has contributed — what the project editor reads to draw them. Default empty.
    /// </summary>
    IReadOnlyList<Projects.ProjectFieldRegistration> ProjectFields => [];

    /// <summary>
    /// Claims some or all of a project's own host fields — Name, Description, Logo, Behaviour, the MCP overlay,
    /// the worktree switch — as externally managed (AC-604, route B), so the editor can draw a Shared/This-machine
    /// badge instead of drawing every project the same way.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="AddProjectField"/>, this does not add a field — it claims ownership of fields that
    /// already exist. The first registration for a given
    /// <see cref="Projects.ProjectOwnershipRegistration.ProjectId"/> wins. An edit made to a claimed field never
    /// reaches <c>cockpit.json</c> — see <see cref="GetProjectFieldOwnership"/>. Default no-op so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    void ClaimProjectOwnership(Projects.ProjectOwnershipRegistration registration)
    {
    }

    /// <summary>
    /// The resolved ownership of <paramref name="projectId"/>'s own host fields (AC-604) — every
    /// <see cref="Projects.HostProjectField"/> the project editor knows about, each either claimed or left local.
    /// </summary>
    /// <remarks>
    /// Null when nothing ever claimed this project: the editor then draws it exactly as it always has. Default
    /// null so existing implementations keep compiling untouched.
    /// </remarks>
    IReadOnlyDictionary<Projects.HostProjectField, Projects.ProjectFieldOwnership?>? GetProjectFieldOwnership(string projectId) => null;

    /// <summary>
    /// Registers a place a project's memory can live other than a folder (AC-165/166) — a Depot project, say — so
    /// the project editor can offer it beside "Folder".
    /// </summary>
    /// <remarks>
    /// A scheme another plugin already registered is kept as it was and this registration ignored (matched
    /// case-insensitively). Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void AddProjectMemorySource(Projects.ProjectMemorySourceRegistration registration)
    {
    }

    /// <summary>
    /// Withdraws the memory source registered under <paramref name="scheme"/> (AC-501), so it stops being offered
    /// in the project editor's picker.
    /// </summary>
    /// <remarks>
    /// A no-op when nothing is registered under this scheme. Does not touch any project's own stored
    /// <c>MemoryRef</c>. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void RemoveProjectMemorySource(string scheme)
    {
    }

    /// <summary>
    /// The memory sources every plugin has contributed — what the project editor's picker and a session's standing instructions both read. Default empty.
    /// </summary>
    IReadOnlyList<Projects.ProjectMemorySourceRegistration> ProjectMemorySources => [];

    /// <summary>
    /// Declares a group <see cref="Projects.ProjectMemorySourceRegistration.FamilyKey"/> can opt an instance into
    /// (AC-499) — "Depot", say — so the project editor's picker offers one "Depot" entry regardless of how many
    /// connections are configured, rather than one row per connection.
    /// </summary>
    /// <remarks>
    /// A key another plugin already declared is kept as it was and this registration ignored (matched
    /// case-insensitively). Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void AddProjectMemorySourceFamily(Projects.ProjectMemorySourceFamily family)
    {
    }

    /// <summary>
    /// What the operator picked for <paramref name="key"/> on the project a session belongs to (AC-317), or
    /// <see langword="null"/> when that session has no project or the project is not linked. Reading half of
    /// <see cref="AddProjectField"/>.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="paneId"/> means the selected session
    /// (<see cref="ICockpitSessionObserver.ActivePaneId"/>). Default <see langword="null"/> so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    Task<string?> GetProjectFieldValueAsync(string key, string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Every value the operator picked for <paramref name="key"/> on the project a session belongs to (AC-884) —
    /// the plural of <see cref="GetProjectFieldValueAsync"/>, empty under the same conditions that answers null for.
    /// Default empty so existing <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </summary>
    Task<IReadOnlyList<string>> GetProjectFieldValuesAsync(string key, string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// The project's own Memory rows (AC-483/AC-827) — 0, 1 or several, read-only. The missing read half of
    /// <see cref="AddProjectMemorySource"/>/<see cref="ProjectMemorySources"/>, which register where a scheme
    /// resolves rather than what a project stored. Resolved like <see cref="GetProjectFieldValueAsync"/>: null/blank
    /// <paramref name="paneId"/> means the selected session; no linked project answers empty. Default empty.
    /// </summary>
    Task<IReadOnlyList<Projects.ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Projects.ProjectMemoryRow>>([]);

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

    /// <summary>
    /// The trackers every plugin has contributed — what a consumer reads to find the one for an issue's tracker id. Default empty.
    /// </summary>
    IReadOnlyList<Tracking.ITrackerProvider> TrackerProviders => [];

    /// <summary>
    /// Registers a managed-CLI install recipe (#AC-20): the host can then download the provider's CLI, verify it,
    /// keep it up to date, and hand its path back through <see cref="ResolveManagedCliPath"/>.
    /// </summary>
    /// <remarks>
    /// A convenience, never a dependency: a pinned absolute path still wins, and a machine with no managed copy
    /// falls back to PATH untouched. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void AddManagedCli(ManagedCliDescriptor descriptor)
    {
    }

    /// <summary>
    /// The path to the newest managed copy of <paramref name="cliName"/> the host has installed, or
    /// <see langword="null"/> when none is installed (#AC-20).
    /// </summary>
    /// <remarks>
    /// Consulted <em>after</em> a pinned absolute path but <em>before</em> PATH. Default <see langword="null"/>
    /// so existing implementations keep compiling untouched.
    /// </remarks>
    string? ResolveManagedCliPath(string cliName) => null;

    /// <summary>
    /// Downloads and installs the latest version of a registered managed CLI (#AC-20), returning where it landed
    /// or why it could not.
    /// </summary>
    /// <remarks>
    /// Never throws: a checksum mismatch, an offline machine or an unregistered name comes back as an
    /// unsuccessful <see cref="ManagedCliInstallResult"/>. Default returns a failure so existing implementations
    /// keep compiling untouched.
    /// </remarks>
    Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ManagedCliInstallResult.Fail("This host does not install managed CLIs."));

    /// <summary>
    /// Removes the cockpit-managed copy of a CLI (#AC-20), so resolution falls back to a pinned path or PATH.
    /// Returns whether anything was removed.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/> so existing implementations keep compiling untouched.
    /// </remarks>
    bool RemoveManagedCli(string cliName) => false;

    /// <summary>
    /// Reports the installed and latest-available versions of a managed CLI (#AC-20), so a config view can offer
    /// "Update to X" only when a newer version actually exists.
    /// </summary>
    /// <remarks>
    /// A lightweight channel check, no download; a channel it cannot reach comes back as a null
    /// <see cref="ManagedCliStatus.LatestVersion"/> rather than a thrown error. Default returns both-null so
    /// existing implementations keep compiling untouched.
    /// </remarks>
    Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManagedCliStatus(null, null));

    /// <summary>
    /// Whether the background update check installs a newer version of a managed CLI itself, instead of only
    /// toasting that one exists (AC-767).
    /// </summary>
    /// <remarks>
    /// Default <see langword="true"/>: an installation that never touched this setting keeps auto-updating.
    /// </remarks>
    Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// Turns auto-update for a managed CLI on or off (AC-767) — what the checkbox writes. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations keep compiling untouched; only the app's own host persists it.
    /// </summary>
    Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Registers something this plugin gives every session as it starts (AC-165), e.g. environment variables,
    /// asked for per session so the answer can depend on the project it belongs to.
    /// </summary>
    /// <remarks>
    /// The host asks each registered provider once per launch and merges what they return, so a plugin
    /// contributes once and reaches every provider without knowing any of them. Default no-op so existing
    /// implementations keep compiling untouched.
    /// </remarks>
    void AddSessionResourceProvider(Sessions.ISessionResourceProvider provider)
    {
    }

    /// <summary>
    /// The session-resource providers every plugin has contributed — what a starting session is assembled from. Default empty.
    /// </summary>
    IReadOnlyList<Sessions.ISessionResourceProvider> SessionResourceProviders => [];

    /// <summary>
    /// Registers a place a plugin can list projects it shares elsewhere but this machine has not bound yet
    /// (AC-245), so the Projects workspace can offer them beside the local ones under a heading named by
    /// <see cref="Projects.SharedProject.SourceName"/>.
    /// </summary>
    /// <remarks>
    /// A <see cref="Projects.ISharedProjectSource.Key"/> another plugin already registered is kept as it was and
    /// this registration ignored. Default no-op so existing implementations keep compiling untouched.
    /// </remarks>
    void AddSharedProjectSource(Projects.ISharedProjectSource source)
    {
    }

    /// <summary>
    /// Withdraws the shared-project source registered under <paramref name="key"/> (AC-245), so a source that no
    /// longer applies stops being offered.
    /// </summary>
    /// <remarks>
    /// A no-op when nothing is registered under this key. Default no-op so existing implementations keep
    /// compiling untouched.
    /// </remarks>
    void RemoveSharedProjectSource(string key)
    {
    }

    /// <summary>
    /// The shared-project sources every plugin has contributed — what the Projects workspace reads to list them. Default empty.
    /// </summary>
    IReadOnlyList<Projects.ISharedProjectSource> SharedProjectSources => [];

    /// <summary>
    /// Registers a health section (AC-1466) the host reads on every health request, so /healthz sees this plugin's state.
    /// Default no-op so existing implementations keep compiling untouched.
    /// </summary>
    void AddHealthSection(Health.IPluginHealthSection section)
    {
    }

}
