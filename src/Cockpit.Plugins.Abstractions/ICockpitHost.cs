using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workflows;

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
    /// Adds an in-process MCP server to the cockpit (#AC-12): the host mounts <paramref name="tools"/> — an already-
    /// built class whose <c>[McpServerTool]</c> methods are the tools, constructed by the plugin with its own
    /// dependencies — on a loopback address and auto-publishes it to the registry under <paramref name="serverName"/>
    /// as its own MCP server, tickable per session. This is how a plugin gives agents its own tools (workflows,
    /// say) without any Kestrel or registry code. Idempotent per name. <paramref name="enabledByDefault"/> follows
    /// the same on-by-default rule as a built-in cockpit MCP. Call it fire-and-forget from
    /// <see cref="ICockpitPlugin.Initialize"/>. Default no-op so existing host implementations keep compiling.
    /// </summary>
    Task AddMcpEndpoint(string serverName, object tools, bool enabledByDefault = true) => Task.CompletedTask;

    /// <summary>
    /// The read/observe surface over the cockpit's sessions (the contract's first "read-as" capability):
    /// the active session's working directory and a stream of session output, so a plugin can react to what
    /// a session is doing rather than only writing into it. Default returns
    /// <see cref="NullCockpitSessionObserver.Instance"/> so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host supplies a live one.
    /// </summary>
    ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

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
    /// Registers a keyboard shortcut (e.g. YouTrack on <c>Shift+Y</c>): the host binds
    /// <see cref="PluginShortcut.DefaultGesture"/> and runs <see cref="PluginShortcut.OnInvoke"/> when it is
    /// pressed, shown alongside the built-in shortcuts in Options. Only fires when the operator is not typing
    /// into a text field or the terminal. Default no-op so existing <see cref="ICockpitHost"/> implementations
    /// (test fakes, older plugin builds) keep compiling untouched — only the app's own host wires it up.
    /// </summary>
    void AddShortcut(PluginShortcut shortcut)
    {
    }
}
