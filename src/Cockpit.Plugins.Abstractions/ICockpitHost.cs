using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
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
/// helper to open a window beside the cockpit. This facade is the contract's only intended growth surface — new
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
    /// Adds a launcher button to the left menu like <see cref="AddSideMenuButton"/>, but carrying a live
    /// counter/badge next to the title (AC-516) — "Open PR's 3" — that the plugin updates through the returned
    /// <see cref="SideMenuButtonBadge"/> without calling this again, and the host renders on
    /// <see cref="SideMenuButtonBadge.Changed"/> without polling. See <see cref="SideMenuButtonBadge"/> for exactly
    /// what null/zero/two-counter values mean and how they render (<see cref="SideMenuButtonBadge.ToDisplayText"/>).
    /// <para>
    /// A new method rather than a parameter on <see cref="AddSideMenuButton"/>: <see cref="ICockpitHost"/> is a
    /// facade a plugin only <em>consumes</em>, never implements, so adding a method here cannot break an existing
    /// plugin — it simply never calls the new one. Widening <see cref="AddSideMenuButton"/>'s own signature instead
    /// would have been a binary break invisible to <see cref="AbstractionsContract.Version"/>, the exact failure
    /// AC-500 found only by loading a mini-plugin built against the old and new abstractions dlls side by side.
    /// </para>
    /// Default returns a badge no one renders, so existing <see cref="ICockpitHost"/> implementations (test fakes,
    /// older plugin builds) keep compiling untouched — only the app's own host attaches the button and follows
    /// the badge's changes.
    /// </summary>
    SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) => new();

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
    /// Adds a session-bound banner shown under the transcript, above the composer (AC-802) — for PR/CI status and
    /// similar per-session info a header item has no room for. Default no-op so existing implementations keep
    /// compiling untouched; the plugin's own view controls whether anything shows.
    /// </summary>
    /// <param name="createView">
    /// Builds the control for one session; invoked once per session panel, on the UI thread.
    /// </param>
    void AddSessionBanner(Func<IPluginSessionContext, Control> createView)
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
    /// The plugins the host has loaded, each with its manifest id and human-readable name (AC-189) — what a plugin uses
    /// to turn another plugin's id (a template's <see cref="RegisteredAutopilotTemplate.OwnerPluginId"/>, an intent
    /// caller's <see cref="PluginIntent.CallerPluginId"/>) into a name to show, instead of the bare id. The
    /// <see cref="PluginMetadata.Id"/> is the same host-stamped id those carry, so a lookup by it is exact. Default empty
    /// so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host reports the real list.
    /// </summary>
    IReadOnlyList<PluginMetadata> InstalledPlugins => [];

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

    /// <summary>Opens a window beside the cockpit hosting <paramref name="createContent"/>; the plugin owns the content control. Not modal: the operator can still reach a running session while it is open, and can open a second one — every call builds its content afresh. Use the <paramref name="singleInstanceKey"/> overload for a window there should only ever be one of.</summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);

    /// <summary>
    /// The same, for a window there should only ever be one of: asked for again while it is open, the cockpit
    /// brings that window forward and builds nothing. Two calls share a window when they pass the same
    /// <paramref name="singleInstanceKey"/> — the host scopes it to your plugin, so it only has to be unique
    /// within your own.
    /// <para>
    /// Key what is genuinely one thing ("issues"), and key per subject what is not: a picker opened from a
    /// session's header belongs to that session, so it wants the pane in its key
    /// (<c>$"track.{session.PaneId}"</c>) or no key at all. The host cannot work this out for you — it is handed
    /// a caption, and two plugins can title different windows the same.
    /// </para>
    /// <para>
    /// A separate overload rather than a fifth optional parameter, because every plugin zip already published
    /// calls the four-argument method and its signature has to stay (the AC-40 binary-compat rule). The default
    /// body forwards to that one, so an older host that has this member opens a second window as it always did.
    /// A plugin calling this raises its manifest's minHostVersion.
    /// </para>
    /// </summary>
    Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
        ShowDialogAsync(title, createContent, width, height);

    /// <summary>
    /// Renders <paramref name="markdown"/> the way the cockpit's own transcript does (AC-296) — the seam that
    /// gives a plugin's own dialog (an issue's description, say) the host's markdown look instead of showing raw
    /// <c>##</c>/<c>**</c> syntax or forcing the plugin to bundle a second parser. A factory rather than a
    /// stateful presenter, deliberately: it mirrors <see cref="ShowDialogAsync"/>'s own <c>Func&lt;Control&gt;</c>
    /// shape, so a caller that wants the rendering to change — the operator picked a different issue — just calls
    /// this again and swaps the result into its own <see cref="ContentControl.Content"/>, the same way it would
    /// swap any other control. There is no update contract to implement and nothing to dispose.
    /// <para>
    /// Default returns the raw text in a wrapping <see cref="SelectableTextBlock"/> — exactly the plain-text
    /// behaviour every plugin had before this seam existed — so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling and rendering unchanged; only the app's
    /// own host renders real markdown.
    /// </para>
    /// </summary>
    Control CreateMarkdownView(string markdown) =>
        new SelectableTextBlock { Text = markdown, TextWrapping = TextWrapping.Wrap };

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
    /// saved from the plugin manager's gear (#52) — i.e. the host committed what
    /// <see cref="IPluginSettingsView.TryStage"/> handed it. A contribution that read settings once at construction and cached the result (e.g. a side-menu
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
    /// Where the cockpit's OAuth standing is for the MCP server this plugin contributed under <paramref name="name"/>
    /// via <see cref="AddMcpServer"/> (AC-243/AC-355) — no network, no browser, just what is stored, the same
    /// restraint the host's own MCP-servers dialog keeps when it draws a status badge for every row in a list.
    /// <see cref="PluginMcpAuthState.Unknown"/> covers a name the host has no OAuth server registered under (never
    /// contributed, contributed as a static-token server, or removed) as well as a host that predates this member —
    /// a plugin cannot tell those apart from the outside, and does not need to: either way there is no standing to
    /// report. Default <see cref="PluginMcpAuthState.Unknown"/> so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host reports
    /// the real standing.
    /// </summary>
    Task<Mcp.PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpAuthState.Unknown);

    /// <summary>
    /// The operator's own "sign in" act (AC-243/AC-355) for the MCP server this plugin contributed under
    /// <paramref name="name"/> via <see cref="AddMcpServer"/> — opens a browser if needed and reports a named
    /// outcome, driving the exact same loopback sign-in flow the host's own MCP-servers dialog uses rather than a
    /// second one the plugin would have to build (AC-500's "the plugin never sees a bearer token" holds here too:
    /// this never returns one, only whether asking for one worked). Default
    /// <see cref="Mcp.PluginMcpSignInOutcome.Unavailable"/> so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host can actually open a
    /// browser.
    /// </summary>
    Task<Mcp.PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpSignInOutcome.Unavailable);

    /// <summary>
    /// Calls <paramref name="toolName"/> on the MCP server this plugin contributed under <paramref name="name"/> via
    /// <see cref="AddMcpServer"/> — on the app's own behalf, not a session's (AC-502). What a plugin uses to ask its
    /// own server a question before any session exists (a project editor's picker asking a Depot connection to list
    /// its projects, say), using the same connect path and OAuth standing a session would, without a session ever
    /// starting. Refuses any <paramref name="name"/> this plugin did not itself register — the merged catalog
    /// <see cref="Mcp.IMcpToolInvoker"/>-backed calls resolve against also holds every other plugin's contributions,
    /// every operator-configured registry server, and every cockpit-internal endpoint, none of which this plugin has
    /// any standing to reach through here. Never opens a browser: an OAuth server with no usable token yet answers
    /// <see cref="Mcp.PluginMcpToolCallOutcome.AuthorizationRequired"/> rather than prompting — offer
    /// <see cref="SignInMcpServerAsync"/> instead. Never returns a bearer token (Iron Law #8), only the tool's own
    /// result. <paramref name="projectId"/> (AC-218) scopes the same way <see cref="AddMcpServer"/>'s own project
    /// overload's callers scope a session's connect — a server contributed only for one project (a plugin's
    /// <c>IPluginMcpProvider.GetMcpServers(projectId)</c>) is invisible without it; <see langword="null"/> reaches
    /// only servers pushed into the shared registry (this plugin's own, via <see cref="AddMcpServer"/>) regardless
    /// of project. Default <see cref="Mcp.PluginMcpToolCallResult.Unavailable"/> so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host can
    /// actually reach a server.
    /// </summary>
    Task<Mcp.PluginMcpToolCallResult> CallMcpToolAsync(
        string name,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.PluginMcpToolCallResult.Unavailable);

    /// <summary>
    /// Makes one tool call against an MCP server this plugin contributed under <paramref name="serverName"/> via
    /// <see cref="AddMcpServer"/>, outside any running session (AC-503) — what a project editor's own reachability
    /// check uses to confirm a value the operator just typed (a Depot project slug, say) actually resolves to
    /// something, without needing a session to ask through.
    /// <para>
    /// Checks sign-in first, the same non-interactive read <see cref="GetMcpServerAuthStateAsync"/> does: a server
    /// that is not signed in reports <see cref="Mcp.McpProbeOutcome.NotSignedIn"/> without attempting a connection at
    /// all. Otherwise this opens a short-lived connection with a budget of a few seconds — nowhere near the
    /// multi-minute allowance an interactive OAuth sign-in gets — calls <paramref name="toolName"/>, and disposes the
    /// connection immediately after; nothing here is held open between calls. This never opens a browser: the same
    /// restraint <see cref="GetMcpServerAuthStateAsync"/> and a session's own non-interactive renewal already take,
    /// so a token that cannot be renewed silently is exactly the <see cref="Mcp.McpProbeOutcome.NotSignedIn"/> case
    /// above, not a prompt this call could ever trigger.
    /// </para>
    /// <para>
    /// A timeout, a network failure, or any other exception answers <see cref="Mcp.McpProbeOutcome.Failed"/> — never
    /// <see cref="Mcp.McpProbeOutcome.NotFound"/>, which this only reports when the tool itself ran and said so in a
    /// way the host can actually recognise. Confusing the two would tell an operator a value does not exist when the
    /// truth is only that nothing could be confirmed either way (AC-503 acceptance criterion 4).
    /// </para>
    /// Iron Law #8: <see cref="Mcp.McpProbeResult.Detail"/> never carries the bearer token or any other credential
    /// this call used to reach the server — only the tool's own response text, and only on
    /// <see cref="Mcp.McpProbeOutcome.Success"/>. Default <see cref="Mcp.McpProbeOutcome.Failed"/> (no detail) so
    /// existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host can actually reach an MCP server.
    /// </summary>
    /// <param name="serverName">The name this plugin registered the server under via <see cref="AddMcpServer"/> (<see cref="Mcp.McpServerContribution.Name"/>).</param>
    /// <param name="toolName">The MCP tool to call.</param>
    /// <param name="arguments">The tool's arguments, or null for none.</param>
    Task<Mcp.McpProbeResult> ProbeMcpToolAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Mcp.McpProbeResult.Failed);

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
    /// Names a session the way <see cref="SetSessionName"/> does, but leaves alone a session whose name somebody
    /// chose — in the New-session dialog or by renaming it in the sidebar (#AC-310). What a plugin calls when it
    /// ties a ticket to a session that is already running: the session should become recognisable as the one working
    /// on that ticket, but not at the cost of the name the operator deliberately gave it. Use
    /// <see cref="SetSessionName"/> when the caller means it regardless. Blank names, unknown pane ids and
    /// already-named sessions are all no-ops, never errors. Marshals to the UI thread itself. Default no-op so
    /// existing <see cref="ICockpitHost"/> implementations keep compiling untouched — on a host that predates this,
    /// the ticket link simply leaves the session's name as it found it.
    /// </summary>
    Task SuggestSessionName(string paneId, string name) => Task.CompletedTask;

    /// <summary>
    /// Sends <paramref name="text"/> to the session named by <paramref name="paneId"/> as a submitted turn — the seam
    /// a plugin uses to hand a started session (including one it embedded in its own workspace) a prompt without a
    /// human turn, e.g. an Autopilot run's work brief once the operator has approved the run. A paneId that matches no
    /// live session is a no-op, never an error. Marshals to the UI thread itself. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched.
    /// </summary>
    Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

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
    /// Never null: a pane id no session is running behind comes back as a <see cref="DetachedSessionBinding"/>, the
    /// same not-<see cref="IPluginSessionBinding.IsLive"/> state a binding reaches when its session ends.
    /// Deliberately not <c>IWorkspaceContext.EmbedSession</c>, which mints a fresh session panel: pointing one at a
    /// pane the grid already draws builds a rival view over the same pty. A binding holds no view, so any number of
    /// surfaces may bind to one session. Default returns the detached binding, so existing
    /// <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </remarks>
    IPluginSessionBinding BindToSession(string paneId) => new DetachedSessionBinding(paneId);

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
    /// toggle takes effect live; <see langword="null"/> means always on. <paramref name="isInternal"/> marks the
    /// endpoint internal-only (AC-204): hidden from every user-facing MCP selection (the New-session checklist, the
    /// profile preselection and its token estimate) and from the no-selection fan-out, yet still mountable when a
    /// launch names it explicitly in its per-session selection — for an endpoint only a specific spawn should mount
    /// (say the Autopilot CEO/step tools its own run agents scope to by name), never an ordinary operator's to tick.
    /// Call it fire-and-forget from <see cref="ICockpitPlugin.Initialize"/>. Default no-op so existing host
    /// implementations keep compiling.
    /// </summary>
    /// <remarks>
    /// This <c>isInternal</c> overload is kept binary-separate from the three-argument one below: adding an
    /// optional parameter to the original signature would have been source-compatible but binary-breaking —
    /// a plugin compiled against the old three-argument method throws <see cref="MissingMethodException"/> at
    /// load time on a host that only exposes the four-argument shape. Keeping both signatures lets pre-AC-204
    /// plugin binaries (workflows, kubernetes, …) resolve their three-argument call against a real method.
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
    /// Registers a panel for the right-hand dock rail (see <see cref="DockPanelRegistration"/>) — the rail's tab
    /// strip lists it, and opening it builds the registration's own view. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling untouched —
    /// only the app's own host renders it.
    /// </summary>
    void AddDockPanel(DockPanelRegistration registration)
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
    /// Adds a field to the project editor (AC-317) — "which YouTrack project is this project", "which repository" —
    /// so a project carries the identifier this plugin resolves, picked from a list the plugin supplies rather than
    /// typed into a free-text box where a misspelling silently finds nothing.
    /// <para>
    /// The value is the host's to store, on the project itself: three plugins ask the same question about the same
    /// project, and a project that names both a tracker and a repository is the ordinary case. Read it back with
    /// <see cref="GetProjectFieldValueAsync"/>. A key another plugin already registered is kept as it was and this
    /// registration ignored — see <see cref="ProjectFieldRegistration.Key"/> for why that is agreement and not a clash.
    /// </para>
    /// Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep
    /// compiling untouched — only the app's own host draws the field.
    /// </summary>
    void AddProjectField(Projects.ProjectFieldRegistration registration)
    {
    }

    /// <summary>The project fields every plugin has contributed — what the project editor reads to draw them. Default empty.</summary>
    IReadOnlyList<Projects.ProjectFieldRegistration> ProjectFields => [];

    /// <summary>
    /// Claims some or all of a project's own host fields — Name, Description, Logo, Behaviour, the MCP overlay,
    /// the worktree switch — as externally managed (AC-604, route B): a project shared through this plugin
    /// (AC-242's Depot sync, say) tells the editor which of those fields come from there instead of
    /// <c>cockpit.json</c>, so it can draw the ◆ Shared / ● This machine badge instead of drawing every project
    /// the same way. Unlike <see cref="AddProjectField"/>, this does not add a field — it claims ownership of
    /// fields that already exist.
    /// <para>
    /// The first registration for a given <see cref="Projects.ProjectOwnershipRegistration.ProjectId"/> wins,
    /// the same agreement <see cref="AddProjectField"/> already makes for a key two plugins both register (see
    /// <see cref="Projects.ProjectFieldRegistration.Key"/>) — a second plugin claiming a project another already
    /// claimed is ignored, not refused with an error.
    /// </para>
    /// An edit made to a claimed field here never reaches <c>cockpit.json</c> — see
    /// <see cref="GetProjectFieldOwnership"/>, which is what the editor's save path reads to know which fields
    /// that applies to. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older
    /// plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void ClaimProjectOwnership(Projects.ProjectOwnershipRegistration registration)
    {
    }

    /// <summary>
    /// The resolved ownership of <paramref name="projectId"/>'s own host fields (AC-604) — every
    /// <see cref="Projects.HostProjectField"/> the project editor knows about, each either claimed (with who owns
    /// it and whether it is editable here) or left local. Null when nothing ever claimed this project: the editor
    /// then draws it exactly as it always has — no badge, no locked field (acceptance criterion 4). Default null
    /// so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep compiling
    /// untouched — only the app's own host resolves a real claim.
    /// </summary>
    IReadOnlyDictionary<Projects.HostProjectField, Projects.ProjectFieldOwnership?>? GetProjectFieldOwnership(string projectId) => null;

    /// <summary>
    /// Registers a place a project's memory can live other than a folder (AC-165/166) — a Depot project, say — so a
    /// project's editor can offer it beside "Folder" and a session started on a project pointing at this scheme is
    /// told, in its own standing instructions, how to reach it rather than only where it is.
    /// <para>
    /// A scheme another plugin already registered is kept as it was and this registration ignored (matched
    /// case-insensitively — a project's stored reference is read the same way), the same agreement
    /// <see cref="AddProjectField"/> makes for a key two plugins both offer.
    /// </para>
    /// This is additive to the AC-40 contract: <see cref="AbstractionsContract.Version"/> stays 1, the same as every
    /// other default-implemented member added here. Default no-op so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void AddProjectMemorySource(Projects.ProjectMemorySourceRegistration registration)
    {
    }

    /// <summary>
    /// Withdraws the memory source registered under <paramref name="scheme"/> (AC-501) — a plugin that can offer
    /// more than one memory source (a Depot connection the operator later removes, say) uses this so a scheme that
    /// no longer resolves to anything stops being offered in the project editor's picker, instead of lingering there
    /// until the app restarts. A no-op when nothing is registered under this scheme. Does not touch any project's
    /// own stored <c>MemoryRef</c> — see <see cref="AddProjectMemorySource"/>'s remark on why removing a source
    /// never rewrites a project. Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes,
    /// older plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void RemoveProjectMemorySource(string scheme)
    {
    }

    /// <summary>The memory sources every plugin has contributed — what the project editor's picker and a session's standing instructions both read. Default empty.</summary>
    IReadOnlyList<Projects.ProjectMemorySourceRegistration> ProjectMemorySources => [];

    /// <summary>
    /// Declares a group <see cref="Projects.ProjectMemorySourceRegistration.FamilyKey"/> can opt an instance into
    /// (AC-499) — "Depot", say, so the project editor's picker offers one "Depot" entry regardless of how many
    /// connections are configured, rather than one row per connection. Registering a family the instant no instance
    /// exists yet is the point, not an edge case: it is what lets the picker say "Depot" (and offer a way to
    /// configure one) instead of staying silent about a plugin that has nothing registered right now.
    /// <para>
    /// A key another plugin already declared is kept as it was and this registration ignored (matched
    /// case-insensitively), the same agreement <see cref="AddProjectMemorySource"/> makes for a scheme two plugins
    /// both offer.
    /// </para>
    /// This is additive to the AC-40 contract: <see cref="AbstractionsContract.Version"/> stays 1, the same as every
    /// other default-implemented member added here. Default no-op so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void AddProjectMemorySourceFamily(Projects.ProjectMemorySourceFamily family)
    {
    }

    /// <summary>
    /// What the operator picked for <paramref name="key"/> on the project a session belongs to (AC-317), or
    /// <see langword="null"/> when that session has no project, the project is not linked, or nothing matches
    /// <paramref name="paneId"/>. This is the reading half of <see cref="AddProjectField"/>, and a plugin may read a
    /// key it did not register — that is the point of two plugins agreeing on one.
    /// <para>
    /// A null <paramref name="paneId"/> means the selected session (<see cref="ICockpitSessionObserver.ActivePaneId"/>),
    /// which is what a dialog opened from the side menu is acting for; a contribution that belongs to one session
    /// passes that session's own <see cref="Sessions.IPluginSessionContext.PaneId"/> instead of relying on which pane
    /// happens to be selected.
    /// </para>
    /// Default <see langword="null"/> so existing <see cref="ICockpitHost"/> implementations keep compiling untouched.
    /// </summary>
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

    /// <summary>
    /// Whether the background update check installs a newer version of a managed CLI itself, instead of only
    /// toasting that one exists (AC-767) — what the shared <see cref="ManagedCli.ManagedCliConfigSection"/>'s
    /// "Update automatically" checkbox reads. Default <see langword="true"/>: an installation that never touched
    /// this setting keeps auto-updating, and existing <see cref="ICockpitHost"/> implementations (test fakes, older
    /// plugin builds) keep compiling untouched.
    /// </summary>
    Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>
    /// Turns auto-update for a managed CLI on or off (AC-767) — what the checkbox writes. Default no-op so existing
    /// <see cref="ICockpitHost"/> implementations keep compiling untouched; only the app's own host persists it.
    /// </summary>
    Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Registers something this plugin gives every session as it starts (AC-165): environment variables, asked for
    /// per session so the answer can depend on the project it belongs to. The host asks each registered provider
    /// once per launch, merges what they return, and hands it to whichever provider is starting — so a plugin
    /// contributes once and reaches Claude, Codex, Kimi and a TTY alike, without knowing any of them.
    /// <para>
    /// The counterpart to what a project <em>tells</em> its sessions (its memory location, the information rows it
    /// shares), which arrives as standing instructions. This one arrives in the process.
    /// </para>
    /// Default no-op so existing <see cref="ICockpitHost"/> implementations (test fakes, older plugin builds) keep
    /// compiling untouched — only the app's own host asks anyone.
    /// </summary>
    void AddSessionResourceProvider(Sessions.ISessionResourceProvider provider)
    {
    }

    /// <summary>The session-resource providers every plugin has contributed — what a starting session is assembled from. Default empty.</summary>
    IReadOnlyList<Sessions.ISessionResourceProvider> SessionResourceProviders => [];

    /// <summary>
    /// Registers a place a plugin can list projects it shares elsewhere but this machine has not bound yet (AC-245)
    /// — a Depot connection's own catalog, say — so the Projects workspace can offer them beside the local ones,
    /// under a heading named by <see cref="Projects.SharedProject.SourceName"/>.
    /// <para>
    /// A <see cref="Projects.ISharedProjectSource.Key"/> another plugin already registered is kept as it was and
    /// this registration ignored, the same agreement <see cref="AddProjectMemorySource"/> makes for a scheme two
    /// plugins both offer.
    /// </para>
    /// This is additive to the AC-40 contract: <see cref="AbstractionsContract.Version"/> stays 1, the same as every
    /// other default-implemented member added here. Default no-op so existing <see cref="ICockpitHost"/>
    /// implementations (test fakes, older plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void AddSharedProjectSource(Projects.ISharedProjectSource source)
    {
    }

    /// <summary>
    /// Withdraws the shared-project source registered under <paramref name="key"/> (AC-245) — a plugin that can
    /// offer more than one source (a Depot connection the operator later removes, say) uses this so a source that
    /// no longer applies stops being offered, instead of lingering there until the app restarts. A no-op when
    /// nothing is registered under this key. Default no-op so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host records it.
    /// </summary>
    void RemoveSharedProjectSource(string key)
    {
    }

    /// <summary>The shared-project sources every plugin has contributed — what the Projects workspace reads to list them. Default empty.</summary>
    IReadOnlyList<Projects.ISharedProjectSource> SharedProjectSources => [];

    /// <summary>
    /// The app's own "open the help about this" affordance (AC-1033), pointing at one article and — when
    /// <paramref name="section"/> is given — one of its sections. Pass <paramref name="label"/> for a worded
    /// link instead of a bare mark, and see Help ▸ Extending Cockpit ▸ Shipping documentation for how the
    /// article name is resolved and why the returned control hides itself when its target does not exist.
    /// </summary>
    Control CreateHelpHint(string article, string? section = null, string? label = null) =>
        new Panel { IsVisible = false };

    /// <summary>
    /// Opens the help window on <paramref name="article"/>, or on one of its sections — the jump
    /// <see cref="CreateHelpHint"/> performs, for a control the plugin drew itself (AC-1033). A target that
    /// resolves to nothing fails visibly in the window rather than opening the overview.
    /// </summary>
    void OpenHelp(string article, string? section = null)
    {
    }

    /// <summary>
    /// Whether <paramref name="article"/> — and <paramref name="section"/>, when given — is there to be opened
    /// (AC-1033). For deciding whether to word an error message with a link at all; <see cref="CreateHelpHint"/>
    /// asks this itself.
    /// </summary>
    bool HasHelp(string article, string? section = null) => false;
}
