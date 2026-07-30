using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Hotkeys;
using Cockpit.Core.Screenshots;
using Cockpit.Core.Toasts;
using Cockpit.Core.Usage;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Updates;
using Cockpit.Core.Backup;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.UsagePill;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Clones;
using Cockpit.Core.Abstractions.Rendering;
using Cockpit.Core.Configuration;
using Cockpit.Core.Rendering;
using Cockpit.Core.Secrets;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Audio;
using Cockpit.Core.Debugging;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Projects;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Shortcuts;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.UsagePill;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workspaces;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Multi-instance cockpit shell: owns the collection of running <see cref="SessionViewModel"/>
/// panels, which one is selected, and the grid/zoom view mode. Reuses the existing
/// <see cref="SessionViewModel"/>/<c>SessionView</c> per panel — this view model only
/// adds the manager layer around it. See <c>Memory/Cockpit/Plan.md</c> §Vision-uitbreiding + §UX-eisen.
/// </summary>
/// <remarks>
/// Also carries the F0 audio record/play commands so the sidebar's secondary "Tools" footer (see
/// <c>CockpitView.axaml</c>) can bind to them without reaching into a sibling view model — the
/// cockpit is the single root VM behind the window; audio is a small, secondary tool hanging off it.
/// </remarks>
// Singleton: it is the single root view model behind the window, and the shutdown path resolves it
// back to dispose the live sessions (bug #32) — that must be the same instance the window holds.
public partial class CockpitViewModel : ViewModelBase, ISingletonService, IAsyncDisposable, IPluginContributionSink, IEmbeddedSessionHost
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<SessionViewModel>? _sessionFactory;
    private readonly Func<TtyViewModel>? _ttySessionFactory;
    private readonly ISessionProfileStore? _sessionProfileStore;
    private readonly IWorktreeManager? _worktreeManager;
    private readonly ITerminalAccessRegistry? _terminals;
    private readonly IWorkspaceAgentCoordinator? _agentCoordinator;
    private readonly IAgentMessageInbox? _agentMessages;
    private readonly IAgentResourceClaims? _agentClaims;
    private readonly IClaimCollisionMonitor? _claimCollisionMonitor;
    private readonly LiveSessionRegistry? _liveSessions;
    private readonly ISessionDialogService? _dialogService;
    private readonly SessionStateRecorder? _sessionStateRecorder;
    private readonly ISessionStateStore? _sessionStateStore;
    private readonly SessionRestorePlanner? _sessionRestorePlanner;
    private readonly IWorktreeReconcileGate? _worktreeReconcileGate;
    private readonly ILogger<CockpitViewModel>? _logger;

    /// <summary>Composes what a session started from a project opens with (AC-164). Null in the design-time/unit-test graph, where a quick start falls back to the dialog.</summary>
    private readonly ProjectQuickStart? _projectQuickStart;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly IShortcutSettingsStore? _shortcutSettingsStore;
    private readonly IBackupService? _backupService;
    private readonly IAppRestartService? _appRestart;
    private readonly IUpdateService? _updates;
    private readonly IUpdateSettingsStore? _updateSettingsStore;
    // The process-wide key holder, listened to so the awareness banner (AC-41) reappears the moment a save writes
    // a new credential in the clear. A static singleton, so the subscription is unwired in DisposeAsync — a view
    // model that outlived its window would otherwise be kept alive by it, and refresh a dead Security tab. The
    // design-time constructor leaves it at this default and never subscribes (nothing writes there); the real one
    // reassigns it to the injected holder and wires the event.
    private readonly ISecretKeyHolder _secretKeyHolder = SecretKeyHolder.Shared;
    private ShortcutSettings _shortcutSettings = ShortcutSettings.Default;
    private readonly ITranscriptDisplaySettingsStore? _transcriptDisplaySettingsStore;
    private readonly IUsagePillSettingsStore? _usagePillSettingsStore;
    private readonly ISessionBehaviorSettingsStore? _sessionBehaviorSettingsStore;
    private readonly IScreenshotSettingsStore? _screenshotSettingsStore;
    private readonly ILayoutSettingsStore? _layoutSettingsStore;
    private readonly IDebugSettingsStore? _debugSettingsStore;
    private readonly IDelegationMcpToggle? _delegationMcpToggle;
    private readonly ISessionResourceResolver? _sessionResourceResolver;
    private readonly IConsentBroker? _consentBroker;
    private readonly ResourceMonitor? _resourceMonitor;
    private readonly IVoiceSettingsStore? _voiceSettingsStore;
    private readonly ITerminalSettingsStore? _terminalSettingsStore;
    private readonly IWorktreeSettingsStore? _worktreeSettingsStore;
    private readonly ICloneSettingsStore? _cloneSettingsStore;
    private readonly IAudioDeviceProvider? _audioDeviceProvider;
    private readonly IModelCatalog? _modelCatalog;
    private readonly IVoicePlaybackQueue? _voicePlaybackQueue;
    private readonly ITranscriptCleanupService? _cleanupService;
    private readonly ILocalLlmEndpointResolver? _localLlmEndpointResolver;
    private readonly IAudioCaptureService? _audioCapture;
    private CancellationTokenSource? _micTestCancellation;

    // How long the Options dialog waits on a local-LLM probe (resolve + /v1/models) before giving up and keeping
    // the seeded model list — a stopped server refuses fast, but a running-but-busy one can otherwise stall.
    private static readonly TimeSpan LlmProbeTimeout = TimeSpan.FromSeconds(3);

    // Suppresses the per-property refresh hooks while the load method sets several voice-LLM fields at once, and
    // while a refresh rebuilds the model list (whose Clear() writes a null selection back through the ComboBox).
    private bool _suppressVoiceLlmHooks;
    // Coalesces refreshes: a request made while one runs sets the flag, and the running one loops once more — so
    // overlapping refreshes never race the model collection.
    private bool _voiceLlmRefreshing;
    private bool _voiceLlmRefreshQueued;
    private readonly PluginDiagnostics? _pluginDiagnostics;
    private readonly bool _safeMode;
    private readonly IPluginDialogHost? _pluginDialogHost;
    private readonly List<byte> _recordedPcm = [];

    // Last observed status per session, so a NeedsAttention notification fires only on the edge into
    // that state — not on every property change while a session already needs attention.
    private readonly Dictionary<SessionPanelViewModel, SessionStatus> _lastStatus = [];
    private CancellationTokenSource? _recordingCancellation;
    private int _sessionCounter;

    // "Everything is quiet" is edge-triggered too: announced when the last working session falls idle, and armed
    // again only once something starts working, so a cockpit left alone does not repeat itself every sweep.
    private bool _allSessionsIdleNotified = true;

    public ObservableCollection<SessionPanelViewModel> Sessions { get; } = [];

    /// <summary>
    /// Holds the prompts waiting to be sent to a session at a future moment (AC-234). Handed in by the app at
    /// startup rather than taken through the constructor, so the unit-test and design-time graphs — which build
    /// this view-model from the container — never construct a scheduler, never touch the config file, and never
    /// leave one running behind a test.
    /// </summary>
    public ScheduledResumeCoordinator? ScheduledResumes { get; set; }

    /// <summary>
    /// The operator's own usage thresholds (AC-233), loaded once and handed to each session as it is created.
    /// Null in the graphs that never load them, and every signal then warns where its provider said.
    /// </summary>
    public UsageThresholdSettings? UsageThresholds { get; set; }

    /// <summary>
    /// The usage-threshold settings screen (AC-233), rendered from what the providers declared. Handed in by the
    /// app at startup for the same reason the scheduler is: the test and design-time graphs build a cockpit
    /// without one and touch no config.
    /// </summary>
    public UsageThresholdsViewModel? UsageThresholdSettings { get; set; }

    /// <summary>
    /// The sidebar's own display order (AC-115). Kept apart from <see cref="Sessions"/> on purpose: the session
    /// grid binds straight to <see cref="Sessions"/> and keeps its own positional cell layout, so reordering the
    /// strip must never touch <see cref="Sessions"/> — moving an item there rebuilds its pane (a fresh TTY with no
    /// pty → a black terminal) and drags the grid tiles along with the strip. This list is reconciled against
    /// <see cref="Sessions"/> on read: new sessions append, closed ones drop out, and a drag only re-slots it here.
    /// In-memory only, like the sessions it orders.
    /// </summary>
    private readonly List<SessionPanelViewModel> _sidebarOrder = [];

    /// <summary>Left-menu accordion sections contributed by plugins (#14), shown under the session list. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSideSection> PluginSideSections { get; } = [];

    /// <summary>Left-menu launcher buttons contributed by plugins (#14); clicking one runs the plugin's action (typically opening a dialog).</summary>
    public ObservableCollection<PluginSideButton> PluginSideButtons { get; } = [];

    /// <summary>Controls contributed by plugins to every session's header bar, each built per session from that session's own context. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSessionHeaderItem> PluginSessionHeaderItems { get; } = [];

    /// <summary>What plugins can *do* to one session (#: session actions) — gathered into the single menu in every session's header, rather than a button each.</summary>
    public ObservableCollection<PluginSessionAction> PluginSessionHeaderActions { get; } = [];

    /// <summary>Plugin-registered sources of supervised background activities (AC-82) — the status bar shows a counter per source (only while it has activities) and a panel with a Kill per item.</summary>
    public ObservableCollection<ISupervisedActivitySource> PluginSupervisedActivities { get; } = [];

    /// <summary>Sessions-toolbar buttons contributed by plugins (AC-91) — global quick actions shown next to the workspace gear. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginToolbarAction> PluginToolbarActions { get; } = [];

    /// <summary>
    /// The operator's left-menu preference per plugin (#72): where it sits, and whether it shows there at all.
    /// Read from the plugin registrations at startup and refreshed when the manager changes one. A plugin the
    /// operator never touched is absent, which is what keeps discovery order the default.
    /// </summary>
    private readonly Dictionary<string, PluginMenuPreference> _pluginMenuPreferences = new(StringComparer.Ordinal);

    /// <summary>Raised when the left-menu order or visibility changed (#72) — the cue for the sidebar to rebuild.</summary>
    public event EventHandler? PluginMenuChanged;

    /// <summary>
    /// Everything the plugins put in the left menu — launcher buttons and inline sections alike — in the order and
    /// visibility the operator chose (#72); ties keep the order the plugins were discovered in.
    /// <para>
    /// One list, not one per kind: drawing every button and then every section meant a plugin that contributes a
    /// section (the open pull requests) sat below every plugin that contributes a button, however far up the operator
    /// moved it. An order that a plugin's kind can overrule is not an order.
    /// </para>
    /// </summary>
    public IReadOnlyList<PluginMenuEntry> VisibleMenuEntries =>
        PluginSideButtons.Select(button => new PluginMenuEntry(button.PluginId, button, null))
            .Concat(PluginSideSections.Select(section => new PluginMenuEntry(section.PluginId, null, section)))
            .Where(entry => !_IsHiddenInMenu(entry.PluginId))
            // OrderBy is stable, and the buttons come first above — so a plugin contributing both keeps its button
            // above its own section, where a launcher belongs.
            .OrderBy(entry => _MenuOrderOf(entry.PluginId))
            .ToList();

    /// <summary>The plugin Sessions-toolbar buttons in the operator's chosen order/visibility (#72) — the same hide/order rules as the left menu, so a plugin hidden there does not surface a toolbar button either.</summary>
    public IReadOnlyList<PluginToolbarAction> VisibleToolbarActions =>
        PluginToolbarActions
            .Where(action => !_IsHiddenInMenu(action.PluginId))
            .OrderBy(action => _MenuOrderOf(action.PluginId))
            .ToList();

    /// <summary>Applies a menu preference the plugin manager just persisted, and tells the sidebar to rebuild (#72).</summary>
    public void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu)
    {
        _pluginMenuPreferences[pluginId] = new PluginMenuPreference(menuOrder, hiddenInMenu);
        PluginMenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private int _MenuOrderOf(string pluginId) =>
        _pluginMenuPreferences.TryGetValue(pluginId, out var preference) ? preference.Order : 0;

    private bool _IsHiddenInMenu(string pluginId) =>
        _pluginMenuPreferences.TryGetValue(pluginId, out var preference) && preference.Hidden;

    private sealed record PluginMenuPreference(int Order, bool Hidden);

    /// <summary>Keyboard shortcuts contributed by plugins (#: shortcuts), dispatched alongside the built-in app-action shortcuts.</summary>
    public ObservableCollection<PluginShortcut> PluginShortcuts { get; } = [];

    /// <summary>The currently-active shortcuts (app actions + plugin shortcuts) the view matches key presses against. Rebuilt when settings or plugin shortcuts change.</summary>
    public IReadOnlyList<ShortcutBinding> ActiveShortcuts { get; private set; } = [];

    /// <summary>Rows for the Options → Shortcuts tab: the editable app-action gestures, then the read-only plugin-contributed ones.</summary>
    public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; } = [];

    /// <summary>Per-plugin settings views (#14) keyed by plugin folder id, opened from any of the gears — the plugin manager's, the left-menu button's, a plugin dialog's — or by the plugin itself.</summary>
    public Dictionary<string, PluginSettingsRegistration> PluginSettings { get; } = [];

    /// <summary>Settings-saved callbacks (#52) keyed by plugin folder id, registered via <see cref="ICockpitHost.OnSettingsSaved"/> and run once that plugin's settings dialog Save() returns true.</summary>
    private readonly Dictionary<string, List<Action>> _settingsSavedHandlers = [];

    /// <summary>The "Plugins" Options tab (#14): install/enable/disable/remove installed plugins. Loaded when the Options dialog opens.</summary>
    public PluginManagerViewModel Plugins { get; }

    /// <summary>The delegated-tasks view (#67): work other sessions handed to a profile, which has no tab of its own.</summary>
    public DelegatedTasksViewModel DelegatedTasks { get; }

    /// <summary>The git worktrees the cockpit created (AC-85): the status-bar counter and the management dialog read this one shared view model.</summary>
    public WorktreesViewModel Worktrees { get; }

    /// <summary>The operator's projects (AC-161): the Options tab that manages them and the sidebar section that starts them read this one shared view model.</summary>
    public ProjectsViewModel Projects { get; }

    /// <summary>The workspace tab strip and the active workspace's panes.</summary>
    public WorkspacesViewModel Workspaces { get; }

    /// <summary>
    /// Names what closing this workspace takes with it, asks, and closes it if the answer is yes — the one path
    /// behind the tab's ✕, its context menu and the command palette, so the prompt cannot drift from what
    /// closing actually does.
    /// <para>
    /// It asks because none of it comes back: a dashboard's whole arrangement, or every session tied to it. The
    /// message says what is about to go rather than "are you sure" — "this cannot be undone" tells an operator
    /// nothing they had not already assumed.
    /// </para>
    /// </summary>
    public async Task CloseWorkspaceWithConfirmationAsync(string workspaceId)
    {
        if (Workspaces.Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } workspace
            || !Workspaces.CanClose(workspaceId))
        {
            return;
        }

        string? message;
        if (workspace.Type == WorkspaceType.Dashboard)
        {
            var loses = _Count(workspace.Panes.Count, "widget");
            message = loses is null
                ? $"Close “{workspace.Name}”?"
                : $"Close “{workspace.Name}” and everything on it?\n\nIt holds {loses}. Closing the workspace discards its layout, and this cannot be undone.";
        }
        else
        {
            // AC-410: a restored pane still showing its offer (HasRestoreOffer) has no runtime behind it — nothing
            // this close would actually stop. Counted apart rather than folded into "sessions", which is the
            // drift this method used to warn about in its own comment: "3 sessions, which will be stopped" reads
            // as a lie the moment one of the three never started.
            var onDesk = Sessions.Where(session => session.WorkspaceId == workspace.Id).ToList();
            var started = onDesk.Count(session => !session.HasRestoreOffer)
                // A plugin workspace's sessions are embedded, so they are not in Sessions — count them too, or the
                // prompt undercounts what closing the desk is about to stop (an agent left running is the one you
                // most want the warning for). Embedded sessions are never restored-but-unstarted (AC-410's "Niet"
                // list), so they always belong on the started side.
                + _EmbeddedSessionCount(workspace.Id);
            var notStarted = onDesk.Count(session => session.HasRestoreOffer);

            message = (started, notStarted) switch
            {
                (0, 0) => $"Close “{workspace.Name}”?",
                (_, 0) => $"Close “{workspace.Name}” and everything on it?\n\nIt holds {_Count(started, "session")}, which will be stopped. This cannot be undone.",
                (0, _) => $"Close “{workspace.Name}” and everything on it?\n\nIt holds {_Count(notStarted, "restored session")} that never started — there is nothing to stop. This cannot be undone.",
                _ => $"Close “{workspace.Name}” and everything on it?\n\nIt holds {_Count(started, "session")}, which will be stopped, and {_Count(notStarted, "restored session")} that never started. This cannot be undone.",
            };
        }

        if (await ConfirmAsync("Close workspace", message, confirmLabel: "Close"))
        {
            await CloseWorkspaceAsync(workspaceId);
        }
    }

    /// <summary>"3 widgets" / "1 session", or null when there is nothing to lose — an empty workspace needs no warning about what it holds.</summary>
    private static string? _Count(int count, string noun) =>
        count == 0 ? null : count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>How many sessions a plugin workspace runs embedded in its body — kept out of <see cref="Sessions"/>, so the close-confirmation counts them here or it undercounts what the workspace is about to stop.</summary>
    private int _EmbeddedSessionCount(string workspaceId) =>
        _embeddedSessions.TryGetValue(workspaceId, out var owned) ? owned.Count : 0;

    /// <summary>
    /// Closes a workspace and everything running on it (Raymond, 2026-07-15). Its sessions go first, through the
    /// ordinary close path so each is disposed the way it would be on its own — otherwise they keep running with
    /// a WorkspaceId pointing at a workspace that no longer exists: no tab shows them, nothing can reach them,
    /// and their pty and child process outlive the desk they belonged to. Invisible-but-alive is the worst of
    /// the three states a closed session can be in.
    /// </summary>
    public async Task CloseWorkspaceAsync(string workspaceId)
    {
        // The last workspace is not closable, and killing the sessions of a workspace that then stays is worse
        // than doing nothing: the desk survives, its work does not. Ask before touching either.
        if (!Workspaces.CanClose(workspaceId))
        {
            return;
        }

        // A plugin workspace's embedded sessions are not in Sessions, so the loop below never sees them — close
        // them here or their pty and child process would outlive the desk they belonged to (AC-122).
        CloseForWorkspace(workspaceId);

        // Snapshot first: closing a session mutates Sessions, and enumerating a collection you are removing from
        // is how you silently skip half of it.
        foreach (var session in Sessions.Where(session => session.WorkspaceId == workspaceId).ToList())
        {
            await CloseSessionCommand.ExecuteAsync(session);
        }

        await Workspaces.CloseWorkspaceCommand.ExecuteAsync(workspaceId);
    }

    /// <summary>
    /// Asks before something irreversible, through the same confirmation dialog the rest of the cockpit uses.
    /// Answers "no" without asking when there is no dialog service (design-time/tests): a graph with no way to
    /// ask must not answer yes on the operator's behalf.
    /// </summary>
    /// <summary>Picks a dashboard file to import; null without a dialog service, or when the operator backed out.</summary>
    public Task<string?> PickDashboardToImportAsync() =>
        _dialogService is null ? Task.FromResult<string?>(null) : _dialogService.PickDashboardToImportAsync();

    /// <summary>Picks where to write a dashboard; null without a dialog service, or when the operator backed out.</summary>
    public Task<string?> PickDashboardExportPathAsync(string suggestedName) =>
        _dialogService is null ? Task.FromResult<string?>(null) : _dialogService.PickDashboardExportPathAsync(suggestedName);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        _dialogService is null
            ? Task.FromResult(false)
            : _dialogService.ShowConfirmationDialogAsync(title, message, confirmLabel);

    /// <summary>
    /// Whether the session grid applies: sessions exist AND a Sessions workspace is active. A dashboard owns
    /// the content area while it is selected, so the grid must stand down even though the sessions themselves
    /// keep running — they are hidden, not closed.
    /// </summary>
    public bool ShowSessionGrid => HasSessionsHere && Workspaces.IsSessionsActive;

    /// <summary>The "no sessions yet" prompt: only on a Sessions workspace, since a dashboard cannot hold a session and has its own empty state.</summary>
    public bool ShowSessionEmptyState => !HasSessionsHere && Workspaces.IsSessionsActive;

    /// <summary>
    /// Whether the workspace now showing holds any session. Deliberately not <see cref="HasSessions"/>: a fresh
    /// second workspace has to greet you with the empty state, even while the first one is full of running
    /// sessions.
    /// </summary>
    public bool HasSessionsHere => VisibleSessions.Any();

    /// <summary>Owns the live toast collection (#61); <see cref="Toasts"/> below is what <c>CockpitView.axaml</c>'s overlay actually binds to.</summary>
    public ToastHostViewModel ToastHost { get; } = new();

    /// <summary>Toasts currently shown by the overlay (#61), fed by <see cref="Services.ToastService"/> via <see cref="ToastHost"/>.</summary>
    public ObservableCollection<ToastViewModel> Toasts => ToastHost.Toasts;

    /// <summary>A dismissible banner shown when one or more plugins failed to load (#14) — the app keeps running; details are in Options → Plugins.</summary>
    [ObservableProperty]
    private string _pluginFailureBanner = string.Empty;

    /// <summary>True while the plugin-failure banner should be shown.</summary>
    [ObservableProperty]
    private bool _hasPluginFailures;

    /// <summary>A dismissible banner (AC-208) shown when one or more plugins are sitting at awaiting-approval — new, or their bytes changed since last approved — so that state is visible without opening Plugin store → Installed.</summary>
    [ObservableProperty]
    private string _pendingApprovalBanner = string.Empty;

    /// <summary>True while the pending-approval banner should be shown.</summary>
    [ObservableProperty]
    private bool _hasPendingApprovals;

    /// <summary>
    /// Whether this run was started with <see cref="PluginManager.SafeModeArgument"/> (AC-478) — no plugin was
    /// instantiated, so the safe-mode banner and its "Restart" affordance (<see cref="RestartAppCommand"/>, which
    /// exits safe mode on any restart — see <c>AppRestartService.BuildLaunchArguments</c>) stay on screen for the
    /// whole run, never dismissed like the failure/pending-approval banners above: it describes the run itself,
    /// not a one-off event to acknowledge.
    /// </summary>
    public bool IsSafeMode => _safeMode;

    /// <summary>The safe-mode banner's text (AC-478); empty (and so invisible, see <see cref="IsSafeMode"/>) on an ordinary run.</summary>
    public string SafeModeBanner => _safeMode
        ? "Safe mode — no plugins were loaded. Plugin manager still works: disable the one that is crashing, then restart."
        : string.Empty;

    /// <summary>
    /// Reads the recorded plugin issues and raises the startup banner; called after plugin phase-2 completes,
    /// and again on every later <see cref="PluginDiagnostics.Changed"/> (#184) — a contribution such as
    /// <see cref="CockpitHost.AddMcpServer"/> can fail after that point, and the banner must not go on
    /// reflecting only the snapshot from startup while the Plugin manager moves on. Errors (a plugin that did
    /// not load), warnings (one that loaded but is flagged, e.g. built against a newer SDK) and a contribution
    /// failing after load read as three different facts, since the operator can do something different about
    /// each.
    /// </summary>
    public void RefreshPluginFailures()
    {
        var issues = _pluginDiagnostics?.Failures ?? [];
        var activationIssues = issues.Where(issue => PluginDiagnostics.ActivationPhases.Contains(issue.Phase) || issue.Phase == "compatibility").ToList();
        var errors = activationIssues.Where(issue => issue.Severity == PluginIssueSeverity.Error).ToList();
        var warnings = activationIssues.Where(issue => issue.Severity == PluginIssueSeverity.Warning).ToList();

        // A contribution recorded after Initialize (e.g. a failed AddMcpServer upsert) is not "failed to load" —
        // the plugin is running, one thing it registered is not. Grouped to one (its latest) per folder, since a
        // folder that also has an activation issue would otherwise count twice for the same plugin.
        var contributionFailures = issues
            .Where(issue => !PluginDiagnostics.ActivationPhases.Contains(issue.Phase) && issue.Phase != "compatibility")
            .GroupBy(issue => issue.FolderId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        HasPluginFailures = activationIssues.Count > 0 || contributionFailures.Count > 0;

        var loadPart = (errors.Count, warnings.Count) switch
        {
            (0, 0) => null,
            (1, 0) => $"a plugin failed to load: {errors[0].DisplayName}",
            (> 1, 0) => $"{errors.Count} plugins failed to load",
            (0, 1) => $"a plugin may be incompatible with this app: {warnings[0].DisplayName}",
            (0, _) => $"{warnings.Count} plugins may be incompatible with this app",
            _ => $"{errors.Count} plugins failed to load and {warnings.Count} may be incompatible",
        };
        var contributionPart = contributionFailures.Count switch
        {
            0 => null,
            1 => $"a plugin's contribution failed after it loaded: {contributionFailures[0].DisplayName}",
            _ => $"{contributionFailures.Count} plugins had a contribution fail after loading",
        };

        PluginFailureBanner = (loadPart, contributionPart) switch
        {
            (null, null) => string.Empty,
            ({ } load, null) => $"{_Capitalize(load)}. See the Plugin store → Installed for details.",
            (null, { } contribution) => $"{_Capitalize(contribution)}. See the Plugin store → Installed for details.",
            ({ } load, { } contribution) => $"{_Capitalize(load)}, and {contribution}. See the Plugin store → Installed for details.",
        };

        var pending = _pluginDiagnostics?.PendingApprovals ?? [];
        HasPendingApprovals = pending.Count > 0;
        PendingApprovalBanner = pending.Count switch
        {
            0 => string.Empty,
            1 => $"1 plugin is awaiting approval: {pending[0].DisplayName}. See Plugin store → Installed to review it.",
            _ => $"{pending.Count} plugins are awaiting approval. See Plugin store → Installed to review them.",
        };

        // AC-208: seeds the sidebar "Plugin store" badge with the same count, so it is visible right from
        // startup — Plugins.LoadAsync (the badge's live source once the operator opens the manager) has not run
        // yet at this point, called as this is right after plugin phase-2 completes.
        Plugins.SeedPendingApprovalCount(pending.Count);
    }

    private static string _Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [RelayCommand]
    private void DismissPluginFailures() => HasPluginFailures = false;

    [RelayCommand]
    private void DismissPendingApprovals() => HasPendingApprovals = false;

    void IPluginContributionSink.AddPluginSideSection(string pluginId, string title, Func<Control> createView) =>
        _OnUiThread(() => PluginSideSections.Add(new PluginSideSection(pluginId, title, createView)));

    void IPluginContributionSink.AddPluginSideButton(string pluginId, string title, Action onInvoke) =>
        _OnUiThread(() => PluginSideButtons.Add(new PluginSideButton(pluginId, title, onInvoke)));

    void IPluginContributionSink.AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        _OnUiThread(() => PluginSessionHeaderItems.Add(new PluginSessionHeaderItem(createView)));

    void IPluginContributionSink.AddPluginSessionHeaderAction(PluginSessionAction action) =>
        _OnUiThread(() => PluginSessionHeaderActions.Add(action));

    void IPluginContributionSink.AddSupervisedActivityProvider(ISupervisedActivitySource source) =>
        _OnUiThread(() => PluginSupervisedActivities.Add(source));

    void IPluginContributionSink.AddToolbarAction(string pluginId, ToolbarAction action) =>
        _OnUiThread(() => PluginToolbarActions.Add(new PluginToolbarAction(pluginId, action)));

    void IPluginContributionSink.AddPluginShortcut(PluginShortcut shortcut) =>
        _OnUiThread(() => PluginShortcuts.Add(shortcut));

    // Registration touches only this plain dictionary — never a bound ObservableCollection — and every caller is
    // an Avalonia UI-thread callback in practice (a plugin's Initialize). Kept synchronous for the same reason as
    // AddSettingsSavedHandler below: a dispatcher hop would only run once something pumps the queue, which leaves
    // the registration invisible to anything reading it in the same turn.
    void IPluginContributionSink.AddPluginSettings(string pluginId, string pluginName, Func<Control> createView) =>
        PluginSettings[pluginId] = new PluginSettingsRegistration(pluginId, pluginName, createView);

    public bool HasPluginSettings(string pluginId) => PluginSettings.ContainsKey(pluginId);

    /// <summary>
    /// The single way a plugin's settings dialog opens, wherever the gear that opened it sits (#: settings from
    /// anywhere). Every entry point routes here rather than opening the view itself, so a settings change saved
    /// from a plugin's own dialog runs the same settings-saved handlers as one saved from the manager — a plugin
    /// that re-registers its MCP server on save must not depend on which gear the operator happened to reach for.
    /// </summary>
    public async Task OpenPluginSettingsAsync(string pluginId)
    {
        if (_pluginDialogHost is null || !PluginSettings.TryGetValue(pluginId, out var settings))
        {
            return;
        }

        await _pluginDialogHost.ShowSettingsDialogAsync(
            $"{settings.PluginName} settings",
            settings.CreateView,
            640,
            560,
            onSaved: () => ((IPluginContributionSink)this).NotifySettingsSaved(pluginId),
            // One settings window per plugin (AC-367): every gear that reaches a plugin's settings routes here, so
            // the one on its own dialog and the one in the manager would otherwise open two forms over one store,
            // where whichever is saved last wins without saying so.
            singleInstanceKey: $"settings:{pluginId}");
    }

    /// <summary>
    /// The ⚙ on a widget pane. The widget supplies the form's content and the host puts it in the same
    /// Save/Close dialog a plugin's own settings use — a widget never builds a window. Saving asks that
    /// instance to refresh, which is how its view picks up the config the form just wrote, without the widget
    /// having to watch its own storage.
    /// </summary>
    public async Task ShowWidgetSettingsAsync(WidgetPaneViewModel pane)
    {
        if (_pluginDialogHost is null || pane.CreateConfigView() is not { } form)
        {
            return;
        }

        await _pluginDialogHost.ShowSettingsDialogAsync(
            $"{pane.Title} settings",
            () => form,
            520,
            460,
            onSaved: pane.Refresh,
            // Per widget, and not optional here (AC-367): the form is built once above and handed to the window as
            // a captured instance, so a second window would try to adopt a control that already has a parent.
            singleInstanceKey: $"widget:{pane.Id}");
    }

    // Unlike the three contributions above, registration here touches only this private dictionary — never
    // a bound ObservableCollection — and both members are reached exclusively from Avalonia UI-thread
    // callbacks in practice (a contribution's own constructor, and the settings dialog's Save click), so no
    // dispatcher hop is needed. Kept synchronous rather than routed through _OnUiThread — that hop only
    // actually runs when something later pumps the dispatcher queue, which a unit test never does.
    void IPluginContributionSink.AddSettingsSavedHandler(string pluginId, Action callback)
    {
        if (!_settingsSavedHandlers.TryGetValue(pluginId, out var handlers))
        {
            handlers = [];
            _settingsSavedHandlers[pluginId] = handlers;
        }

        handlers.Add(callback);
    }

    void IPluginContributionSink.NotifySettingsSaved(string pluginId)
    {
        if (!_settingsSavedHandlers.TryGetValue(pluginId, out var handlers))
        {
            return;
        }

        // Snapshot before invoking: a handler could itself register another (unlikely, but avoids mutating
        // the list while iterating it).
        foreach (var handler in handlers.ToArray())
        {
            handler();
        }
    }

    // Plugins register contributions from Initialize (run on the UI thread), but a plugin could also
    // add a section later off a background thread — marshal so the bound collections only mutate on the UI thread.
    private static void _OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>False when no session is open, driving the empty-state welcome screen vs. the session grid (#31).</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>
    /// Column count for the adaptive session grid (#24): one session fills the width; two or more lay
    /// out in two columns (so 3–4 form a 2×2), rather than the old fixed two that left a single session
    /// pinned to the left half.
    /// </summary>
    /// <remarks>Counts the workspace now showing, not every session alive: a second desk with one session must lay out as one, however full the first desk is.</remarks>
    public int GridColumns => VisibleSessions.Count() <= 1 ? 1 : 2;

    /// <summary>The Zoom toggle only makes sense in the grid layout with more than one session — a single session already fills the pane, and single-session layout has no grid to zoom out of.</summary>
    public bool ShowZoomButton => !SingleSessionLayout && VisibleSessions.Count() > 1;

    [ObservableProperty]
    private SessionPanelViewModel? _selectedSession;

    /// <summary>True while the grid is collapsed to show only <see cref="SelectedSession"/> at full width.</summary>
    [ObservableProperty]
    private bool _isZoomed;

    /// <summary>
    /// Options' "show one session at a time" (#24) — the cockpit-wide default, persisted to
    /// <c>LayoutSettings</c>. What a desk actually does is <see cref="SingleSessionLayout"/>: a Sessions
    /// workspace may override this (Raymond, 2026-07-15). Options edits the default and nothing else, or
    /// opening it on an overriding workspace would save that workspace's choice over the global one.
    /// </summary>
    [ObservableProperty]
    private bool _globalSingleSessionLayout;

    /// <summary>Options' "stack sessions vertically" — the cockpit-wide default. The effective value is <see cref="StackSessionsVertically"/>.</summary>
    [ObservableProperty]
    private bool _globalStackSessionsVertically;

    /// <summary>
    /// What the active workspace actually does: its own override, else Options' default. Everything that
    /// arranges panes reads this; nothing writes it.
    /// </summary>
    public bool SingleSessionLayout =>
        Workspaces?.Active is { SingleSessionLayout: { } single } active && active.Type == WorkspaceType.Sessions
            ? single
            : GlobalSingleSessionLayout;

    /// <summary>The active workspace's stacking, its own override else Options'. Bound to the grid's <see cref="Controls.SessionTilePanel.StackVertically"/>.</summary>
    public bool StackSessionsVertically =>
        Workspaces?.Active is { StackSessionsVertically: { } stack } active && active.Type == WorkspaceType.Sessions
            ? stack
            : GlobalStackSessionsVertically;

    /// <summary>
    /// Two-way for the Sessions ⚙: whether this desk follows Options. Unticking it starts the override from
    /// what the desk is doing right now, so taking control changes nothing until the operator changes
    /// something — a checkbox that rearranges your sessions the moment you tick it is one nobody ticks twice.
    /// </summary>
    public bool WorkspaceFollowsGlobalLayout
    {
        get => Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions
            || (sessions.SingleSessionLayout is null && sessions.StackSessionsVertically is null);
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == WorkspaceFollowsGlobalLayout)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(
                sessions.Id,
                value ? null : SingleSessionLayout,
                value ? null : StackSessionsVertically);
            _OnEffectiveLayoutChanged();
        }
    }

    /// <summary>Two-way for the Sessions ⚙'s own "show one session at a time" — writes this workspace's override, never Options.</summary>
    public bool WorkspaceSingleSessionLayout
    {
        get => SingleSessionLayout;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == SingleSessionLayout)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(sessions.Id, value, StackSessionsVertically);
            _OnEffectiveLayoutChanged();
        }
    }

    /// <summary>Two-way for the Sessions ⚙'s own "stack sessions vertically" — writes this workspace's override, never Options.</summary>
    public bool WorkspaceStackSessionsVertically
    {
        get => StackSessionsVertically;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == StackSessionsVertically)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(sessions.Id, SingleSessionLayout, value);
            _OnEffectiveLayoutChanged();
        }
    }

    /// <summary>
    /// True whenever the multi-pane grid is showing (two or more sessions, not the single-pane/zoom layout):
    /// every pane then carries the drag-reorder grip, and the column/row gutters between them are resizable.
    /// Covers the vertical column, the side-by-side row, and the 2×2 alike — they're one draggable grid.
    /// </summary>
    public bool StackSessionsInStack => !ShowSinglePane && Sessions.Count >= 2;

    /// <summary>When true, closing the window hides it to the system tray and keeps the app running (#33). Read by MainWindow on close.</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    /// <summary>
    /// Width in pixels of the left sidebar column (#49), dragged via the <c>GridSplitter</c> in
    /// <c>CockpitView.axaml</c> and persisted so it survives a restart. The splitter's column already
    /// enforces <see cref="LayoutSettings.MinSidebarWidth"/>/<see cref="LayoutSettings.MaxSidebarWidth"/>
    /// while dragging; <see cref="LoadLayoutSettingsAsync"/> and <c>LayoutSettingsStore</c> clamp again
    /// defensively for a value read from a hand-edited <c>cockpit.json</c>.
    /// </summary>
    [ObservableProperty]
    private double _sidebarWidth = LayoutSettings.DefaultSidebarWidth;

    /// <summary>When true the left sidebar is collapsed out of view; the session content takes its space. Toggled by the chevron in the sidebar header (and the floating one that appears when collapsed), persisted immediately.</summary>
    [ObservableProperty]
    private bool _sidebarCollapsed;

    [ObservableProperty]
    private string _layoutSettingsStatus = string.Empty;

    /// <summary>
    /// Mirrors <see cref="Cockpit.Core.Debugging.DebugSettings.ShowDebugControls"/> (#73): show the controls
    /// that exist to investigate the cockpit itself — the TTY header's Redraw — rather than to do the work.
    /// Off by default; pushed to open sessions so a change takes effect without reopening them.
    /// </summary>
    [ObservableProperty]
    private bool _showDebugControls;

    /// <summary>
    /// Whether the orchestrator (delegation) MCP is offered to sessions (AC-40). It is a cockpit-hosted server, no
    /// longer listed in the MCP-servers manager, so this Options toggle is where it is turned on or off. On by
    /// default; the change is persisted and takes effect on the next session's servers.
    /// </summary>
    [ObservableProperty]
    private bool _orchestratorMcpEnabled = true;

    [ObservableProperty]
    private string _debugSettingsStatus = string.Empty;

    /// <summary>
    /// Whether a backup keeps the keys, tokens and webhooks that live in the settings (#70). Off by design: the
    /// archive's whole use is that you can put it somewhere — a cloud folder, another machine — and a thing you can
    /// put anywhere must not be a key ring.
    /// </summary>
    [ObservableProperty]
    private bool _backupIncludesCredentials;

    /// <summary>Whether a backup also carries the profiles' own config directories (<c>~/.claude</c> and friends) — the agents' own logins, which live outside the cockpit's directory. Never a default.</summary>
    [ObservableProperty]
    private bool _backupIncludesProfiles;

    [ObservableProperty]
    private string _backupStatus = string.Empty;

    /// <summary>The plugins this backup will carry — their binaries and everything they saved. All of them, unless the operator unticks one.</summary>
    public ObservableCollection<BackupPluginViewModel> BackupPlugins { get; } = [];

    /// <summary>The build this cockpit is (#71): the version, and the commit — which is a nightly's only identity.</summary>
    [ObservableProperty]
    private string _currentBuild = string.Empty;

    /// <summary>Look for a newer build when the cockpit starts. On: an update nobody is told about is an update nobody installs.</summary>
    [ObservableProperty]
    private bool _checkForUpdatesOnStartup = true;

    /// <summary>Also hear about the nightly build of main. Off, and it means what it says: main, as it was last night.</summary>
    [ObservableProperty]
    private bool _includeNightlyBuilds;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>Where the newer build is, or empty — what the Download button opens.</summary>
    [ObservableProperty]
    private string _updateUrl = string.Empty;

    /// <summary>The newer build's name/version, shown as the headline of the persistent update banner (AC-73).</summary>
    [ObservableProperty]
    private string _updateName = string.Empty;

    /// <summary>
    /// Whether the persistent update banner (AC-73) is shown: a newer build was found and the operator has not
    /// dismissed this one. The startup toast auto-dismisses before the window has focus and is missed; the banner
    /// stays until "Open release" or dismiss, and comes back when a build newer than the dismissed one turns up —
    /// so the same release never nags while a genuinely newer one still gets through.
    /// </summary>
    [ObservableProperty]
    private bool _updateBannerVisible;

    /// <summary>
    /// Whether a download for "Update now"/"Install on next start" is in flight (AC-388). Drives the banner's/Options'
    /// progress indicator directly (AC-379: a rendered-view test asserts the control itself, not this field) and
    /// disables both buttons, so a second click cannot start a second transfer over the first.
    /// </summary>
    [ObservableProperty]
    private bool _isUpdateDownloading;

    /// <summary>
    /// 0-100 progress for the download <see cref="IsUpdateDownloading"/> is tracking (AC-388). Velopack's progress
    /// callback fires from whatever thread it runs its transfer on, not the UI thread (AC-368) — every write to this
    /// property from that callback goes through <c>Dispatcher.UIThread</c>, the same discipline
    /// <see cref="_periodicUpdateTimer"/>'s tick already follows.
    /// </summary>
    [ObservableProperty]
    private int _updateDownloadProgress;

    /// <summary>The version of the release now on offer, and of the one the operator last dismissed from the banner.
    /// A version identifies a build on its own: a nightly is packed as <c>-nightly.&lt;run&gt;</c>, so the rolling tag
    /// it is published under repeats but the version does not.</summary>
    private string _offeredRelease = string.Empty;
    private string _dismissedRelease = string.Empty;

    /// <summary>
    /// The channel the operator picked, or null while nobody has (AC-387). Held apart from
    /// <see cref="IncludeNightlyBuilds"/> — which shows the channel in force, chosen or derived — so that saving the
    /// settings for an unrelated reason cannot turn a derived channel into a choice behind the operator's back.
    /// </summary>
    private UpdateChannel? _chosenChannel;

    /// <summary>True while the stored settings are being applied, so filling the controls does not read as using them.</summary>
    private bool _loadingUpdateSettings;

    /// <summary>
    /// Which of the two update settings the operator has decided for. Kept apart rather than as one "touched" flag:
    /// they are stored together but chosen separately, and one flag for both means changing either one claims the
    /// other as well.
    /// </summary>
    private bool _startupChoiceMade;
    private bool _channelChoiceMade;

    /// <summary>
    /// Whether the stored update settings have been read and applied, and whether the operator changed something
    /// before that happened. Two plain flags rather than awaiting the read: both this and the read run on the UI
    /// thread, and awaiting the same task from two places says nothing about which of them resumes first — a save
    /// that woke up first would still be writing settings it had not learned yet.
    /// </summary>
    private bool _updateSettingsRead;
    private bool _updateSettingsSavePending;

    // How often the background re-check for a newer build runs while the cockpit is open (AC-188) — the startup look
    // is a single shot, this catches a release cut hours after the window opened.
    private static readonly TimeSpan PeriodicUpdateCheckInterval = TimeSpan.FromHours(1);

    // The hourly update-check timer (AC-188), on the same DispatcherTimer footing as the plugin/managed-CLI check in
    // App; null until StartPeriodicUpdateChecks runs, stopped in DisposeAsync.
    private DispatcherTimer? _periodicUpdateTimer;

    public bool CanCheckForUpdates => _updates is not null;

    /// <summary>
    /// Whether this copy can fetch a newer build over itself (AC-385) — true only for one the updater installed.
    /// Unpacked from the tarball, run from a checkout, or installed by a distribution's package manager, and the
    /// answer is no: the cockpit can still say that a newer build exists, but replacing this one is somebody
    /// else's job. Fixed for the lifetime of the process; see the constructor.
    /// </summary>
    public bool CanUpdateItself { get; }

    public bool HasUpdate => UpdateUrl.Length > 0;

    /// <summary>
    /// Whether "Update now"/"Install on next start" show, in the banner and in Options (AC-388): a build must be on
    /// offer, and this copy must be one the updater can replace. A rendered-view test asserts the actual
    /// <c>Button.IsVisible</c> against this, not <see cref="CanUpdateItself"/>/<see cref="HasUpdate"/> separately —
    /// AC-379's lesson, that a button hung off a container's own condition or an internal field a test cannot see is
    /// not the same as the button being visible for the right reason.
    /// </summary>
    public bool ShowSelfUpdateButtons => CanUpdateItself && HasUpdate;

    /// <summary>The pre-AC-388 fallback: a build is on offer but this copy cannot fetch it, so the release page is the whole offer.</summary>
    public bool ShowOpenReleaseButton => !CanUpdateItself && HasUpdate;

    /// <summary>
    /// Global TTY terminal font family (#40) — one setting for every TTY session, not per-profile or
    /// per-session. The effective value fed straight into <c>TerminalControl.FontFamily</c>, so both a
    /// single family name and a comma-separated fallback list work. Driven by the Options dropdown
    /// (<see cref="TerminalFontSelection"/>): a curated choice sets it directly, the "Custom…" choice
    /// mirrors <see cref="TerminalCustomFontFamily"/>.
    /// </summary>
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    /// <summary>Global TTY terminal font size in points (#40), clamped to <see cref="Cockpit.Core.Terminal.TerminalSettings.MinFontSize"/>-<see cref="Cockpit.Core.Terminal.TerminalSettings.MaxFontSize"/> on save.</summary>
    [ObservableProperty]
    private int _terminalFontSize = 13;

    /// <summary>Selected item in the Options font-family dropdown (#40) — a curated family or <see cref="CustomFontChoice"/>. Drives <see cref="TerminalFontFamily"/> and toggles <see cref="IsTerminalFontCustom"/>.</summary>
    [ObservableProperty]
    private string _terminalFontSelection = "Cascadia Mono, Consolas, monospace";

    /// <summary>True when the font-family dropdown is on "Custom…" (#40), revealing the free-text box bound to <see cref="TerminalCustomFontFamily"/>.</summary>
    [ObservableProperty]
    private bool _isTerminalFontCustom;

    /// <summary>Free-text font family entered when the dropdown is on "Custom…" (#40); mirrored into <see cref="TerminalFontFamily"/> while custom is active.</summary>
    [ObservableProperty]
    private string _terminalCustomFontFamily = string.Empty;

    [ObservableProperty]
    private string _terminalSettingsStatus = string.Empty;

    /// <summary>The worktree-root override (AC-85); blank uses the default. Bound in Options → Sessions.</summary>
    [ObservableProperty]
    private string _worktreeRoot = string.Empty;

    [ObservableProperty]
    private string _worktreeSettingsStatus = string.Empty;

    /// <summary>The default worktree root, shown as the folder field's placeholder so a blank value clearly means "use the default".</summary>
    public string WorktreeRootPlaceholder { get; private set; } = string.Empty;

    /// <summary>The clones-root override (AC-90); blank uses the default. Bound in Options → Sessions, alongside the worktree root.</summary>
    [ObservableProperty]
    private string _cloneRoot = string.Empty;

    [ObservableProperty]
    private string _cloneSettingsStatus = string.Empty;

    /// <summary>The default clones root, shown as the folder field's placeholder so a blank value clearly means "use the default".</summary>
    public string CloneRootPlaceholder { get; private set; } = string.Empty;

    /// <summary>Sentinel item in the font-family dropdown (#40) that switches to a free-text box for any font not in the curated list.</summary>
    public const string CustomFontChoice = "Custom…";

    /// <summary>Curated monospace font choices offered by the Options dialog's Terminal font-family dropdown; any font not listed is reachable via <see cref="CustomFontChoice"/>.</summary>
    public IReadOnlyList<string> TerminalFontFamilies { get; } =
    [
        "Cascadia Mono, Consolas, monospace",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "DejaVu Sans Mono",
        "Courier New",
    ];

    /// <summary>Items for the Options font-family dropdown (#40): the curated families plus the "Custom…" sentinel.</summary>
    public IReadOnlyList<string> TerminalFontChoices => [.. TerminalFontFamilies, CustomFontChoice];

    // ── AC-67: macOS render-backend selector (Auto / Metal / OpenGL / Software) ──────────────────────────────
    private readonly IRenderingSettingsStore? _renderingSettingsStore;

    /// <summary>The backend the app actually started on (what it is rendering with now), so a save can tell whether
    /// the operator's choice differs and a restart is needed. Fixed for the session — only a restart re-reads it.</summary>
    private RenderBackendChoice _startupRenderBackend = RenderBackendChoice.Auto;

    /// <summary>Selected item in the Options render-backend dropdown (AC-67): Auto / Metal / OpenGL / Software.</summary>
    [ObservableProperty]
    private string _renderBackendSelection = "Auto";

    /// <summary>True once a saved render-backend choice differs from what this process started on — reveals "Restart now".</summary>
    [ObservableProperty]
    private bool _renderBackendNeedsRestart;

    [ObservableProperty]
    private string _renderingSettingsStatus = string.Empty;

    /// <summary>The render-backend choices offered by the Options dropdown.</summary>
    public IReadOnlyList<string> RenderBackendChoices { get; } = ["Auto", "Metal", "OpenGL", "Software"];

    /// <summary>True on macOS, where the render backend is a real choice; gates the setting's visibility.</summary>
    public bool IsMacOsPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>Whether to show the render-backend setting (AC-67): where it does something — macOS — plus in any dev
    /// build, so it can be verified on a Windows/Linux dev machine even though it is inert there for release users.</summary>
    public bool ShowRenderBackendSetting => IsMacOsPlatform || CockpitBuild.IsDevelopment;

    private static string RenderBackendLabel(RenderBackendChoice choice) => choice switch
    {
        RenderBackendChoice.Metal => "Metal",
        RenderBackendChoice.OpenGl => "OpenGL",
        RenderBackendChoice.Software => "Software",
        _ => "Auto",
    };

    private static RenderBackendChoice RenderBackendFromLabel(string label) => label switch
    {
        "Metal" => RenderBackendChoice.Metal,
        "OpenGL" => RenderBackendChoice.OpenGl,
        "Software" => RenderBackendChoice.Software,
        _ => RenderBackendChoice.Auto,
    };

    private async Task LoadRenderingSettingsAsync()
    {
        if (_renderingSettingsStore is null)
        {
            return;
        }

        var settings = await _renderingSettingsStore.LoadAsync();
        _startupRenderBackend = settings.Backend;
        RenderBackendSelection = RenderBackendLabel(settings.Backend);
        RenderBackendNeedsRestart = false;
    }

    /// <summary>Persists the render-backend choice (AC-67). Avalonia fixes the backend once at startup, so a save that
    /// changes it from what this process started on flags <see cref="RenderBackendNeedsRestart"/> to offer a restart.</summary>
    [RelayCommand]
    private async Task SaveRenderingSettingsAsync()
    {
        if (_renderingSettingsStore is null)
        {
            return;
        }

        var choice = RenderBackendFromLabel(RenderBackendSelection);
        await _renderingSettingsStore.SaveAsync(new RenderingSettings { Backend = choice });
        RenderBackendNeedsRestart = choice != _startupRenderBackend;
        RenderingSettingsStatus = "Saved";
    }
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The default-shell choices for the Options terminal picker (#AC-25): an "OS default" entry first, then every
    /// shell <see cref="ShellCatalog"/> detected on this machine. Rebuilt on load so it reflects what is installed.
    /// </summary>
    public ObservableCollection<TerminalShellChoice> TerminalShellChoices { get; } = [];

    /// <summary>
    /// The chosen default shell a new terminal opens (#AC-25). Its <see cref="TerminalShellChoice.Value"/> is
    /// persisted to <see cref="Cockpit.Core.Terminal.TerminalSettings.Shell"/> on save; "OS default" persists blank,
    /// "Custom…" persists whatever the operator typed in <see cref="TerminalCustomShell"/>.
    /// </summary>
    [ObservableProperty]
    private TerminalShellChoice? _selectedTerminalShell;

    /// <summary>True when the shell picker is on "Custom…" (#AC-25), revealing the free-text box for a third-party shell path/command.</summary>
    [ObservableProperty]
    private bool _isTerminalShellCustom;

    /// <summary>Free-text shell path or command entered when the picker is on "Custom…" (#AC-25) — e.g. <c>/usr/bin/fish</c>, <c>nu</c>, common on Linux/macOS. Resolved via <see cref="ShellCatalog.ForCommand"/> at launch.</summary>
    [ObservableProperty]
    private string _terminalCustomShell = string.Empty;

    /// <summary>Sentinel <see cref="TerminalShellChoice.Value"/> for the "Custom…" entry that reveals the free-text shell box; any shell not in the detected list is reachable through it.</summary>
    public const string CustomShellChoiceValue = "custom";

    /// <summary>Reveals the custom-shell box when the picker is on "Custom…" (#AC-25), mirroring the font-family "Custom…" pattern.</summary>
    partial void OnSelectedTerminalShellChanged(TerminalShellChoice? value) =>
        IsTerminalShellCustom = value is not null && value.Value == CustomShellChoiceValue;

    /// <summary>Maps the dropdown selection to the effective font family (#40): "Custom…" reveals the free-text box and uses its value, any other choice is used directly.</summary>
    partial void OnTerminalFontSelectionChanged(string value)
    {
        if (value == CustomFontChoice)
        {
            IsTerminalFontCustom = true;
            if (!string.IsNullOrWhiteSpace(TerminalCustomFontFamily))
            {
                TerminalFontFamily = TerminalCustomFontFamily;
            }
        }
        else
        {
            IsTerminalFontCustom = false;
            TerminalFontFamily = value;
        }
    }

    /// <summary>While the dropdown is on "Custom…" (#40), keeps the effective font family in sync with the free-text box.</summary>
    partial void OnTerminalCustomFontFamilyChanged(string value)
    {
        if (IsTerminalFontCustom && !string.IsNullOrWhiteSpace(value))
        {
            TerminalFontFamily = value;
        }
    }

    /// <summary>Aligns the dropdown/custom-box state with the effective <see cref="TerminalFontFamily"/> (#40) — used after loading from the store so a saved custom font reopens in the "Custom…" state.</summary>
    private void SyncTerminalFontSelectionFromFamily()
    {
        if (TerminalFontFamilies.Contains(TerminalFontFamily))
        {
            IsTerminalFontCustom = false;
            TerminalCustomFontFamily = string.Empty;
            TerminalFontSelection = TerminalFontFamily;
        }
        else
        {
            TerminalCustomFontFamily = TerminalFontFamily;
            IsTerminalFontCustom = true;
            TerminalFontSelection = CustomFontChoice;
        }
    }

    /// <summary>Pushes the terminal font family to every open TTY session as it changes (#40), so Options → Terminal applies live without a restart.</summary>
    partial void OnTerminalFontFamilyChanged(string value)
    {
        foreach (var session in Sessions)
        {
            if (session is TtyViewModel tty)
            {
                tty.TerminalFontFamily = value;
            }
        }
    }

    /// <summary>Pushes the terminal font size to every open TTY session as it changes (#40), same live-apply as <see cref="OnTerminalFontFamilyChanged"/>.</summary>
    partial void OnTerminalFontSizeChanged(int value)
    {
        foreach (var session in Sessions)
        {
            if (session is TtyViewModel tty)
            {
                tty.TerminalFontSize = value;
            }
        }
    }

    partial void OnGlobalStackSessionsVerticallyChanged(bool value) => _OnEffectiveLayoutChanged();

    partial void OnGlobalSingleSessionLayoutChanged(bool value) => _OnEffectiveLayoutChanged();

    /// <summary>
    /// Re-reads what the active desk is doing and pushes it everywhere. One place, because the effective value
    /// moves for three different reasons — Options changed, this workspace's override changed, or a different
    /// workspace became active — and every one of them has to re-dock the TTY headers (#54) and re-lay the grid.
    /// </summary>
    internal void _OnEffectiveLayoutChanged()
    {
        OnPropertyChanged(nameof(SingleSessionLayout));
        OnPropertyChanged(nameof(StackSessionsVertically));
        OnPropertyChanged(nameof(WorkspaceFollowsGlobalLayout));
        OnPropertyChanged(nameof(WorkspaceSingleSessionLayout));
        OnPropertyChanged(nameof(WorkspaceStackSessionsVertically));
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(ShowZoomButton));
        OnPropertyChanged(nameof(StackSessionsInStack));

        foreach (var session in Sessions)
        {
            if (session is TtyViewModel tty)
            {
                tty.IsVerticalLayout = StackSessionsVertically;
            }
        }

        RefreshPaneVisibility();
    }

    /// <summary>True when only the selected session should be shown full-size — either the persisted single layout (#24) or a transient Zoom.</summary>
    public bool ShowSinglePane => SingleSessionLayout || IsZoomed;

    partial void OnIsZoomedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(StackSessionsInStack));
        RefreshPaneVisibility();
    }


    [ObservableProperty]
    private string _audioStatus = "Ready.";

    /// <summary>Whether a local OS toast is shown when a session needs attention while you are present (independent of Discord).</summary>
    [ObservableProperty]
    private bool _localNotificationsEnabled = true;

    /// <summary>Whether the Discord webhook is POSTed when a session needs attention while you are away (independent of local toasts).</summary>
    [ObservableProperty]
    private bool _discordNotificationsEnabled;

    /// <summary>Discord webhook URL POSTed to when the operator is away. Empty disables the away channel.</summary>
    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    /// <summary>Idle minutes before the operator counts as "away" (when the PC is not locked).</summary>
    [ObservableProperty]
    private int _idleThresholdMinutes = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    /// <summary>Minutes a finished session stays "done" before it falls back to idle. 0 leaves it on "done" forever. Distinct from <see cref="IdleThresholdMinutes"/>, which is about the operator being away.</summary>
    [ObservableProperty]
    private int _sessionIdleMinutes = (int)SessionIdleDecision.DefaultIdleThreshold.TotalMinutes;

    /// <summary>Whether a session that finished its turn announces itself when the operator is not watching it.</summary>
    [ObservableProperty]
    private bool _notifyOnSessionFinished = true;

    /// <summary>Whether a session announces that it has gone idle.</summary>
    [ObservableProperty]
    private bool _notifyOnSessionIdle;

    /// <summary>Whether one message is sent when the last session goes idle — nothing is running any more.</summary>
    [ObservableProperty]
    private bool _notifyWhenAllSessionsIdle;

    /// <summary>
    /// Whether the cockpit window is the focused one. Set by the window itself (it is the only thing that knows),
    /// and read by the finished-session notification: a session you are looking at does not need to announce itself.
    /// </summary>
    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private string _notificationSettingsStatus = string.Empty;

    /// <summary>One shared "Saved" indicator for the Options dialog's single footer Save (#13), shown next to the Save button instead of a per-section label.</summary>
    [ObservableProperty]
    private string _allSettingsStatus = string.Empty;

    [ObservableProperty]
    private string _shortcutSettingsStatus = string.Empty;

    /// <summary>When true, every transcript row shows its arrival timestamp (T7). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _showTimestamps;

    [ObservableProperty]
    private string _transcriptDisplaySettingsStatus = string.Empty;

    /// <summary>Which metrics the header's usage pill shows (AC-105), as four toggles composed into the saved field list. Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _showUsagePillContext = true;

    [ObservableProperty]
    private bool _showUsagePillSessionUsage;

    [ObservableProperty]
    private bool _showUsagePillFiveHour;

    [ObservableProperty]
    private bool _showUsagePillWeekly;

    [ObservableProperty]
    private string _usagePillSettingsStatus = string.Empty;

    /// <summary>When true, sending "exit" closes the session after its turn completes (T10). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

    /// <summary>When true, messages queued mid-turn are sent together as one follow-up turn instead of one-per-turn (AC-145). Applied to all open SDK/chat sessions.</summary>
    [ObservableProperty]
    private bool _combineQueuedMessages;

    [ObservableProperty]
    private string _sessionBehaviorSettingsStatus = string.Empty;

    /// <summary>Master switch for voice input (push-to-talk dictation). Off by default — enabling it is what triggers the first Whisper model download.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCalibration))]
    private bool _voiceEnabled;

    private readonly ITranscriptionAdvisor? _transcriptionAdvisor;

    /// <summary>Effective ggml model name fed to the speech-to-text service, e.g. "large-v3-turbo", "small", "tiny".
    /// Driven by the Options dropdown (<see cref="SelectedTranscriptionModel"/>): a curated model sets it directly,
    /// the "Custom…" choice mirrors <see cref="VoiceCustomModelName"/>. Smaller models download and transcribe faster.</summary>
    [ObservableProperty]
    private string _voiceModelName = "large-v3-turbo";

    /// <summary>Sentinel item in the transcription-model dropdown (AC-68) that reveals a free-text box for any ggml
    /// name not in the curated list — quantized variants like <c>large-v3-turbo-q5_0</c>, or a model added later.</summary>
    public const string CustomModelChoice = "Custom…";

    /// <summary>Curated Whisper models offered by the Options → Voice → Transcribe dropdown (AC-68), each with a short
    /// accuracy-vs-load hint. Prefixed at runtime with an "Auto ★" recommendation and suffixed with <see cref="CustomModelChoice"/>.</summary>
    private static readonly IReadOnlyList<TranscriptionModelOption> _curatedModels =
    [
        new("large-v3-turbo", "most accurate · heaviest"),
        new("medium", "≈1pt less accurate on NL · lighter"),
        new("small", "fast · light"),
        new("base", "faster · less accurate"),
        new("tiny", "fastest · least accurate"),
        new(CustomModelChoice, "enter any ggml name", IsCustom: true),
    ];

    /// <summary>Items for the model dropdown (AC-68): an "Auto ★" recommendation (when an advisor is present), then the
    /// curated models, then "Custom…". Built once at construction — the recommendation is fixed for the session.</summary>
    public ObservableCollection<TranscriptionModelOption> TranscriptionModelChoices { get; } = new();

    /// <summary>The per-machine recommendation (AC-68 slice 2); null in the design-time/test graph with no advisor.</summary>
    private TranscriptionRecommendation? _transcriptionRecommendation;

    /// <summary>Whether the model dropdown is on the "Auto ★" item — persisted as <see cref="Cockpit.Core.Voice.VoiceSettings.ModelAutoSelected"/>.</summary>
    private bool _transcriptionModelAuto;

    /// <summary>Selected item in the transcription-model dropdown (AC-68) — the "Auto ★" recommendation, a curated
    /// model, or the "Custom…" sentinel. Drives <see cref="VoiceModelName"/> and toggles <see cref="IsTranscriptionModelCustom"/>.</summary>
    [ObservableProperty]
    private TranscriptionModelOption? _selectedTranscriptionModel;

    /// <summary>True when the model dropdown is on "Custom…" (AC-68), revealing the free-text box bound to <see cref="VoiceCustomModelName"/>.</summary>
    [ObservableProperty]
    private bool _isTranscriptionModelCustom;

    /// <summary>Free-text ggml model entered when the dropdown is on "Custom…" (AC-68); mirrored into <see cref="VoiceModelName"/> while custom is active.</summary>
    [ObservableProperty]
    private string _voiceCustomModelName = string.Empty;

    /// <summary>Host-aware Whisper backend choices offered by the Options → Voice → Transcribe combo box (AC-68).
    /// Built from <see cref="ITranscriptionAdvisor"/>: always Auto and CPU, plus a single GPU option only when a GPU
    /// runtime actually loads here — so a non-NVIDIA machine is never offered CUDA.</summary>
    public ObservableCollection<VoiceBackendPreferenceOption> VoiceBackendPreferences { get; } = new();

    [ObservableProperty]
    private VoiceBackendPreferenceOption _selectedVoiceBackendPreference = new("Auto (recommended)", VoiceBackendPreference.Auto);

    /// <summary>One-line explanation of what the chosen transcription backend does on this machine (AC-68); recomputed
    /// when the selection changes. Slice 2 makes the Auto recommendation hardware-aware and richer.</summary>
    [ObservableProperty]
    private string _transcriptionAdvice = string.Empty;

    /// <summary>A short badge line describing the detected transcription hardware (AC-68), e.g. "Vulkan GPU available"
    /// or "No GPU acceleration detected — CPU only". Slice 2 adds GPU brand and display-adapter facts.</summary>
    [ObservableProperty]
    private string _transcriptionHardware = string.Empty;

    /// <summary>Builds the host-aware backend list and the initial model/advice state (AC-68). Called from both
    /// constructors; without an advisor (design-time/tests) it offers Auto + CPU only.</summary>
    private void _InitVoiceTranscriptionOptions()
    {
        var capabilities = _transcriptionAdvisor?.DetectCapabilities() ?? TranscriptionCapabilities.CpuOnly;
        _transcriptionRecommendation = _transcriptionAdvisor?.Recommend();

        // Model dropdown: the "Auto ★" recommendation first (only when we have an advisor to recommend), then the
        // curated models and Custom…. The Auto item carries the recommended model as its hint.
        TranscriptionModelChoices.Clear();
        if (_transcriptionRecommendation is { } recommendation)
        {
            TranscriptionModelChoices.Add(new("★ Auto — recommended", recommendation.Model, IsAuto: true));
        }

        foreach (var model in _curatedModels)
        {
            TranscriptionModelChoices.Add(model);
        }

        // Backend dropdown: host-aware Auto / GPU / CPU.
        VoiceBackendPreferences.Clear();
        foreach (var choice in TranscriptionOptions.BackendChoices(capabilities))
        {
            VoiceBackendPreferences.Add(choice);
        }

        SelectedVoiceBackendPreference = VoiceBackendPreferences[0];

        // Badges: the recommendation's (brand · drives display · CUDA state) when we have one, else the plain line.
        TranscriptionHardware = _transcriptionRecommendation is { Badges.Count: > 0 } withBadges
            ? string.Join(" · ", withBadges.Badges)
            : TranscriptionOptions.HardwareBadge(capabilities);

        _SyncTranscriptionModelFromName();
        _UpdateTranscriptionAdvice();
    }

    /// <summary>Recomputes the one-line advice (AC-68). For "Auto" the recommendation's reason is the richest
    /// explanation (why CPU on a single GPU that draws the screen); an explicit CPU/GPU choice gets the generic note.</summary>
    private void _UpdateTranscriptionAdvice()
    {
        if (SelectedVoiceBackendPreference.Value is VoiceBackendPreference.Auto && _transcriptionRecommendation is { } recommendation)
        {
            TranscriptionAdvice = recommendation.Reason;
            return;
        }

        var capabilities = _transcriptionAdvisor?.DetectCapabilities() ?? TranscriptionCapabilities.CpuOnly;
        TranscriptionAdvice = TranscriptionOptions.Advice(SelectedVoiceBackendPreference.Value, capabilities);
    }

    partial void OnSelectedVoiceBackendPreferenceChanged(VoiceBackendPreferenceOption value) => _UpdateTranscriptionAdvice();

    /// <summary>Maps the dropdown selection to the effective model (AC-68): "Custom…" reveals the free-text box and
    /// uses its value, any curated model is used directly.</summary>
    partial void OnSelectedTranscriptionModelChanged(TranscriptionModelOption? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.IsAuto)
        {
            _transcriptionModelAuto = true;
            IsTranscriptionModelCustom = false;
            // Resolve the Auto item to the concrete recommended model, so the speech-to-text service reads a real name.
            if (_transcriptionRecommendation is { } recommendation)
            {
                VoiceModelName = recommendation.Model;
            }

            return;
        }

        _transcriptionModelAuto = false;
        if (value.IsCustom)
        {
            IsTranscriptionModelCustom = true;
            if (!string.IsNullOrWhiteSpace(VoiceCustomModelName))
            {
                VoiceModelName = VoiceCustomModelName.Trim();
            }
        }
        else
        {
            IsTranscriptionModelCustom = false;
            VoiceModelName = value.Name;
        }
    }

    /// <summary>While the model dropdown is on "Custom…" (AC-68), keeps the effective model in sync with the box.</summary>
    partial void OnVoiceCustomModelNameChanged(string value)
    {
        if (IsTranscriptionModelCustom && !string.IsNullOrWhiteSpace(value))
        {
            VoiceModelName = value.Trim();
        }
    }

    /// <summary>Aligns the model dropdown/custom-box with the effective <see cref="VoiceModelName"/> (AC-68) — used
    /// after loading so a saved custom model reopens in the "Custom…" state, and a preset reopens selected.</summary>
    private void _SyncTranscriptionModelFromName()
    {
        // Auto ★ when the operator chose it and there is a recommendation item to point at.
        if (_transcriptionModelAuto && TranscriptionModelChoices.FirstOrDefault(model => model.IsAuto) is { } auto)
        {
            IsTranscriptionModelCustom = false;
            VoiceCustomModelName = string.Empty;
            SelectedTranscriptionModel = auto;
            return;
        }

        var preset = TranscriptionModelChoices.FirstOrDefault(model => !model.IsAuto && !model.IsCustom && model.Name == VoiceModelName);
        if (preset is not null)
        {
            IsTranscriptionModelCustom = false;
            VoiceCustomModelName = string.Empty;
            SelectedTranscriptionModel = preset;
        }
        else
        {
            VoiceCustomModelName = VoiceModelName;
            IsTranscriptionModelCustom = true;
            SelectedTranscriptionModel = TranscriptionModelChoices.First(model => model.IsCustom);
        }
    }

    // ── AC-68: first-use calibration — measures every usable backend, one child process each ─────────────────
    private readonly ITranscriptionCalibrator? _transcriptionCalibrator;
    private readonly ITranscriptionCalibrationStore? _transcriptionCalibrationStore;
    private TranscriptionCalibration? _transcriptionCalibration;
    private CancellationTokenSource? _calibrationCts;

    /// <summary>True while a calibration runs — shows the overlay and disables Run (AC-68).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCalibration))]
    private bool _isCalibrating;

    /// <summary>The current step's text ("CPU: measuring… (2/3)", a result note, or an error) (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationStatus = string.Empty;

    /// <summary>0..100 for the overlay bar while a step reports a real fraction (a model download); else indeterminate.</summary>
    [ObservableProperty]
    private double _calibrationProgressValue;

    /// <summary>True when the current step has no honest percentage (loading, warming up, measuring) — the bar spins.</summary>
    [ObservableProperty]
    private bool _calibrationProgressIndeterminate = true;

    /// <summary>Whether measured results exist to show the comparison bars and verdict (AC-68).</summary>
    [ObservableProperty]
    private bool _hasCalibration;

    /// <summary>One row per measured backend (CPU, GPU), fastest first — the comparison bars.</summary>
    public ObservableCollection<CalibrationResultRow> CalibrationResults { get; } = [];

    /// <summary>Full-scale (ms) for the speed bars: the slowest backend, so the bars read relative to each other.</summary>
    [ObservableProperty]
    private double _calibrationSpeedMaxMs = 1;

    /// <summary>Full-scale (ms) for the hitch bars, floored so a smooth result still shows a short bar.</summary>
    [ObservableProperty]
    private double _calibrationHitchMaxMs = 32;

    /// <summary>Which backend Auto runs on, in words ("Auto runs on GPU (Vulkan)") — so the resolved choice is visible (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationChosenText = string.Empty;

    /// <summary>The model the backend comparison was timed with, so those numbers are read against a known model (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationModelText = string.Empty;

    /// <summary>Per-model measured rows on the chosen backend (AC-68) — the accuracy-vs-speed table.</summary>
    public ObservableCollection<CalibrationModelRow> CalibrationModelResults { get; } = [];

    /// <summary>Full-scale (ms) for the model bars: the slowest measured model.</summary>
    [ObservableProperty]
    private double _calibrationModelMaxMs = 1;

    /// <summary>The model the verdict suggests, in words ("Suggested model: small") (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationModelRecommendation = string.Empty;

    /// <summary>Why that model is suggested (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationModelAdvice = string.Empty;

    /// <summary>Whether a measured model ladder exists to show its table (AC-68).</summary>
    [ObservableProperty]
    private bool _hasModelLadder;

    /// <summary>The verdict's one-line reasoning (AC-68).</summary>
    [ObservableProperty]
    private string _calibrationRationale = string.Empty;

    /// <summary>Calibration needs the model, so it can run only when voice is on and a calibrator is present (AC-68).</summary>
    public bool CanRunCalibration => _transcriptionCalibrator is not null && VoiceEnabled && !IsCalibrating;

    /// <summary>Whether the "Run calibration" affordance is offered at all — only in a graph that has a calibrator.</summary>
    public bool ShowCalibration => _transcriptionCalibrator is not null;

    /// <summary>
    /// Measures every backend this machine can use — the CPU and, if a GPU runtime loads, the GPU — each in its own
    /// child process, then picks one with a CPU preference and remembers it (AC-68). A failed measurement is reported
    /// on the status line, never thrown into the dialog.
    /// </summary>
    [RelayCommand]
    private async Task RunCalibrationAsync()
    {
        if (_transcriptionCalibrator is null || IsCalibrating)
        {
            return;
        }

        IsCalibrating = true;
        CalibrationStatus = "Starting…";
        CalibrationProgressIndeterminate = true;
        _calibrationCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CalibrationProgress>(step =>
            {
                CalibrationStatus = step.Message;
                if (step.Fraction is { } fraction)
                {
                    CalibrationProgressIndeterminate = false;
                    CalibrationProgressValue = Math.Clamp(fraction * 100, 0, 100);
                }
                else
                {
                    CalibrationProgressIndeterminate = true;
                }
            });
            _ApplyCalibration(await _transcriptionCalibrator.MeasureAsync(progress, _calibrationCts.Token));
            CalibrationStatus = "Measured";
        }
        catch (OperationCanceledException)
        {
            CalibrationStatus = "Calibration cancelled.";
        }
        catch (Exception)
        {
            // A calibration is a nice-to-have; a model that would not load must not crash Options.
            CalibrationStatus = "Calibration could not run — check that voice works first.";
        }
        finally
        {
            _calibrationCts.Dispose();
            _calibrationCts = null;
            IsCalibrating = false;
        }
    }

    /// <summary>Cancels a running calibration — the blocking overlay's escape hatch, so a wedged child (a stalled
    /// download, a native load that hangs) can never trap the operator behind it (AC-68).</summary>
    [RelayCommand]
    private void CancelCalibration() => _calibrationCts?.Cancel();

    private void _ApplyCalibration(TranscriptionCalibration calibration)
    {
        _transcriptionCalibration = calibration;
        CalibrationResults.Clear();

        if (calibration.Measurements.Count == 0)
        {
            HasCalibration = false;

            return;
        }

        CalibrationSpeedMaxMs = Math.Max(1, calibration.Measurements.Max(measurement => measurement.LatencyMs));
        CalibrationHitchMaxMs = Math.Max(32, calibration.Measurements.Max(measurement => measurement.HitchMs));

        foreach (var measurement in calibration.Measurements.OrderBy(measurement => measurement.LatencyMs))
        {
            CalibrationResults.Add(new CalibrationResultRow(
                _BackendLabel(measurement.Backend),
                measurement.LatencyMs,
                measurement.HitchMs,
                $"{(measurement.LatencyMs / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s",
                $"{measurement.HitchMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms",
                measurement.Backend == calibration.ChosenBackend,
                TranscriptionCalibrationReport.IsSmooth(measurement)));
        }

        CalibrationChosenText = $"Auto runs on {_BackendLabel(calibration.ChosenBackend)}";
        CalibrationModelText = $"Backend timings measured with model: {calibration.Model}";
        CalibrationRationale = TranscriptionCalibrationReport.Rationale(calibration);
        _ApplyModelLadder(calibration);
        HasCalibration = true;
    }

    private void _ApplyModelLadder(TranscriptionCalibration calibration)
    {
        CalibrationModelResults.Clear();

        if (calibration.ModelLadder.Count == 0)
        {
            HasModelLadder = false;

            return;
        }

        CalibrationModelMaxMs = Math.Max(1, calibration.ModelLadder.Max(measurement => measurement.LatencyMs));

        foreach (var measurement in calibration.ModelLadder.OrderBy(measurement => measurement.LatencyMs))
        {
            CalibrationModelResults.Add(new CalibrationModelRow(
                measurement.Model,
                measurement.LatencyMs,
                $"{(measurement.LatencyMs / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s",
                string.Equals(measurement.Model, calibration.RecommendedModel, StringComparison.OrdinalIgnoreCase),
                string.Equals(measurement.Model, calibration.Model, StringComparison.OrdinalIgnoreCase)));
        }

        CalibrationModelRecommendation = $"Suggested model on {_BackendLabel(calibration.ChosenBackend)}: {calibration.RecommendedModel}";
        CalibrationModelAdvice = TranscriptionCalibrationReport.RecommendModel(calibration.ModelLadder).Rationale;
        HasModelLadder = true;
    }

    private static string _BackendLabel(VoiceBackendPreference backend) => backend switch
    {
        VoiceBackendPreference.Vulkan => "GPU (Vulkan)",
        VoiceBackendPreference.Cuda => "GPU (CUDA)",
        VoiceBackendPreference.Cpu => "CPU",
        _ => backend.ToString(),
    };

    /// <summary>Selectable dictation languages for speech-to-text — "Auto-detect" plus common fixed languages. A fixed language beats detection when you always dictate in one tongue (Options flyout combo).</summary>
    public IReadOnlyList<SttLanguageOption> SttLanguages { get; } =
    [
        new("Auto-detect", "auto"),
        new("Dutch", "nl"),
        new("English", "en"),
        new("German", "de"),
        new("French", "fr"),
        new("Spanish", "es"),
    ];

    [ObservableProperty]
    private SttLanguageOption _selectedSttLanguage = new("Auto-detect", "auto");

    /// <summary>Input (microphone) devices offered by the Options combo box; the first entry is the system default. Refreshed from the audio backend when the voice settings load.</summary>
    public ObservableCollection<AudioDeviceOption> InputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedInputDevice = new("System default", null);

    /// <summary>Output (playback) devices for read-aloud (#35); the first entry is the system default.</summary>
    public ObservableCollection<AudioDeviceOption> OutputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedOutputDevice = new("System default", null);

    /// <summary>Whether a transcript is passed through the local Ollama cleanup step before injection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalLlmServerPicker))]
    [NotifyPropertyChangedFor(nameof(ShowManualLlmFields))]
    [NotifyPropertyChangedFor(nameof(ShowLlmModelPicker))]
    [NotifyPropertyChangedFor(nameof(ShowAutoLlmSummary))]
    private bool _voiceCleanupEnabled = true;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoDetectLocalLlm"/>: auto-detect the running Ollama/LM Studio server and its model. On by default; when off, the server is set by hand below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalLlmServerPicker))]
    [NotifyPropertyChangedFor(nameof(ShowManualLlmFields))]
    [NotifyPropertyChangedFor(nameof(ShowLlmModelPicker))]
    [NotifyPropertyChangedFor(nameof(ShowAutoLlmSummary))]
    private bool _voiceAutoDetectLocalLlm = true;

    // Re-resolve the "auto will use…" summary when the operator flips auto-detect, so the line reflects the change.
    partial void OnVoiceAutoDetectLocalLlmChanged(bool value)
    {
        if (!_suppressVoiceLlmHooks)
        {
            _ = _RefreshVoiceLlmAsync();
        }
    }

    /// <summary>Which detected server auto-detect prefers when both are running (Options combo box).</summary>
    public IReadOnlyList<LocalLlmPreferenceOption> LocalLlmPreferences { get; } =
    [
        new("Auto-detect", LocalLlmPreference.Auto),
        new("Ollama", LocalLlmPreference.Ollama),
        new("LM Studio", LocalLlmPreference.LmStudio),
    ];

    [ObservableProperty]
    private LocalLlmPreferenceOption _selectedLocalLlmPreference = new("Auto-detect", LocalLlmPreference.Auto);

    // A different preferred server can resolve to a different server + model list, so re-resolve and re-list.
    partial void OnSelectedLocalLlmPreferenceChanged(LocalLlmPreferenceOption value)
    {
        if (!_suppressVoiceLlmHooks)
        {
            _ = _RefreshVoiceLlmAsync();
        }
    }

    /// <summary>The server-preference combo box is only meaningful while cleanup is on and auto-detect is choosing the server.</summary>
    public bool ShowLocalLlmServerPicker => VoiceCleanupEnabled && VoiceAutoDetectLocalLlm;

    /// <summary>The model picker is shown whenever cleanup is on: it is the exact model in manual mode, and the preferred/override model auto-detect uses first when present in auto mode.</summary>
    public bool ShowLlmModelPicker => VoiceCleanupEnabled;

    /// <summary>The "auto will use…" summary + the pick-rule hint are shown only while auto-detect is deciding the server/model, so the operator can see what it resolves to.</summary>
    public bool ShowAutoLlmSummary => VoiceCleanupEnabled && VoiceAutoDetectLocalLlm;

    /// <summary>The manual server URL is shown only when cleanup is on and auto-detect is off — otherwise Cockpit picks the server and showing a URL field would contradict it.</summary>
    public bool ShowManualLlmFields => VoiceCleanupEnabled && !VoiceAutoDetectLocalLlm;

    /// <summary>What auto-detect resolves to right now — e.g. "Auto-detected LM Studio → phi-3-mini-4k-instruct" — so the chosen server/model is visible rather than hidden. Refreshed on dialog open and when the auto-detect toggle, server preference or preferred model change.</summary>
    [ObservableProperty]
    private string _voiceLlmAutoSummary = string.Empty;

    /// <summary>The first model-dropdown entry: "Auto" means no explicit choice — auto-detect (or the server list) decides, and the summary line shows what it landed on. Stored as an empty model id.</summary>
    private const string AutoModel = "Auto";

    /// <summary>Models offered by the dropdown — always "Auto" first, then the advised models and whatever the server reports, so it is never empty. Refreshed when the Options dialog opens.</summary>
    public ObservableCollection<string> VoiceLlmModels { get; } = [];

    /// <summary>Selected model for the shared voice-LLM step (STT cleanup + read-aloud). "Auto" (the default) lets auto-detect choose; otherwise it is the preferred/exact model. Persisted as an empty id when "Auto".</summary>
    [ObservableProperty]
    private string _voiceLlmModel = AutoModel;

    // The preferred model steers what auto-detect picks (it is used first when the server has it), so refresh the
    // summary — but not the list — when it changes, to avoid disturbing the dropdown the operator is using.
    partial void OnVoiceLlmModelChanged(string value)
    {
        if (!_suppressVoiceLlmHooks)
        {
            _ = _RefreshVoiceLlmSummaryAsync();
        }
    }

    /// <summary>Base URL of the local OpenAI-compatible LLM server (Ollama/LM Studio) used by the shared voice-LLM step, without the <c>/v1</c> suffix.</summary>
    [ObservableProperty]
    private string _voiceLlmBaseUrl = "http://localhost:11434";

    /// <summary>Avalonia <c>Key</c> enum name for the push-to-talk hotkey, e.g. "F9".</summary>
    [ObservableProperty]
    private string _voicePushToTalkKeyName = "F9";

    /// <summary>
    /// When true, the push-to-talk hotkey also fires while the cockpit window has no focus (#34), via
    /// <c>VoicePushToTalkCoordinator</c>. Off by default — opt-in like voice itself.
    /// </summary>
    [ObservableProperty]
    private bool _voiceGlobalPushToTalk;

    /// <summary>
    /// Shown next to global push-to-talk on Linux once the operator has saved a change to it (#34): there the
    /// hotkey is a desktop-portal binding the compositor only picks up at startup, so unlike on Windows — where
    /// <c>VoicePushToTalkCoordinator</c> re-arms it live — the change takes effect only after a restart. The label
    /// says so; this drives the "Restart now" button beside it.
    /// </summary>
    [ObservableProperty]
    private bool _voiceGlobalPushToTalkNeedsRestart;

    /// <summary>The global push-to-talk value this process actually armed with at startup — the baseline the save
    /// compares against, so toggling it and back leaves nothing to restart for. Null until first loaded.</summary>
    private bool? _voiceGlobalPushToTalkRunning;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoSubmitAfterVoice"/>. When true a finished transcript is submitted straight after injection instead of waiting for a manual send. Off by default.</summary>
    [ObservableProperty]
    private bool _voiceAutoSubmit;

    /// <summary>
    /// What the global hotkey is really triggered by, in the words of whoever bound it — or why nothing is. Read
    /// back rather than assumed: under Wayland the compositor owns the binding and the key above is a hint it may
    /// ignore, and on macOS there is no implementation at all. Empty while global push-to-talk is off.
    /// </summary>
    [ObservableProperty]
    private string _voiceGlobalHotkeyTrigger = string.Empty;

    /// <summary>
    /// Mirrors <see cref="Cockpit.Core.Screenshots.ScreenshotSettings.GlobalHotkeyEnabled"/> (AC-220): whether the
    /// screenshot key fires while the cockpit has no focus. Off by default — a desktop-wide key is taken from
    /// every other application, so it is the operator's to grant. The composer button works either way.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyConflict))]
    private bool _screenshotGlobalHotkeyEnabled;

    /// <summary>Mirrors <see cref="Cockpit.Core.Screenshots.ScreenshotSettings.HotkeyKeyName"/> — the Avalonia <c>Key</c> name for the screenshot hotkey, e.g. "F8".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyConflict))]
    private string _screenshotHotkeyKeyName = "F8";

    /// <summary>What the screenshot hotkey is really triggered by, in the words of whoever bound it. Read back for the same reason push-to-talk's is; empty while the key is off.</summary>
    [ObservableProperty]
    private string _screenshotHotkeyTrigger = string.Empty;

    /// <summary>
    /// Names two desktop-wide keys that want the same key, or empty when there is no clash (AC-220). Shown live
    /// while the operator is typing a key rather than after saving, since after saving one of the two features
    /// has already silently stopped working — which is the whole failure this exists to prevent.
    /// </summary>
    public string HotkeyConflict =>
        GlobalHotkeyConflictCheck.Describe(_ConfiguredGlobalHotkeys()) ?? string.Empty;

    /// <summary>The bindings as the settings screen currently reads — what would be armed if it were saved now.</summary>
    private IReadOnlyList<GlobalHotkeyBinding> _ConfiguredGlobalHotkeys()
    {
        var bindings = new List<GlobalHotkeyBinding>();
        if (VoiceEnabled && VoiceGlobalPushToTalk)
        {
            bindings.Add(new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", VoicePushToTalkKeyName));
        }

        if (ScreenshotGlobalHotkeyEnabled)
        {
            bindings.Add(new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", ScreenshotHotkeyKeyName));
        }

        return bindings;
    }

    partial void OnVoiceGlobalPushToTalkChanged(bool value) => OnPropertyChanged(nameof(HotkeyConflict));

    partial void OnVoicePushToTalkKeyNameChanged(string value) => OnPropertyChanged(nameof(HotkeyConflict));

    partial void OnVoiceEnabledChanged(bool value) => OnPropertyChanged(nameof(HotkeyConflict));

    private async Task LoadScreenshotSettingsAsync()
    {
        if (_screenshotSettingsStore is null)
        {
            return;
        }

        var settings = await _screenshotSettingsStore.LoadAsync();
        ScreenshotGlobalHotkeyEnabled = settings.GlobalHotkeyEnabled;
        ScreenshotHotkeyKeyName = settings.HotkeyKeyName;
    }

    /// <summary>Persists the screenshot settings edited in Options (AC-220). Re-arming is <c>GlobalHotkeyCoordinator</c>'s, driven by the same saved event push-to-talk uses.</summary>
    [RelayCommand]
    private async Task SaveScreenshotSettingsAsync()
    {
        if (_screenshotSettingsStore is null)
        {
            return;
        }

        await _screenshotSettingsStore.SaveAsync(new ScreenshotSettings
        {
            GlobalHotkeyEnabled = ScreenshotGlobalHotkeyEnabled,
            HotkeyKeyName = string.IsNullOrWhiteSpace(ScreenshotHotkeyKeyName) ? "F8" : ScreenshotHotkeyKeyName.Trim(),
        });
    }

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.StopReadAloudWhenSpeaking"/> (AC-9). Off by default — the threshold cannot tell your voice from the cockpit's own coming out of a speaker.</summary>
    [ObservableProperty]
    private bool _voiceStopReadAloudWhenSpeaking;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.StopReadAloudLevelThreshold"/>. Decimal because that is what NumericUpDown binds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceStopReadAloudThresholdValue))]
    private decimal _voiceStopReadAloudLevelThreshold = 0.15m;

    /// <summary>The barge-in threshold as a 0..1 double, for the <c>MicLevelMeter</c> marker (the setting itself is a decimal so NumericUpDown can bind it).</summary>
    public double VoiceStopReadAloudThresholdValue => (double)VoiceStopReadAloudLevelThreshold;

    /// <summary>Two-way bound to the "Test microphone" toggle; flipping it starts/stops a live level meter for setting the barge-in threshold by eye (AC-9).</summary>
    [ObservableProperty]
    private bool _isTestingMic;

    /// <summary>Live microphone level (0..1 RMS) during the mic test, driving the <c>MicLevelMeter</c> fill.</summary>
    [ObservableProperty]
    private double _micTestLevel;

    // Start/stop the capture from the toggle itself, so the button and the running state can never disagree. A
    // failed start (no capture service) reverts the toggle rather than leaving it stuck on.
    partial void OnIsTestingMicChanged(bool value)
    {
        if (value)
        {
            if (_audioCapture is null)
            {
                IsTestingMic = false;
                return;
            }

            var cts = new CancellationTokenSource();
            _micTestCancellation = cts;
            MicTestLevel = 0;

            // The run loop owns the CTS and disposes it in its own finally — we only Cancel here, never Dispose,
            // so the capture stream can never register a callback on a token source disposed out from under it.
            _ = _RunMicTestAsync(cts);
        }
        else
        {
            _micTestCancellation?.Cancel();
            _micTestCancellation = null;
            MicTestLevel = 0;
        }
    }

    private async Task _RunMicTestAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await foreach (var frame in _audioCapture!.CaptureAsync(new AudioFormat(), cancellation.Token).ConfigureAwait(false))
            {
                var level = AudioLevelMeter.NormalizedRms(frame.Span);
                Dispatcher.UIThread.Post(() => MicTestLevel = level);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: StopMicTest cancels the capture stream.
        }
        catch (Exception)
        {
            // A microphone that will not open should not crash the dialog; the meter simply stays flat.
        }
        finally
        {
            cancellation.Dispose();
            Dispatcher.UIThread.Post(() => MicTestLevel = 0);
        }
    }

    /// <summary>Stops the mic test and releases the microphone. Called from the dialog's close handler so it never stays open.</summary>
    public void StopMicTest()
    {
        if (IsTestingMic)
        {
            IsTestingMic = false;
        }
    }

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.OpenMicSilenceTimeoutMs"/>: trailing silence (ms) that ends an open-mic utterance. Tunable.</summary>
    [ObservableProperty]
    private int _voiceOpenMicSilenceTimeoutMs = 800;

    /// <summary>The open-mic coordinator, wired at startup, exposing the runtime on/off toggle bound to the sidebar mic button (open-mic is turned on/off live, not via a settings checkbox).</summary>
    [ObservableProperty]
    private OpenMicCoordinator? _openMic;

    /// <summary>
    /// The screenshot coordinator, wired at startup (AC-220). Held so every session panel can be handed the
    /// capture its composer button runs, and so a platform that cannot capture at all is said once rather than
    /// discovered per button.
    /// </summary>
    [ObservableProperty]
    private ScreenshotCoordinator? _screenshots;

    partial void OnScreenshotsChanged(ScreenshotCoordinator? value)
    {
        foreach (var session in Sessions)
        {
            _WireScreenshots(session);
        }

        if (value is { } screenshots)
        {
            _ = _RewireScreenshotsWhenSupportSettlesAsync(screenshots);
        }
    }

    /// <summary>
    /// Wires every session again once the platform has finished saying whether it can capture (AC-326). On Linux
    /// that answer is a D-Bus round trip and this property is assigned in the same statement that builds the
    /// coordinator, so the pass above always reads "cannot" — and every session already open at startup would
    /// keep a greyed-out button for the rest of the run.
    /// </summary>
    private async Task _RewireScreenshotsWhenSupportSettlesAsync(ScreenshotCoordinator screenshots)
    {
        await screenshots.SupportSettled.ConfigureAwait(true);

        foreach (var session in Sessions)
        {
            _WireScreenshots(session);
        }
    }

    /// <summary>Hands a session panel the capture behind its composer button — and, where the platform has none, the sentence that says so.</summary>
    private void _WireScreenshots(SessionPanelViewModel session)
    {
        if (Screenshots is not { } screenshots)
        {
            return;
        }

        session.ScreenshotPlatformRefusal = screenshots.IsSupported
            ? null
            : "Screen capture is not available on this platform.";
        session.ScreenshotCapture = panel => screenshots.CaptureIntoAsync(panel);
        session.NotifyScreenshotWiringChanged();
    }

    /// <summary>Read-aloud rendering modes (#35) offered by the Options flyout combo box.</summary>
    public IReadOnlyList<ReadAloudModeOption> ReadAloudModes { get; } =
    [
        new("Verbatim — read the reply as-is", ReadAloudMode.Verbatim),
        new("Naturalized — rewrite into natural speech", ReadAloudMode.Naturalized),
        new("Summarized — speak a short summary", ReadAloudMode.Summarized),
    ];

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.ReadAloudMode"/>: how read-aloud renders a reply before speaking it (#35). Verbatim by default.</summary>
    [ObservableProperty]
    private ReadAloudModeOption _selectedReadAloudMode = new("Verbatim — read the reply as-is", ReadAloudMode.Verbatim);

    /// <summary>Turn-start acknowledgement modes (AC-99) offered by the Options flyout combo box.</summary>
    public IReadOnlyList<TurnAckModeOption> TurnAckModes { get; } =
    [
        new("Off — no acknowledgement", TurnAckMode.Off),
        new("Preset phrases — instant, rotates a short set", TurnAckMode.InstantPhrases),
        new("Local LLM — a contextual line (falls back to a preset)", TurnAckMode.LocalLlm),
    ];

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.TurnAckMode"/>: how a turn-start acknowledgement is produced (AC-99). Preset phrases by default. Only spoken when read-aloud is on.</summary>
    [ObservableProperty]
    private TurnAckModeOption _selectedTurnAckMode = new("Preset phrases — instant, rotates a short set", TurnAckMode.InstantPhrases);

    /// <summary>Selectable read-aloud voices (#35) offered by the Options flyout combo box — SupertonicTTS speaker choices.</summary>
    public IReadOnlyList<TtsVoiceOption> TtsVoices => TtsVoiceCatalog.Voices;

    /// <summary>SupertonicTTS speaker used for read-aloud (#35). One multilingual model voices both languages; the model downloads lazily on first use, the same as the Whisper model.</summary>
    [ObservableProperty]
    private TtsVoiceOption _selectedTtsVoice = TtsVoiceCatalog.Default;

    /// <summary>Preferred read-aloud base language (#35): the voice leans to it and unmarked text speaks in it, keeping foreign terms in their language. English or Dutch — the two the voice handles here.</summary>
    public IReadOnlyList<SttLanguageOption> ReadAloudLanguages { get; } =
    [
        new("English", "en"),
        new("Dutch", "nl"),
    ];

    [ObservableProperty]
    private SttLanguageOption _selectedReadAloudLanguage = new("English", "en");

    /// <summary>Status shown next to the read-aloud Test button — "Preparing…" while a Naturalized/Summarized preview calls the local LLM, then cleared.</summary>
    [ObservableProperty]
    private string _voiceTestStatus = string.Empty;

    /// <summary>
    /// Speaks a short sample through the currently selected voice and mode (#35, AC-21) so the operator can hear how
    /// read-aloud sounds before saving. Naturalized/Summarized run the sample through the local LLM the same way a
    /// real reply would (falling back to the raw sample when it is unavailable); Verbatim reads it as-is. The
    /// Supertonic model downloads on first use, so the first preview can take a few seconds. No-op without a
    /// playback queue (design-time/tests).
    /// </summary>
    [RelayCommand]
    private async Task PreviewReadAloudAsync()
    {
        if (_voicePlaybackQueue is null)
        {
            return;
        }

        // Stop any current playback first (this is a preview the operator triggered), then render + enqueue the
        // sample through the one shared read-aloud path so the Test button can never drift from a real turn.
        _voicePlaybackQueue.StopAll();
        var mode = SelectedReadAloudMode.Value;

        VoiceTestStatus = "Preparing…";
        try
        {
            await ReadAloudPipeline.SpeakAsync(
                _voicePlaybackQueue, _cleanupService, _SampleReadAloudText(mode), mode, SelectedTtsVoice.Sid, SelectedReadAloudLanguage.Code);
        }
        finally
        {
            VoiceTestStatus = string.Empty;
        }
    }

    /// <summary>A representative preview sample: bilingual and, for the two LLM modes, shaped so Naturalized has symbols to phrase and Summarized has details to compress.</summary>
    private static string _SampleReadAloudText(ReadAloudMode mode) => mode switch
    {
        ReadAloudMode.Summarized =>
            "This is a longer sample so you can hear summarizing. Imagine a reply that lists three steps, "
            + "mentions a deadline in five days, and warns about one risk. Summarized keeps the numbers, the "
            + "decision and the warning, but says it in fewer words. En dit werkt ook in het Nederlands.",
        ReadAloudMode.Naturalized =>
            "This is a preview of read-aloud. It can mention a file or a folder path in plain words instead of "
            + "reading the symbols out loud. En het schakelt netjes over naar het Nederlands waar dat nodig is.",
        _ =>
            "This is a preview of read-aloud. The selected voice reads replies out loud, one sentence at a time.",
    };

    [ObservableProperty]
    private string _voiceSettingsStatus = string.Empty;

    /// <summary>
    /// True on Linux, where the physical key for global push-to-talk is bound by the desktop's own
    /// Shortcuts settings rather than configurable in-app (#34) — drives the Options-flyout hint text.
    /// </summary>
    public bool IsLinuxPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Pushes the timestamp toggle to every open session as it changes, so the switch takes effect live.</summary>
    partial void OnShowTimestampsChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.ShowTimestamps = value;
        }
    }

    partial void OnShowUsagePillContextChanged(bool value) => ApplyUsagePillFields();

    partial void OnShowUsagePillSessionUsageChanged(bool value) => ApplyUsagePillFields();

    partial void OnShowUsagePillFiveHourChanged(bool value) => ApplyUsagePillFields();

    partial void OnShowUsagePillWeeklyChanged(bool value) => ApplyUsagePillFields();

    /// <summary>The chosen usage-pill fields in display order, composed from the four toggles.</summary>
    private IReadOnlyList<UsagePillField> ComposeUsagePillFields()
    {
        var fields = new List<UsagePillField>();
        if (ShowUsagePillContext)
        {
            fields.Add(UsagePillField.Context);
        }

        if (ShowUsagePillSessionUsage)
        {
            fields.Add(UsagePillField.SessionUsage);
        }

        if (ShowUsagePillFiveHour)
        {
            fields.Add(UsagePillField.FiveHourWindow);
        }

        if (ShowUsagePillWeekly)
        {
            fields.Add(UsagePillField.WeeklyWindow);
        }

        return fields;
    }

    /// <summary>Pushes the usage-pill field selection to every open session as a toggle changes, so it takes effect live.</summary>
    private void ApplyUsagePillFields()
    {
        var fields = ComposeUsagePillFields();
        foreach (var session in Sessions)
        {
            session.UsagePillVisibleFields = fields;
        }
    }

    /// <summary>Pushes the auto-close-on-exit toggle to every open session as it changes.</summary>
    partial void OnAutoCloseOnExitChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.AutoCloseOnExit = value;
        }
    }

    /// <summary>Pushes the combine-queued-messages toggle to every open SDK/chat session as it changes (AC-145); TTY sessions have no send queue.</summary>
    partial void OnCombineQueuedMessagesChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            if (session is SessionViewModel sdk)
            {
                sdk.CombineQueuedMessages = value;
            }
        }
    }

    /// <summary>Keeps each session's <see cref="SessionViewModel.IsSelected"/> in sync with the active selection.</summary>
    partial void OnSelectedSessionChanged(SessionPanelViewModel? oldValue, SessionPanelViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        RefreshPaneVisibility();
    }

    /// <summary>
    /// Sets each session's <see cref="SessionPanelViewModel.IsPaneVisible"/> for the current layout: all
    /// visible in the multi-session grid, only the selected one in single-pane mode (#24 / Zoom). Driven
    /// from C# on every selection/layout change rather than a per-item XAML binding, so the one live grid
    /// reliably shows exactly one panel in single-pane mode instead of stacking them.
    /// </summary>
    private void RefreshPaneVisibility()
    {
        var single = ShowSinglePane;
        foreach (var session in Sessions)
        {
            session.IsPaneVisible = BelongsToActiveWorkspace(session) && (!single || session.IsSelected);
        }
    }

    /// <summary>
    /// Whether a session belongs on the workspace now showing. Two Sessions workspaces are separate desks, so
    /// each shows only its own — but the sessions of the others keep running: they are hidden, never removed
    /// from <see cref="Sessions"/>. That distinction is the whole point. Rebinding the grid to a filtered list
    /// would rebuild the panes, which is what cost a dragged TTY its pty on 2026-07-13; gating visibility
    /// leaves every view (and pty) built exactly once, the same way the single-pane layout already works.
    /// </summary>
    private bool BelongsToActiveWorkspace(SessionPanelViewModel session)
    {
        if (Workspaces.Active is not { } active)
        {
            return true;
        }

        // A dashboard shows no sessions at all; and a session with no workspace — created before workspaces
        // existed, or in the design-time graph — belongs to the first desk that can actually show one. By
        // position it would belong to whatever happens to sit at index 0, and since the projects overview is a
        // fixture that survives every close, a cockpit whose session desks were all closed would leave such a
        // session belonging to a surface that shows no sessions at all: invisible everywhere.
        var firstSessionsWorkspace = Workspaces.Settings.Workspaces
            .FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions);

        return active.Type == WorkspaceType.Sessions
            && (session.WorkspaceId == active.Id
                || (session.WorkspaceId.Length == 0 && firstSessionsWorkspace?.Id == active.Id));
    }

    /// <summary>
    /// The sessions on the workspace now showing, in the sidebar's own order — what the strip lists, so it never
    /// offers a session the grid is hiding. Reads from <see cref="_sidebarOrder"/> (reconciled on access) rather
    /// than from <see cref="Sessions"/>, so a drag-reorder of the strip leaves the grid's tiles where they are.
    /// Returns a snapshot rather than a deferred query: the getter reconciles <see cref="_sidebarOrder"/> as a
    /// side effect, so handing back a live view over that same field would risk a "collection modified" the moment
    /// a later read reconciles again mid-enumeration.
    /// </summary>
    public IEnumerable<SessionPanelViewModel> VisibleSessions
    {
        get
        {
            _ReconcileSidebarOrder();
            return _sidebarOrder.Where(BelongsToActiveWorkspace).ToList();
        }
    }

    /// <summary>
    /// Brings <see cref="_sidebarOrder"/> back in line with <see cref="Sessions"/>: drops sessions that have
    /// closed and appends any that appeared, keeping the operator's chosen order for everything already tracked.
    /// Idempotent and cheap (a handful of sessions), so it is safe to run on every <see cref="VisibleSessions"/>
    /// read — no dependency on when <see cref="Sessions"/>'s change event happens to fire.
    /// </summary>
    private void _ReconcileSidebarOrder()
    {
        _sidebarOrder.RemoveAll(session => !Sessions.Contains(session));
        foreach (var session in Sessions)
        {
            if (!_sidebarOrder.Contains(session))
            {
                _sidebarOrder.Add(session);
            }
        }
    }

    /// <summary>
    /// Ties the session content to the strip: which workspace is active decides which panes belong on screen
    /// and whether the session grid applies at all. Called from both constructors, right after
    /// <see cref="Workspaces"/> is built — the design-time/test graph needs this exactly as much as the real
    /// one, and wiring it in only one of them is how the two quietly drift apart.
    /// </summary>
    private void _WireWorkspaceVisibility() =>
        Workspaces.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(WorkspacesViewModel.IsSessionsActive) or nameof(WorkspacesViewModel.Settings)))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowSessionGrid));
            OnPropertyChanged(nameof(ShowSessionEmptyState));
            OnPropertyChanged(nameof(HasSessionsHere));
            OnPropertyChanged(nameof(VisibleSessions));
            OnPropertyChanged(nameof(GridColumns));
            OnPropertyChanged(nameof(ShowZoomButton));

            // A desk can arrange itself differently from the last one, so switching re-reads the effective
            // layout and re-docks the TTY headers — the same work Options changing does, for the same reason.
            // It ends in RefreshPaneVisibility, which is also what keeps the other desks' sessions alive but
            // unshown.
            _OnEffectiveLayoutChanged();
        };

    // Parameterless constructor kept for the Avalonia previewer/Screenshotter design-time context —
    // seeds three sample sessions across different providers and statuses so the render shows the
    // overview + grid without a real DI-backed session behind each one.
    public CockpitViewModel()
    {
        // First: selecting a session below raises pane-visibility, which asks which workspace is active.
        Workspaces = new WorkspacesViewModel();
        _WireWorkspaceVisibility();

        var waiting = new SessionViewModel { Title = "Session 1", ActiveProfileLabel = "work (Claude)", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new SessionViewModel { Title = "Session 2", ActiveProfileLabel = "local (Ollama)", SessionStatus = SessionStatus.Busy };
        var tty = new TtyViewModel { Title = "Session 3", ActiveProfileLabel = "personal (Claude TTY)", SessionStatus = SessionStatus.Busy };

        Sessions.Add(waiting);
        Sessions.Add(busy);
        Sessions.Add(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
        Plugins = new PluginManagerViewModel();
        DelegatedTasks = new DelegatedTasksViewModel();
        Worktrees = new WorktreesViewModel();
        Projects = new ProjectsViewModel();
        Security = new SecurityOptionsViewModel(new UnprotectedSecrets());
        Diagnostics = new DiagnosticsViewModel(null, _BuildSessionDescriptors);

        // Seed the Options → Shortcuts rows from the catalog defaults; without a settings store the DI path
        // that normally builds them never runs, and the tab would render empty in the previewer/screenshotter.
        _RebuildShortcutRows();

        // No advisor in the design-time/previewer graph: the Transcribe page then offers Auto + CPU only.
        _InitVoiceTranscriptionOptions();
    }

    /// <summary>The Security tab: encrypting the credentials in cockpit.json at rest, and the migration either way.</summary>
    public SecurityOptionsViewModel Security { get; }

    // A save wrote a credential in the clear (AC-41). Re-read the banner state on the UI thread — the event comes
    // off whatever thread the save ran on, and the Security VM's properties feed a binding.
    private void OnUnprotectedSecretsWritten(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = Security.RefreshAsync());

    /// <summary>
    /// Turns encryption on from the awareness banner (AC-41) and says how it went with a toast. The banner's
    /// "Enable now" opens the password dialog in the view and hands the password here, so a success or a failure
    /// is reported the same way however the migration was started. On failure the credentials are untouched — the
    /// migration verifies itself before it publishes anything — so the message says exactly that.
    /// </summary>
    public async Task EnableEncryptionFromBannerAsync(string password)
    {
        try
        {
            await Security.EnableAsync(password);
            ToastHost.Add("Your credentials are encrypted now.", ToastSeverity.Success, null, null);
        }
        catch (Exception)
        {
            ToastHost.Add(
                "Encryption could not be turned on. Your credentials are unchanged.",
                ToastSeverity.Error,
                null,
                null);
        }
    }

    /// <summary>The Debug tab's diagnostics panel (AC-58): render backend, memory, GC, platform and crash logs, as copyable text.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    public CockpitViewModel(
        Func<SessionViewModel> sessionFactory,
        Func<TtyViewModel> ttySessionFactory,
        ISessionDialogService dialogService,
        IAudioCaptureService captureService,
        IAudioPlaybackService playbackService,
        IAttentionNotifier attentionNotifier,
        INotificationSettingsStore notificationSettingsStore,
        ITranscriptDisplaySettingsStore transcriptDisplaySettingsStore,
        ISessionBehaviorSettingsStore sessionBehaviorSettingsStore,
        ILayoutSettingsStore layoutSettingsStore,
        IVoiceSettingsStore voiceSettingsStore,
        ITerminalSettingsStore terminalSettingsStore,
        IPluginRegistrationStore? pluginRegistrationStore = null,
        IPluginInstaller? pluginInstaller = null,
        PluginBootstrap? pluginBootstrap = null,
        IPluginStoreConfigStore? pluginStoreConfigStore = null,
        IPluginStoreClient? pluginStoreClient = null,
        IPluginDialogHost? pluginDialogHost = null,
        PluginDiagnostics? pluginDiagnostics = null,
        IAudioDeviceProvider? audioDeviceProvider = null,
        IAppRestartService? appRestartService = null,
        IShortcutSettingsStore? shortcutSettingsStore = null,
        DelegatedTasksViewModel? delegatedTasks = null,
        IDebugSettingsStore? debugSettingsStore = null,
        IDelegationMcpToggle? delegationMcpToggle = null,
        IRenderingSettingsStore? renderingSettingsStore = null,
        ResourceMonitor? resourceMonitor = null,
        DiagnosticsCollector? diagnosticsCollector = null,
        IBackupService? backupService = null,
        IUpdateService? updateService = null,
        IUpdateSettingsStore? updateSettingsStore = null,
        IUpdateSupportProbe? updateSupportProbe = null,
        IWorkflowTemplateLibrary? workflowTemplateLibrary = null,
        ISecretProtectionService? secretProtection = null,
        IWorkspaceSettingsStore? workspaceSettingsStore = null,
        IWidgetRegistry? widgetRegistry = null,
        IConsentBroker? consentBroker = null,
        ITranscriptionAdvisor? transcriptionAdvisor = null,
        ITranscriptionCalibrator? transcriptionCalibrator = null,
        ITranscriptionCalibrationStore? transcriptionCalibrationStore = null,
        IModelCatalog? modelCatalog = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ITranscriptCleanupService? cleanupService = null,
        ILocalLlmEndpointResolver? localLlmEndpointResolver = null,
        IAudioCaptureService? audioCapture = null,
        ISecretKeyHolder? secretKeyHolder = null,
        IWorktreeManager? worktreeManager = null,
        WorktreesViewModel? worktrees = null,
        ProjectsViewModel? projects = null,
        IWorktreeSettingsStore? worktreeSettingsStore = null,
        ICloneSettingsStore? cloneSettingsStore = null,
        LiveSessionRegistry? liveSessions = null,
        IUsagePillSettingsStore? usagePillSettingsStore = null,
        IScreenLockSettingsStore? screenLockSettingsStore = null,
        ITerminalAccessSwitch? terminalAccessSwitch = null,
        ITerminalAccessSettingsStore? terminalAccessSettingsStore = null,
        ITerminalAccessRegistry? terminals = null,
        ISessionProfileStore? sessionProfileStore = null,
        IWorkspaceTypeRegistry? workspaceTypeRegistry = null,
        ProjectQuickStart? projectQuickStart = null,
        IScreenshotSettingsStore? screenshotSettingsStore = null,
        ISessionResourceResolver? sessionResourceResolver = null,
        IWorkspaceAgentCoordinator? agentCoordinator = null,
        IAgentMessageInbox? agentMessages = null,
        IAgentResourceClaims? agentClaims = null,
        IClaimCollisionMonitor? claimCollisionMonitor = null,
        SessionStateRecorder? sessionStateRecorder = null,
        ISessionStateStore? sessionStateStore = null,
        SessionRestorePlanner? sessionRestorePlanner = null,
        IWorktreeReconcileGate? worktreeReconcileGate = null,
        PluginManager? pluginManager = null,
        ILogger<CockpitViewModel>? logger = null)
    {
        // Without a store this is the default single Sessions workspace and nothing persists — which is exactly
        // what the unit-test and design-time graphs want, and is why the tab strip stays hidden there.
        //
        // The toast host goes in so a refused save is said rather than dropped: the strip's changes are all
        // fire-and-forget, so without somewhere to report to, a write the config gate turned down would be
        // silence and a lost arrangement.
        Workspaces = new WorkspacesViewModel(workspaceSettingsStore, widgetRegistry, ToastHost, workspaceTypeRegistry);
        _WireWorkspaceVisibility();

        // The Security tab (encrypting the credentials at rest). Absent in the design-time/unit-test graph, and
        // the tab simply reports "not encrypted" then rather than the dialog failing to open at all.
        Security = new SecurityOptionsViewModel(secretProtection ?? new UnprotectedSecrets(), screenLockSettingsStore, terminalAccessSwitch, terminalAccessSettingsStore);
        _ = Security.RefreshAsync();

        // The awareness banner (AC-41) has to re-evaluate the moment a credential is written in the clear — a new
        // MCP server, a provider key, a plugin's token — not just when the Security tab is opened. The write seam
        // raises this from whatever thread did the save, so the refresh is marshalled back to the UI thread.
        _secretKeyHolder = secretKeyHolder ?? SecretKeyHolder.Shared;
        _secretKeyHolder.UnprotectedSecretsWritten += OnUnprotectedSecretsWritten;

        // The Debug tab's diagnostics panel (AC-58). Absent in the design-time/unit-test graph, where the collector
        // is not registered; the panel then reports it is unavailable rather than the dialog failing to open.
        Diagnostics = new DiagnosticsViewModel(diagnosticsCollector, _BuildSessionDescriptors);

        _updates = updateService;
        _updateSettingsStore = updateSettingsStore;

        // Asked once, here: whether this copy was installed by the updater is settled before the process started
        // and cannot change while it runs. A probe that was not supplied — the design-time view model, a test that
        // does not care — reads as not packaged, which is the answer that offers less rather than more.
        CanUpdateItself = (updateSupportProbe?.Detect() ?? UpdateSupport.NotPackaged) == UpdateSupport.Supported;
        _backupService = backupService;
        _appRestart = appRestartService;
        // AC-478: whether this process was launched with PluginManager.SafeModeArgument, read off the same
        // singleton Program.cs constructed the switch on — not a second source of truth for a fact that must
        // agree with what actually happened to plugin loading.
        _safeMode = pluginManager?.SafeMode ?? false;
        DelegatedTasks = delegatedTasks ?? new DelegatedTasksViewModel();
        _worktreeManager = worktreeManager;
        _sessionStateRecorder = sessionStateRecorder;
        _sessionStateStore = sessionStateStore;
        _sessionRestorePlanner = sessionRestorePlanner;
        _worktreeReconcileGate = worktreeReconcileGate;
        _logger = logger;

        // One subscription rather than a call after each of the three creation paths: the branch can move on any of
        // them, and on the plugin-run path the start can still be cancelled afterwards, which would take the news
        // with it (AC-349).
        if (worktreeManager is not null)
        {
            worktreeManager.SourceRefreshed += _ToastWorktreeSource;
        }
        _terminals = terminals;
        _liveSessions = liveSessions;
        Worktrees = worktrees ?? new WorktreesViewModel();
        Projects = projects ?? new ProjectsViewModel();
        _projectQuickStart = projectQuickStart;

        // The sidebar's Projects section (AC-164) is on screen from startup, so the list is read now rather than
        // when Options opens — which used to be the only thing that needed it. Fire-and-forget like every other
        // startup read here; the section simply stays hidden until it lands.
        _ = Projects.LoadAsync();
        // The panes are one source of "which sessions are live" (their pane ids, what worktrees are keyed on); the
        // shared registry adds the ones that run without a pane, today the delegated tasks (AC-106). Both worktree
        // guards — the managed panel and the agent's worktree_remove MCP tool — then read that registry, so neither
        // pulls a running session's checkout out from under it, and neither offers to sweep a checkout the other
        // still considers taken. Without a registry (a graph built without one) the panel falls back to the panes,
        // which is what it read before.
        IReadOnlySet<string> LivePaneIds() => Sessions.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        liveSessions?.SetSource(LivePaneIds);
        Worktrees.LiveSessionIds = liveSessions is { } registry ? () => registry.LiveSessionIds : LivePaneIds;
        Worktrees.ReattachRequested += record => _ = _ReattachSessionAsync(record);
        _ = Worktrees.RefreshCountAsync();
        _worktreeSettingsStore = worktreeSettingsStore;
        WorktreeRootPlaceholder = worktreeSettingsStore?.DefaultRoot ?? string.Empty;
        _ = LoadWorktreeSettingsAsync();
        _cloneSettingsStore = cloneSettingsStore;
        CloneRootPlaceholder = cloneSettingsStore?.DefaultRoot ?? string.Empty;
        _ = LoadCloneSettingsAsync();
        _audioDeviceProvider = audioDeviceProvider;
        _modelCatalog = modelCatalog;
        _voicePlaybackQueue = voicePlaybackQueue;
        _cleanupService = cleanupService;
        _localLlmEndpointResolver = localLlmEndpointResolver;
        _audioCapture = audioCapture;
        // Seed the model dropdown synchronously so it is never empty before the first async probe runs — "Auto"
        // plus the advised models, always. The probe adds the server's models on top when the dialog opens.
        _PopulateVoiceLlmModels([]);
        _pluginDiagnostics = pluginDiagnostics;
        _pluginDialogHost = pluginDialogHost;
        _shortcutSettingsStore = shortcutSettingsStore;
        // The full plugin manager needs its store/installer/bootstrap, store dependencies, the dialog host
        // and the diagnostics; when they are absent (unit tests that don't exercise plugins) the design-time
        // manager is used, so the tab is inert.
        Plugins = pluginRegistrationStore is not null && pluginInstaller is not null && pluginBootstrap is not null
                && pluginStoreConfigStore is not null && pluginStoreClient is not null && pluginDialogHost is not null
                && pluginDiagnostics is not null
            ? new PluginManagerViewModel(pluginRegistrationStore, pluginInstaller, pluginBootstrap, dialogService, pluginStoreConfigStore, pluginStoreClient, PluginSettings, pluginDiagnostics, this, appRestartService, workflowTemplateLibrary)
            : new PluginManagerViewModel();
        // #184: a contribution can fail after the phase-2 pass that first calls RefreshPluginFailures (e.g. a
        // plugin's fire-and-forget AddMcpServer completing on a background continuation) — without this, the
        // banner would keep reporting the state at startup while the Plugin manager moved on, the exact
        // divergence the ticket rules out. Subscribed after Plugins above is assigned: RefreshPluginFailures
        // dereferences it, and a Record arriving on the UI thread runs the handler synchronously.
        if (_pluginDiagnostics is not null)
        {
            _pluginDiagnostics.Changed += () => _OnUiThread(RefreshPluginFailures);
        }
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _sessionProfileStore = sessionProfileStore;
        _dialogService = dialogService;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
        _transcriptDisplaySettingsStore = transcriptDisplaySettingsStore;
        _usagePillSettingsStore = usagePillSettingsStore;
        _sessionBehaviorSettingsStore = sessionBehaviorSettingsStore;
        _screenshotSettingsStore = screenshotSettingsStore;
        _layoutSettingsStore = layoutSettingsStore;
        _voiceSettingsStore = voiceSettingsStore;
        _terminalSettingsStore = terminalSettingsStore;
        _debugSettingsStore = debugSettingsStore;
        // The orchestrator loads its own setting on startup (before the UI), so its live value seeds the toggle here.
        _delegationMcpToggle = delegationMcpToggle;
        _orchestratorMcpEnabled = delegationMcpToggle?.McpEnabled ?? true;
        _sessionResourceResolver = sessionResourceResolver;
        _agentCoordinator = agentCoordinator;
        _agentMessages = agentMessages;
        _agentClaims = agentClaims;
        _claimCollisionMonitor = claimCollisionMonitor;
        _renderingSettingsStore = renderingSettingsStore;
        _transcriptionAdvisor = transcriptionAdvisor;
        _transcriptionCalibrator = transcriptionCalibrator;
        _transcriptionCalibrationStore = transcriptionCalibrationStore;
        // Build the host-aware backend list before the fire-and-forget voice load below reselects the saved
        // preference against it (AC-68).
        _InitVoiceTranscriptionOptions();
        _resourceMonitor = resourceMonitor;
        // No session is opened on startup (#31): the app starts on the empty state and a session only
        // exists once the operator creates one from the New-session dialog.
        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasSessionsHere));
            OnPropertyChanged(nameof(VisibleSessions));
            OnPropertyChanged(nameof(GridColumns));
            OnPropertyChanged(nameof(ShowZoomButton));
            OnPropertyChanged(nameof(StackSessionsInStack));
            OnPropertyChanged(nameof(ShowSessionGrid));
            OnPropertyChanged(nameof(ShowSessionEmptyState));
            RefreshPaneVisibility();
        };

        _ = LoadNotificationSettingsAsync();
        _ = LoadTranscriptDisplaySettingsAsync();
        _ = LoadUsagePillSettingsAsync();
        _ = LoadSessionBehaviorSettingsAsync();
        _ = LoadScreenshotSettingsAsync();
        _ = LoadLayoutSettingsAsync();
        _ = LoadVoiceSettingsAsync();
        _ = LoadTerminalSettingsAsync();
        _ = LoadShortcutSettingsAsync();
        _ = LoadDebugSettingsAsync();
        _ = LoadRenderingSettingsAsync();
        _ = LoadPluginMenuPreferencesAsync(pluginRegistrationStore);

        // Plugin shortcuts arrive as plugins initialize; each changes the active bindings and the Options list.
        PluginShortcuts.CollectionChanged += (_, _) =>
        {
            _RebuildActiveShortcuts();
            _RebuildShortcutRows();
        };

        // The consent gate (#AC-47) opens a prompt on the session it belongs to; the cockpit owns the panes, so it
        // routes the prompt to the right one (and a toast when that pane is not the one on screen). Absent in the
        // design-time/unit-test graph — the banner simply never appears there.
        _consentBroker = consentBroker;
        if (consentBroker is not null)
        {
            consentBroker.PromptOpened += _OnConsentPromptOpened;
            consentBroker.PromptClosed += _OnConsentPromptClosed;
        }
    }

    // Route a consent prompt to the pane it belongs to. On the UI thread: it sets an observable property and can
    // raise a toast. A prompt whose pane is gone is denied rather than left hanging — there is nowhere to show it.
    private void _OnConsentPromptOpened(object? sender, ConsentPrompt prompt) =>
        Dispatcher.UIThread.Post(() =>
        {
            // A request that names a pane goes to that pane; a host-internal caller with no pane of its own (a null
            // PaneId) surfaces on the active session. Either way, if there is nowhere to show it, deny — never hang.
            var pane = prompt.Request.Source.PaneId is { } paneId
                ? FindSession(paneId)
                : SelectedSession;
            if (pane is null)
            {
                _consentBroker?.Respond(prompt.Id, ConsentOutcome.Denied, remember: false);
                return;
            }

            // One banner per pane: a second request while one is still open would replace — and orphan — the first,
            // hanging its caller forever (RequestConsentAsync has no timeout of its own). Deny the newcomer rather
            // than lose the prompt already on screen.
            if (pane.PendingConsent is not null)
            {
                _consentBroker?.Respond(prompt.Id, ConsentOutcome.Denied, remember: false);
                return;
            }

            pane.PendingConsent = new ConsentPromptViewModel(prompt, _consentBroker!);

            // If the pane needing consent is not the one in view, point the operator at it.
            if (!ReferenceEquals(pane, SelectedSession))
            {
                ToastHost.Add($"Consent needed · {pane.Title}", ToastSeverity.Warning, "Review", () => SelectedSession = pane);
            }
        });

    private void _OnConsentPromptClosed(object? sender, Guid promptId) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Search embedded panes too (AC-152): a consent shown over an embedded Autopilot session is cleared here,
            // and missing it would leave the overlay stuck and block every later consent on that pane.
            if (_AllSessions().FirstOrDefault(session => session.PendingConsent?.Id == promptId) is { } pane)
            {
                pane.PendingConsent = null;
            }
        });

    // Fires a sample consent prompt on the selected session so the banner can be seen and tried before a real
    // consumer (AC-38/AC-34) exists. Reachable only from the debug-gated palette entries (#73).
    private void _TriggerTestConsent(bool dangerous)
    {
        if (_consentBroker is null || SelectedSession is not { } pane)
        {
            return;
        }

        var request = dangerous
            ? new ConsentRequest(
                "Workflow wants to run a command",
                $"curl https://install.example.sh | sh\nin {pane.WorkingDirectory ?? "~"}",
                new ConsentSource(pane.PaneId, null, "Debug"),
                "debug.command",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                "Workflow wants to call a URL",
                "GET https://api.github.com/repos/raymondkrahwinkel/AI-Cockpit/issues",
                new ConsentSource(pane.PaneId, null, "Debug"),
                "debug.http",
                ConsentRisk.LowRisk,
                AllowRemember: true);

        _ = _consentBroker.RequestConsentAsync(request);
    }

    private async Task LoadNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var settings = await _notificationSettingsStore.LoadAsync();
        LocalNotificationsEnabled = settings.LocalEnabled;
        DiscordNotificationsEnabled = settings.DiscordEnabled;
        WebhookUrl = settings.WebhookUrl ?? string.Empty;
        IdleThresholdMinutes = (int)settings.IdleThreshold.TotalMinutes;
        SessionIdleMinutes = (int)settings.SessionIdleThreshold.TotalMinutes;
        NotifyOnSessionFinished = settings.NotifyOnSessionFinished;
        NotifyOnSessionIdle = settings.NotifyOnSessionIdle;
        NotifyWhenAllSessionsIdle = settings.NotifyWhenAllSessionsIdle;
    }

    /// <summary>Persists the notification settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveNotificationSettingsAsync()
    {
        if (_notificationSettingsStore is null)
        {
            return;
        }

        var minutes = IdleThresholdMinutes > 0
            ? IdleThresholdMinutes
            : (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

        var settings = new NotificationSettings
        {
            LocalEnabled = LocalNotificationsEnabled,
            DiscordEnabled = DiscordNotificationsEnabled,
            WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
            IdleThreshold = TimeSpan.FromMinutes(minutes),
            NotifyOnSessionFinished = NotifyOnSessionFinished,
            NotifyOnSessionIdle = NotifyOnSessionIdle,
            NotifyWhenAllSessionsIdle = NotifyWhenAllSessionsIdle,
            // 0 is a real choice here ("never let a session go idle"), so it is saved as written rather than
            // being nudged back to the default the way the away-threshold is.
            SessionIdleThreshold = SessionIdleMinutes > 0 ? TimeSpan.FromMinutes(SessionIdleMinutes) : TimeSpan.Zero,
        };

        await _notificationSettingsStore.SaveAsync(settings);
        NotificationSettingsStatus = "Saved";
    }

    private async Task LoadShortcutSettingsAsync()
    {
        if (_shortcutSettingsStore is not null)
        {
            _shortcutSettings = await _shortcutSettingsStore.LoadAsync();
        }

        _RebuildShortcutRows();
        _RebuildActiveShortcuts();
    }

    /// <summary>Persists the keyboard shortcuts edited in the Options → Shortcuts tab to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveShortcutSettingsAsync()
    {
        // Fold the editable rows back into the settings, then re-arm the live bindings so a change takes effect
        // immediately without a restart.
        var settings = _shortcutSettings;
        foreach (var row in ShortcutRows)
        {
            settings = row.Action is { } action
                ? settings.With(action, row.Gesture)
                : row.PluginShortcutId is { } id
                    ? settings.WithPlugin(id, row.Gesture)
                    : settings;
        }

        _shortcutSettings = settings;
        _RebuildActiveShortcuts();

        if (_shortcutSettingsStore is not null)
        {
            await _shortcutSettingsStore.SaveAsync(settings);
        }

        ShortcutSettingsStatus = "Saved";
    }

    // The Options list: one editable row per app action (label + configured gesture), then a read-only row per
    // plugin-contributed shortcut so the operator can see what plugins bound.
    private void _RebuildShortcutRows()
    {
        ShortcutRows.Clear();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            ShortcutRows.Add(new ShortcutRowViewModel(descriptor.Label, descriptor.Action, _shortcutSettings.GestureFor(descriptor.Action)));
        }

        foreach (var shortcut in PluginShortcuts)
        {
            ShortcutRows.Add(new ShortcutRowViewModel(
                $"{shortcut.Title} (plugin)",
                shortcut.Id,
                _shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture)));
        }
    }

    // The live dispatch table the view matches against: every bound app action (blank = unbound, skipped) plus
    // every plugin shortcut, each paired with the action to run.
    private void _RebuildActiveShortcuts()
    {
        var bindings = new List<ShortcutBinding>();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            var gesture = _shortcutSettings.GestureFor(descriptor.Action);
            if (!string.IsNullOrWhiteSpace(gesture))
            {
                // The command palette is the one shortcut that must open even while typing in a session/terminal.
                var alwaysActive = descriptor.Action == ShortcutAction.CommandPalette;
                bindings.Add(new ShortcutBinding(
                    gesture,
                    descriptor.Label,
                    () => _InvokeAppAction(descriptor.Action),
                    alwaysActive,
                    ShortcutCatalog.StaysActiveInTerminal(descriptor.Action)));
            }
        }

        foreach (var shortcut in PluginShortcuts)
        {
            var gesture = _shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture);
            if (!string.IsNullOrWhiteSpace(gesture))
            {
                bindings.Add(new ShortcutBinding(gesture, shortcut.Title, shortcut.OnInvoke));
            }
        }

        ActiveShortcuts = bindings;
    }

    // Runs the command behind an app-action shortcut. Commands are the same ones the main menu binds to.
    private void _InvokeAppAction(ShortcutAction action)
    {
        // Duplicate takes the active session as its parameter, unlike the parameterless app commands below.
        if (action == ShortcutAction.DuplicateSession)
        {
            if (SelectedSession is { } session && DuplicateSessionCommand.CanExecute(session))
            {
                DuplicateSessionCommand.Execute(session);
            }

            return;
        }

        // These carry what they act on, like Duplicate above, so they cannot join the parameterless switch.
        // Each does nothing when it does not apply — the palette lists every command, and running one that does
        // not apply right now should be a no-op rather than a surprise.
        switch (action)
        {
            case ShortcutAction.NewSessionsWorkspace:
                Workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Sessions);
                return;

            case ShortcutAction.NewDashboardWorkspace:
                Workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Dashboard);
                return;

            case ShortcutAction.CloseWorkspace:
                if (Workspaces.Active is { } active)
                {
                    // The same ask-then-close the tab's ✕ takes: the palette does not get to skip the prompt for
                    // something that stops running sessions.
                    _ = CloseWorkspaceWithConfirmationAsync(active.Id);
                }

                return;

            // Spatial pane focus needs the grid geometry, which lives in the view — raise for the view to answer.
            case ShortcutAction.FocusPaneLeft:
                SpatialNavigationRequested?.Invoke(this, PaneDirection.Left);
                return;

            case ShortcutAction.FocusPaneRight:
                SpatialNavigationRequested?.Invoke(this, PaneDirection.Right);
                return;

            case ShortcutAction.FocusPaneUp:
                SpatialNavigationRequested?.Invoke(this, PaneDirection.Up);
                return;

            case ShortcutAction.FocusPaneDown:
                SpatialNavigationRequested?.Invoke(this, PaneDirection.Down);
                return;
        }

        System.Windows.Input.ICommand? command = action switch
        {
            ShortcutAction.NewSession => NewSessionCommand,
            ShortcutAction.NewTerminal => NewTerminalCommand,
            ShortcutAction.ManageProfiles => ManageProfilesCommand,
            ShortcutAction.McpServers => OpenMcpServersCommand,
            ShortcutAction.PluginStore => OpenPluginStoreCommand,
            ShortcutAction.Options => OptionsCommand,
            ShortcutAction.About => AboutCommand,
            ShortcutAction.ToggleZoom => ToggleZoomCommand,
            ShortcutAction.CommandPalette => ShowCommandPaletteCommand,
            ShortcutAction.PreviousSession => SelectPreviousSessionCommand,
            ShortcutAction.NextSession => SelectNextSessionCommand,
            ShortcutAction.PreviousWorkspace => Workspaces.SelectPreviousWorkspaceCommand,
            ShortcutAction.NextWorkspace => Workspaces.SelectNextWorkspaceCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Raised when a pane-focus shortcut (Ctrl+arrow) asks to move the selection to the pane in that direction.
    /// The grid geometry that answers "which pane is to the left" lives in the view (the session tile panel),
    /// which the view-model does not reach — so the view handles this and sets <see cref="SelectedSession"/>,
    /// the same one-way arrangement the drag-reorder and scroll-to-selected already use.
    /// </summary>
    public event EventHandler<PaneDirection>? SpatialNavigationRequested;

    private async Task LoadTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        var settings = await _transcriptDisplaySettingsStore.LoadAsync();
        ShowTimestamps = settings.ShowTimestamps;
    }

    /// <summary>Persists the transcript-display settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveTranscriptDisplaySettingsAsync()
    {
        if (_transcriptDisplaySettingsStore is null)
        {
            return;
        }

        await _transcriptDisplaySettingsStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = ShowTimestamps });
        TranscriptDisplaySettingsStatus = "Saved";
    }

    private async Task LoadUsagePillSettingsAsync()
    {
        if (_usagePillSettingsStore is null)
        {
            return;
        }

        var settings = await _usagePillSettingsStore.LoadAsync();
        ShowUsagePillContext = settings.VisibleFields.Contains(UsagePillField.Context);
        ShowUsagePillSessionUsage = settings.VisibleFields.Contains(UsagePillField.SessionUsage);
        ShowUsagePillFiveHour = settings.VisibleFields.Contains(UsagePillField.FiveHourWindow);
        ShowUsagePillWeekly = settings.VisibleFields.Contains(UsagePillField.WeeklyWindow);
    }

    /// <summary>Persists the usage-pill field selection edited in the Options dialog to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveUsagePillSettingsAsync()
    {
        if (_usagePillSettingsStore is null)
        {
            return;
        }

        await _usagePillSettingsStore.SaveAsync(new UsagePillSettings { VisibleFields = ComposeUsagePillFields() });
        UsagePillSettingsStatus = "Saved";
    }

    private async Task LoadSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionBehaviorSettingsStore.LoadAsync();
        AutoCloseOnExit = settings.AutoCloseOnExit;
        CombineQueuedMessages = settings.CombineQueuedMessages;
    }

    /// <summary>Persists the session-behaviour settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        await _sessionBehaviorSettingsStore.SaveAsync(new SessionBehaviorSettings
        {
            AutoCloseOnExit = AutoCloseOnExit,
            CombineQueuedMessages = CombineQueuedMessages,
        });
        SessionBehaviorSettingsStatus = "Saved";
    }

    /// <summary>What the cockpit and its sessions are using, for the status bar (#78) — e.g. "CPU 12% · RAM 1.9 GB".</summary>
    [ObservableProperty]
    private string _resourceSummary = string.Empty;

    /// <summary>The CPU half of the status-bar figure, up to and including "RAM " — split from the memory so the memory alone can change colour.</summary>
    [ObservableProperty]
    private string _resourceCpu = string.Empty;

    [ObservableProperty]
    private string _resourceMemory = string.Empty;

    /// <summary>Which brush the memory figure reads in: quiet, amber as it climbs, red where the system starts killing things.</summary>
    [ObservableProperty]
    private string _resourceMemoryBrushKey = "CockpitTextSecondaryBrush";

    /// <summary>The same, broken down per session — the panel's own text when there is nothing to break down.</summary>
    [ObservableProperty]
    private string _resourceDetail = string.Empty;

    /// <summary>
    /// The breakdown as rows (#78): what the resource panel lists. It opens from the figures in the status bar
    /// rather than appearing on hover — a tooltip is at the mercy of the platform's hit-testing and placement, and
    /// on this one it turned out to be at the mercy of both. A panel the operator opens is also a panel that stays
    /// open while they read it.
    /// </summary>
    public ObservableCollection<ResourceRowViewModel> ResourceRows { get; } = [];

    /// <summary>
    /// The local model servers (#78) — Ollama, LM Studio — with what they are holding. A session that talks to one
    /// over HTTP has no process of its own, so it can never appear above; the model it loaded is nonetheless the
    /// heaviest thing on the machine, and "nothing to break down" was a poor answer to "what is using my memory".
    /// </summary>
    public ObservableCollection<ResourceRowViewModel> ModelServerRows { get; } = [];

    /// <summary>Whether a local model server is running at all — no Ollama, no section.</summary>
    public bool HasModelServers => ModelServerRows.Count > 0;

    /// <summary>Whether the resource panel is open — toggled from the status bar's figures.</summary>
    [ObservableProperty]
    private bool _isResourcePanelOpen;

    /// <summary>True when there is nothing to break down: sessions that run over HTTP have no local process to weigh.</summary>
    public bool HasResourceRows => ResourceRows.Count > 0;

    /// <summary>Opens the breakdown, or closes it — the status bar's figures are the button.</summary>
    [RelayCommand]
    private void ToggleResourcePanel() => IsResourcePanelOpen = !IsResourcePanelOpen;

    /// <summary>Closes the breakdown. Esc, and the panel's own close button.</summary>
    [RelayCommand]
    private void CloseResourcePanel() => IsResourcePanelOpen = false;

    /// <summary>Left of the meter: how many sessions are being weighed, so it is visible that the breakdown exists at all rather than hidden behind a hover nobody tries.</summary>
    [ObservableProperty]
    private string _resourceSessions = string.Empty;

    /// <summary>
    /// Whether a memory warning is standing. Kept here between samples, because the decision is "has it climbed since
    /// I last said so", and that question needs a memory of its own.
    /// </summary>
    private bool _warnedAboutMemory;

    /// <summary>
    /// Says something when the cockpit and its sessions together approach what the machine has (#78).
    /// <para>
    /// A session is 300–700 MB of Node; three of them outweigh the whole app. This is the difference between "the app
    /// suddenly disappeared" and "you were told, and you could have closed a session". Why the operating system kills
    /// what it kills — and why the coalition explanation this comment used to give is wrong — is in
    /// <see cref="Cockpit.Core.Diagnostics.MemoryPressure"/>.
    /// </para>
    /// </summary>
    private void _WarnAboutMemory(ResourceUsage usage)
    {
        var decision = MemoryPressure.Decide(usage.MemoryBytes, MachineMemory.TotalBytes(), _warnedAboutMemory);
        _warnedAboutMemory = decision.Warned;

        if (!decision.Warn)
        {
            return;
        }

        var heaviest = usage.Sessions.MaxBy(session => session.MemoryBytes);

        var advice = heaviest is not null
            ? $" '{heaviest.Title}' is the largest at {_Megabytes(heaviest.MemoryBytes)} — closing or restarting it frees that."
            : string.Empty;

        // Raised on the host this view model owns: ToastService is built *from* it, and injecting the service back in
        // is a circle the container walks forever.
        ToastHost.Add(
            $"The cockpit and its sessions are using {_Megabytes(usage.MemoryBytes)} of {_Megabytes(MachineMemory.TotalBytes())}. On macOS the system kills the whole app when memory gets tight — sessions and all.{advice}",
            ToastSeverity.Warning,
            actionLabel: null,
            onAction: null);
    }

    // The sessions the diagnostics panel weighs (AC-58): title, kind and process id, built here so the collector
    // stays free of any view-model type. A terminal session is a TtyViewModel; everything else runs an agent.
    private IReadOnlyList<SessionDescriptor> _BuildSessionDescriptors() =>
        Sessions
            .Select(session => new SessionDescriptor(
                session.Title,
                session is TtyViewModel ? "Terminal" : "Agent",
                session.ProcessId))
            .ToList();

    /// <summary>
    /// Takes one sample and updates the status bar (#78). Driven by a timer in the view, like the idle sweep —
    /// the view model stays free of timers, and a test can tick it whenever it likes.
    /// </summary>
    internal void SampleResources()
    {
        if (_resourceMonitor is null)
        {
            return;
        }

        // A session with no process (an HTTP-backed provider) has nothing local to weigh; it is left out rather
        // than shown as 0%, which would read as "idle" instead of "not measurable here".
        var processes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in Sessions.Where(session => session.ProcessId is not null))
        {
            processes[session.Title] = session.ProcessId!.Value;
        }

        var usage = _resourceMonitor.Sample(processes);

        _WarnAboutMemory(usage);

        ResourceCpu = $"CPU {usage.CpuPercent:0}%  ·  RAM ";
        ResourceMemory = _Megabytes(usage.MemoryBytes);

        // Amber before the toast, red at the point where macOS starts thinking about killing the app: a number that
        // changes colour while you work is something you can act on without being interrupted.
        ResourceMemoryBrushKey = MemoryPressure.Level(usage.MemoryBytes, MachineMemory.TotalBytes()) switch
        {
            MemoryPressureLevel.High => "CockpitStatusErrorBrush",
            MemoryPressureLevel.Elevated => "CockpitStatusWaitingBrush",
            _ => "CockpitTextSecondaryBrush",
        };

        ResourceSummary = $"CPU {usage.CpuPercent:0}%  ·  RAM {_Megabytes(usage.MemoryBytes)}";
        ResourceSessions = usage.Sessions.Count switch
        {
            0 => string.Empty,
            1 => "1 session",
            var count => $"{count} sessions",
        };

        // The total is the cockpit's whole tree, so it already contains the sessions — saying so stops the
        // breakdown from reading like it should add up to the total.
        ResourceDetail = usage.Sessions.Count == 0
            ? "No session has a process of its own — one that runs over HTTP (Ollama, LM Studio) is served by the model server below. What the total is made of:"
            : "What the total is made of — each session's process and everything it spawned, the app itself, and the tool servers it started for them:";

        _RefreshResourceRows(usage);
    }

    // Rebuilt in place, same as the session rows: the panel is refreshed every couple of seconds, and a list that
    // empties itself first flickers in the hand of whoever is reading it.
    private void _RefreshModelServerRows(ResourceUsage usage)
    {
        // Measured against the machine, not against the cockpit: these servers are not inside the cockpit's total, so
        // a share of that total would be a fraction of the wrong thing — and a model can easily outweigh the app.
        var machine = MachineMemory.TotalBytes();

        var rows = usage.ModelServers
            .Select(server => new ResourceRowViewModel(
                server.Name,
                $"{_Percent(server.MemoryBytes, machine)} of this machine",
                _Megabytes(server.MemoryBytes),
                _Share(server.MemoryBytes, machine)))
            .ToList();

        for (var index = 0; index < rows.Count; index++)
        {
            if (index < ModelServerRows.Count)
            {
                if (!ModelServerRows[index].Equals(rows[index]))
                {
                    ModelServerRows[index] = rows[index];
                }
            }
            else
            {
                ModelServerRows.Add(rows[index]);
            }
        }

        while (ModelServerRows.Count > rows.Count)
        {
            ModelServerRows.RemoveAt(ModelServerRows.Count - 1);
        }

        OnPropertyChanged(nameof(HasModelServers));
    }

    // Rebuilt in place rather than cleared and refilled: this runs every couple of seconds, and a collection that
    // empties itself first makes the panel flicker in the hand of whoever has it open.
    private void _RefreshResourceRows(ResourceUsage usage)
    {
        // Everything inside the total, in one list: the sessions, the app itself, and the MCP tool servers it started
        // for them. Those servers are what took the figure from 300 MB to 800 the moment a session connected, and they
        // were nowhere on screen — a total that cannot be explained is a total nobody can act on.
        var parts = usage.Sessions
            .Select(session => new ResourceRowViewModel(
                session.Title,
                $"CPU {session.CpuPercent:0}%",
                _Megabytes(session.MemoryBytes),
                _Share(session.MemoryBytes, usage.MemoryBytes)))
            .Append(new ResourceRowViewModel(
                // A label in a column of names, so it is capitalised like one — and it is not the product's name,
                // because this list is in the main window and the title bar is where that gets stated.
                "The cockpit itself",
                "the app, its windows and its transcripts",
                _Megabytes(usage.Parts.OwnBytes),
                _Share(usage.Parts.OwnBytes, usage.MemoryBytes)))
            .Concat(usage.Parts.Children.Select(child => new ResourceRowViewModel(
                child.Name,
                "a tool server the cockpit started",
                _Megabytes(child.MemoryBytes),
                _Share(child.MemoryBytes, usage.MemoryBytes))));

        var rows = parts
            .OrderByDescending(row => row.MemoryShare)
            .ToList();

        for (var index = 0; index < rows.Count; index++)
        {
            if (index < ResourceRows.Count)
            {
                if (!ResourceRows[index].Equals(rows[index]))
                {
                    ResourceRows[index] = rows[index];
                }
            }
            else
            {
                ResourceRows.Add(rows[index]);
            }
        }

        while (ResourceRows.Count > rows.Count)
        {
            ResourceRows.RemoveAt(ResourceRows.Count - 1);
        }

        OnPropertyChanged(nameof(HasResourceRows));
        _RefreshModelServerRows(usage);
    }

    // A session's number includes everything it spawned, so "RAM" here means the tree, not the parent.
    private static string _Megabytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{bytes / 1024 / 1024} MB";

    private static double _Share(long part, long whole) =>
        whole > 0 ? Math.Clamp((double)part / whole, 0, 1) : 0;

    // "of this machine" only means something when the machine's memory can be read; where it cannot, the share is
    // left unsaid rather than shown as zero.
    private static string _Percent(long part, long whole) =>
        whole > 0 ? $"{(double)part / whole:P0}" : "an unknown share";

    /// <summary>
    /// Whether this cockpit can back itself up (#70) — false only in the design-time view model, which has no
    /// services at all. The buttons bind to it, so a build that forgot to register the service shows them disabled
    /// rather than showing two controls that swallow a click and do nothing.
    /// </summary>
    public bool CanBackUp => _backupService is not null;

    /// <summary>
    /// Reads the update preferences and, if they say so, looks once for a newer build (#71). Called at startup — the
    /// single first look; <see cref="StartPeriodicUpdateChecks"/> keeps looking every hour after this while the window
    /// stays open. A failed check is silent here: the cockpit has just opened, and a toast saying GitHub was
    /// unreachable is noise about a thing nobody asked for. Ask from the Options tab and it says exactly what went wrong.
    /// </summary>
    public async Task InitialiseUpdatesAsync()
    {
        if (_updates is not { } updates)
        {
            return;
        }

        var (version, commit) = updates.Current;
        CurrentBuild = commit.Length == 0 ? version : $"{version} ({commit[..Math.Min(7, commit.Length)]})";

        if (_updateSettingsStore is { } store)
        {
            var settings = await store.LoadAsync();

            // Reading the file waits out anything holding it, for up to a couple of seconds, and the Updates tab is
            // reachable while it does. A control the operator changed in that window is theirs and keeps their value;
            // every other one takes what was read. Per control rather than all-or-nothing: they touched one setting,
            // not the section, and treating the whole section as spoken for is how touching the startup box came to
            // discard a channel chosen on an earlier run.
            _loadingUpdateSettings = true;
            try
            {
                if (!_startupChoiceMade)
                {
                    CheckForUpdatesOnStartup = settings.CheckOnStartup;
                }

                if (!_channelChoiceMade)
                {
                    _chosenChannel = settings.Channel;

                    // Nobody has chosen, so the build decides (AC-387). Defaulting to stable instead is how a nightly
                    // started without a configuration file is offered the latest stable as its first update — a
                    // downgrade, presented as an upgrade.
                    IncludeNightlyBuilds = (_chosenChannel ?? BuildChannel.FromVersion(version)) == UpdateChannel.Nightly;
                }
            }
            finally
            {
                _loadingUpdateSettings = false;
            }

            _updateSettingsRead = true;

            // A change the operator made while this was reading was held back rather than written — it would have
            // persisted a channel not yet read, erasing an earlier choice. Now that both halves are known, it goes.
            if (_updateSettingsSavePending)
            {
                _updateSettingsSavePending = false;
                _SaveUpdateSettings();
            }
        }

        if (!CheckForUpdatesOnStartup)
        {
            return;
        }

        var result = await updates.CheckAsync(_Stream);
        if (result.Release is not { } release)
        {
            return;
        }

        _Announce(release);

        // The toast is the whole point of checking on startup: a newer build nobody is told about is one nobody
        // installs. Raised on the host this view model owns, not through IToastService — that service is built *from*
        // this view model, so injecting it here would be a circle the container never resolves.
        _ToastUpdate(release);
    }

    // The "a newer build is out" toast, shared by the startup check and the hourly re-check (AC-188) so the two never
    // drift. Raised on ToastHost for the same circular-dependency reason as the startup toast above.
    private void _ToastUpdate(AppRelease release) =>
        ToastHost.Add($"{release.Version} is out. You are on {CurrentBuild}.", ToastSeverity.Information, "Open it", OpenUpdate);

    /// <summary>
    /// One background re-check for a newer build (AC-188), on the hourly cadence set by <see cref="StartPeriodicUpdateChecks"/>.
    /// Gated by the same <see cref="CheckForUpdatesOnStartup"/> setting as the startup look, and toasts a given release
    /// only once — a build already on offer, or one the operator dismissed, stays quiet. Silent on a failed check.
    /// </summary>
    public async Task RunPeriodicUpdateCheckAsync()
    {
        if (_updates is not { } updates || !CheckForUpdatesOnStartup)
        {
            return;
        }

        UpdateCheckResult result;
        try
        {
            result = await updates.CheckAsync(_Stream);
        }
        catch (Exception)
        {
            // A background poll that cannot reach GitHub says nothing — an error toast for a look nobody asked for is noise.
            return;
        }

        if (result.Release is not { } release)
        {
            return;
        }

        // Captured before _Announce overwrites it, so a release already on offer does not re-toast every hour; the
        // same key builder keeps this comparison and _Announce from drifting.
        var isNewRelease = _offeredRelease != _ReleaseKey(release);

        _Announce(release);

        // A genuinely new build that is actually on screen — a dismissed one that keeps turning up stays quiet.
        if (isNewRelease && UpdateBannerVisible)
        {
            _ToastUpdate(release);
        }
    }

    /// <summary>
    /// Starts the hourly re-check for a newer build (AC-188) while the window stays open, so a long-running cockpit
    /// does not miss a release cut hours after it opened. No-op without an update service, and idempotent — a second
    /// call is ignored rather than starting a second timer. Stopped in <see cref="DisposeAsync"/>. Rides a
    /// DispatcherTimer, like the plugin/managed-CLI check in <c>App</c>: it ticks on the UI thread, so the check
    /// touches its bound state directly without marshalling.
    /// </summary>
    /// <summary>
    /// Starts watching for resumes that have come due (AC-234), and reports whatever lapsed while the cockpit was
    /// closed. Teaches the coordinator how to find a live session, which only the cockpit knows. Idempotent, and a
    /// no-op in the graphs that have no coordinator (unit tests, the designer).
    /// </summary>
    public Task StartScheduledResumesAsync()
    {
        if (ScheduledResumes is not { } coordinator)
        {
            return Task.CompletedTask;
        }

        coordinator.ResolveSession = paneId => Sessions.FirstOrDefault(session => session.PaneId == paneId);
        coordinator.ReopenAndSend = _ReopenAndSendResumeAsync;

        return coordinator.StartAsync();
    }

    public void StartPeriodicUpdateChecks()
    {
        if (_updates is null || _periodicUpdateTimer is not null)
        {
            return;
        }

        _periodicUpdateTimer = new DispatcherTimer { Interval = PeriodicUpdateCheckInterval };
        _periodicUpdateTimer.Tick += (_, _) => _ = RunPeriodicUpdateCheckAsync();
        _periodicUpdateTimer.Start();
    }

    /// <summary>Looks now, because the operator asked (#71). Unlike the startup check, this one says when it could not look at all.</summary>
    public async Task CheckForUpdatesAsync()
    {
        if (_updates is not { } updates)
        {
            return;
        }

        UpdateStatus = "Looking…";
        UpdateUrl = string.Empty;

        var result = await updates.CheckAsync(_Stream);

        if (result.Failure is { } failure)
        {
            // Not "up to date": that would be a lie the operator has every reason to believe.
            UpdateStatus = failure;
            return;
        }

        if (result.Release is { } release)
        {
            _Announce(release);
            return;
        }

        UpdateStatus = $"You are on the newest build ({CurrentBuild}).";
    }

    /// <summary>Opens the release page. The cockpit does not install itself — see IUpdateService for why.</summary>
    [RelayCommand]
    public void OpenUpdate()
    {
        if (UpdateUrl.Length == 0)
        {
            return;
        }

        // Through the shared opener, which also means the release URL now has to be http(s) like every other link the
        // app follows — a release page always is, and anything else was never something to hand to a shell.
        if (!ExternalLink.TryOpen(UpdateUrl))
        {
            // A browser that will not open is not worth taking the cockpit down for; the URL is on screen either way.
            UpdateStatus = $"Could not open a browser. The release is at {UpdateUrl}";
        }
    }

    /// <summary>
    /// Downloads the build on offer and, once the operator confirms, applies it and restarts (AC-388). Only reachable
    /// when <see cref="CanUpdateItself"/> — the view gates the button on it, the NotPackaged copy keeps its
    /// "Open release" link unchanged. A failed or aborted download leaves <see cref="UpdateUrl"/>/the banner/the
    /// offered release exactly as they were (criterion 4): only <see cref="UpdateStatus"/> changes, the same
    /// discipline <see cref="CheckForUpdatesAsync"/> already holds for its own failures.
    /// <para>
    /// Restarting is never automatic (criterion 6): a successful download only asks, through
    /// <see cref="ISessionDialogService.ShowConfirmationDialogAsync"/>, naming how many sessions are still running
    /// (criterion 7) rather than a generic "are you sure?" — a running agent session should not vanish without the
    /// operator being told what is about to take it down. Declining leaves the build downloaded and ready; nothing
    /// is applied until they click again or use <see cref="InstallUpdateOnNextStartAsync"/> instead.
    /// </para>
    /// </summary>
    [RelayCommand]
    public async Task UpdateNowAsync()
    {
        if (_updates is not { } updates || !CanUpdateItself || UpdateUrl.Length == 0 || IsUpdateDownloading)
        {
            return;
        }

        if (!await _DownloadUpdateAsync(updates))
        {
            return;
        }

        if (!await _ConfirmRestartAsync())
        {
            UpdateStatus = "Downloaded. Restart when you are ready, from \"Update now\" or \"Install on next start\".";
            return;
        }

        UpdateStatus = "Restarting…";
        updates.ApplyDownloadedUpdateAndRestart();
    }

    /// <summary>
    /// Downloads the build on offer and applies it the next time the cockpit starts, without touching this session
    /// (criterion 3, criterion 7's conservative alternative to restarting now): <c>WaitExitThenApplyUpdates(silent:
    /// true, restart: false)</c> underneath. Never restarts on its own — that would be exactly the silent apply
    /// criterion 6 rules out.
    /// </summary>
    [RelayCommand]
    public async Task InstallUpdateOnNextStartAsync()
    {
        if (_updates is not { } updates || !CanUpdateItself || UpdateUrl.Length == 0 || IsUpdateDownloading)
        {
            return;
        }

        if (!await _DownloadUpdateAsync(updates))
        {
            return;
        }

        updates.ApplyDownloadedUpdateSilentlyOnNextStart();
        UpdateStatus = $"Downloaded {UpdateName}. It will be installed the next time the cockpit starts.";
    }

    /// <summary>
    /// The download half shared by <see cref="UpdateNowAsync"/> and <see cref="InstallUpdateOnNextStartAsync"/>.
    /// Returns whether it succeeded; a failure already left <see cref="UpdateStatus"/> saying why and touched
    /// nothing else (criterion 4) — the caller has nothing left to do but stop.
    /// </summary>
    private async Task<bool> _DownloadUpdateAsync(IUpdateService updates)
    {
        IsUpdateDownloading = true;
        UpdateDownloadProgress = 0;
        UpdateStatus = "Downloading…";

        try
        {
            // Velopack's progress callback runs on whatever thread its own transfer uses, not necessarily the UI
            // thread (AC-368) — marshalled here the same way _periodicUpdateTimer's tick already is.
            var result = await updates.DownloadAsync(
                _Stream,
                percent => Dispatcher.UIThread.Post(() => UpdateDownloadProgress = percent));

            if (!result.Succeeded)
            {
                UpdateStatus = result.Failure ?? "The download failed.";
                return false;
            }

            return true;
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    /// <summary>
    /// Names how many sessions are running before "Update now" restarts (criterion 7) — never a generic "are you
    /// sure?". <see cref="SessionPanelViewModel.RequiresCloseConfirmation"/> is the same reading the close-confirm
    /// prompt already uses for "is this session doing something a restart would cut off".
    /// </summary>
    private Task<bool> _ConfirmRestartAsync()
    {
        if (_dialogService is not { } dialogs)
        {
            return Task.FromResult(false);
        }

        // _AllSessions(), not Sessions: an embedded agent (an Autopilot step, a plugin-run) is a full session the
        // grid deliberately never lists (AC-391), and restarting kills it exactly as it would a grid session — so
        // counting only Sessions here would tell the operator "nothing running" while one is mid-turn underneath.
        var running = _AllSessions().Where(session => session.RequiresCloseConfirmation).ToList();

        var message = running.Count switch
        {
            0 => $"The cockpit will close and reopen on {UpdateName}.",
            1 => $"1 session is still running ({running[0].Title}) and will be cut off: the cockpit will close and reopen on {UpdateName}.",
            _ => $"{running.Count} sessions are still running ({string.Join(", ", running.Select(session => session.Title))}) and will be cut off: the cockpit will close and reopen on {UpdateName}.",
        };

        return dialogs.ShowConfirmationDialogAsync("Restart now?", message, "Restart now");
    }

    /// <summary>
    /// Hides the update banner (AC-73) for the build now on offer. Per-build, not forever: the operator is saying
    /// "not this one", so a later check that finds a newer build shows the banner again — see <see cref="_Announce"/>.
    /// </summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        _dismissedRelease = _offeredRelease;
        UpdateBannerVisible = false;
    }

    private void _Announce(AppRelease release)
    {
        UpdateUrl = release.Url;
        UpdateName = release.Version;
        UpdateStatus = $"{release.Version} is available.";
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowSelfUpdateButtons));
        OnPropertyChanged(nameof(ShowOpenReleaseButton));

        // The banner shows unless the operator already dismissed this exact build; a newer build always has a
        // different key (see _ReleaseKey) and so returns on its own.
        _offeredRelease = _ReleaseKey(release);
        UpdateBannerVisible = _offeredRelease != _dismissedRelease;
    }

    // The dedup identity of a release. One source of truth: _Announce and the hourly check must key off the same
    // string, or dedup silently breaks.
    private static string _ReleaseKey(AppRelease release) => release.Version;

    /// <summary>The stream the checks ask on: what the channel control says, however it came to say it.</summary>
    private UpdateChannel _Stream => IncludeNightlyBuilds ? UpdateChannel.Nightly : UpdateChannel.Stable;

    // Saves the startup preference without touching the channel: a cockpit that recorded a channel choice because
    // somebody ticked an unrelated box would have exactly the drift AC-387 removes, arriving by the side door.
    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        if (_loadingUpdateSettings)
        {
            return;
        }

        _startupChoiceMade = true;
        _SaveUpdateSettings();
    }

    /// <summary>
    /// Touching the channel is the choice (AC-387). From here on it is the operator's and it wins over what the build
    /// would have implied — including when they set it back to the value the build gave them.
    /// </summary>
    partial void OnIncludeNightlyBuildsChanged(bool value)
    {
        if (_loadingUpdateSettings)
        {
            return;
        }

        _channelChoiceMade = true;
        _chosenChannel = _Stream;
        _SaveUpdateSettings();
    }

    /// <summary>
    /// Writes both settings — but never before the stored ones have been read. A save that went first would persist a
    /// channel this cockpit has not learned yet, writing "nobody chose" over a choice made on an earlier run and
    /// erasing it. Held back instead, and performed by <see cref="InitialiseUpdatesAsync"/> the moment both halves
    /// are known.
    /// </summary>
    private void _SaveUpdateSettings()
    {
        if (_updateSettingsStore is not { } store)
        {
            return;
        }

        if (!_updateSettingsRead)
        {
            _updateSettingsSavePending = true;
            return;
        }

        _ = store.SaveAsync(new UpdateSettings(CheckForUpdatesOnStartup, _chosenChannel));
    }

    /// <summary>
    /// Writes the whole cockpit to <paramref name="archivePath"/> (#70). The view picks the file; this decides what
    /// goes in it, and says afterwards what was left out — a backup without keys is only useful if you know which
    /// ones you will have to enter again.
    /// </summary>
    public async Task CreateBackupAsync(string archivePath)
    {
        if (_backupService is not { } backups)
        {
            return;
        }

        try
        {
            BackupStatus = "Backing up…";

            var chosen = BackupPlugins.Where(plugin => plugin.Selected).Select(plugin => plugin.Id).ToList();

            var manifest = await backups.WriteAsync(
                archivePath,
                new BackupOptions(BackupIncludesCredentials, BackupIncludesProfiles, chosen));

            var stripped = manifest.RemovedSecrets.Count == 0
                ? string.Empty
                : $" {manifest.RemovedSecrets.Count} were left out and must be entered again after a restore.";

            BackupStatus = $"Backed up to {Path.GetFileName(archivePath)}.{stripped}";
        }
        catch (Exception exception)
        {
            BackupStatus = $"The backup was not made: {exception.Message}";
        }
    }

    /// <summary>
    /// Puts the cockpit back from an archive (#70). The archive is read first and the operator is shown what it
    /// carries — the cockpit's own settings, and which plugins — so they choose what comes back rather than
    /// discovering it afterwards. What is replaced is moved aside, not deleted, and the app restarts to read what it
    /// now finds on disk.
    /// </summary>
    /// <param name="archivePath">The backup.</param>
    /// <param name="choose">Asks the operator what to restore; null means they cancelled.</param>
    public async Task RestoreBackupAsync(string archivePath, Func<BackupManifest, Task<RestoreOptions?>> choose)
    {
        if (_backupService is not { } backups)
        {
            return;
        }

        try
        {
            var manifest = await backups.ReadManifestAsync(archivePath);

            if (await choose(manifest) is not { } options)
            {
                return;
            }

            BackupStatus = "Restoring…";
            await backups.RestoreAsync(archivePath, options);

            BackupStatus = "Restored. Restarting the cockpit to read it.";
            _appRestart?.Restart();
        }
        catch (Exception exception)
        {
            BackupStatus = $"Nothing was restored: {exception.Message}";
        }
    }

    /// <summary>
    /// Fills the backup tab's plugin list from what is installed (#70). Called when the Options dialog opens: a plugin
    /// installed since the app started should not be missing from its own backup.
    /// </summary>
    public IReadOnlyList<string> InstalledPluginIds =>
        Plugins.Plugins.Select(plugin => plugin.Discovered.FolderId).ToList();

    public void RefreshBackupPlugins()
    {
        var selected = BackupPlugins
            .Where(plugin => !plugin.Selected)
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        BackupPlugins.Clear();

        foreach (var plugin in Plugins.Plugins)
        {
            var id = plugin.Discovered.FolderId;

            BackupPlugins.Add(new BackupPluginViewModel(id, plugin.Discovered.Manifest.Name is { Length: > 0 } name ? name : id)
            {
                // An operator who unticked something and reopened the dialog meant it.
                Selected = !selected.Contains(id),
            });
        }
    }

    partial void OnShowDebugControlsChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.ShowDebugControls = value;
        }
    }

    // Flips the orchestrator MCP on or off (AC-40) and persists it; it takes effect on the next session's servers.
    partial void OnOrchestratorMcpEnabledChanged(bool value) => _ = _delegationMcpToggle?.SetMcpEnabledAsync(value);

    // The saved left-menu order/visibility per plugin (#72). Plugins register their contributions during phase-2
    // init, which can beat this read; the rebuild below covers that, since the sidebar re-sorts on the event.
    private async Task LoadPluginMenuPreferencesAsync(IPluginRegistrationStore? registrationStore)
    {
        if (registrationStore is null)
        {
            return;
        }

        var registrations = await registrationStore.LoadAllAsync();
        foreach (var (folderId, registration) in registrations)
        {
            _pluginMenuPreferences[folderId] = new PluginMenuPreference(registration.MenuOrder, registration.HiddenInMenu);
        }

        PluginMenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadDebugSettingsAsync()
    {
        if (_debugSettingsStore is null)
        {
            return;
        }

        var settings = await _debugSettingsStore.LoadAsync();
        ShowDebugControls = settings.ShowDebugControls;
    }

    /// <summary>Persists the debug settings edited in the Options dialog to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveDebugSettingsAsync()
    {
        if (_debugSettingsStore is null)
        {
            return;
        }

        await _debugSettingsStore.SaveAsync(new DebugSettings { ShowDebugControls = ShowDebugControls });
        DebugSettingsStatus = "Saved";
    }

    private async Task LoadLayoutSettingsAsync()
    {
        if (_layoutSettingsStore is null)
        {
            return;
        }

        var settings = await _layoutSettingsStore.LoadAsync();
        GlobalSingleSessionLayout = settings.SingleSessionLayout;
        GlobalStackSessionsVertically = settings.StackSessionsVertically;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        SidebarWidth = settings.SidebarWidth;
        SidebarCollapsed = settings.SidebarCollapsed;
    }

    /// <summary>Persists the layout settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveLayoutSettingsAsync()
    {
        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(new LayoutSettings
        {
            SingleSessionLayout = GlobalSingleSessionLayout,
            StackSessionsVertically = GlobalStackSessionsVertically,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            SidebarWidth = SidebarWidth,
            SidebarCollapsed = SidebarCollapsed,
        });
        LayoutSettingsStatus = "Saved";
    }

    /// <summary>
    /// Persists the sidebar width alone (#49), called from the view when the <c>GridSplitter</c> drag
    /// ends — a direct-manipulation UI setting that should save immediately, unlike the Options-dialog
    /// settings above which wait for the dialog's own Save. Clamped before both the property assignment
    /// and the save so an out-of-range drag (shouldn't happen given the column's own min/max) can't
    /// persist.
    /// </summary>
    public async Task SetSidebarWidthAsync(double width)
    {
        SidebarWidth = Math.Clamp(width, LayoutSettings.MinSidebarWidth, LayoutSettings.MaxSidebarWidth);

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(new LayoutSettings
        {
            SingleSessionLayout = GlobalSingleSessionLayout,
            StackSessionsVertically = GlobalStackSessionsVertically,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            SidebarWidth = SidebarWidth,
            SidebarCollapsed = SidebarCollapsed,
        });
    }

    /// <summary>
    /// Collapses or expands the left sidebar and persists it immediately — a direct-manipulation setting like
    /// the width drag, so it survives a restart without waiting for the Options dialog's Save.
    /// </summary>
    [RelayCommand]
    private async Task ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(new LayoutSettings
        {
            SingleSessionLayout = GlobalSingleSessionLayout,
            StackSessionsVertically = GlobalStackSessionsVertically,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            SidebarWidth = SidebarWidth,
            SidebarCollapsed = SidebarCollapsed,
        });
    }

    private async Task LoadWorktreeSettingsAsync()
    {
        if (_worktreeSettingsStore is null)
        {
            return;
        }

        WorktreeRoot = (await _worktreeSettingsStore.LoadAsync()).Root ?? string.Empty;
    }

    /// <summary>Persists the worktree-root override (AC-85); a blank field clears the override, keeping the default.</summary>
    [RelayCommand]
    private async Task SaveWorktreeSettingsAsync()
    {
        if (_worktreeSettingsStore is null)
        {
            return;
        }

        var root = string.IsNullOrWhiteSpace(WorktreeRoot) ? null : WorktreeRoot.Trim();
        await _worktreeSettingsStore.SaveAsync(new WorktreeSettings { Root = root });
        WorktreeRoot = root ?? string.Empty;
        WorktreeSettingsStatus = "Saved";
    }

    private async Task LoadCloneSettingsAsync()
    {
        if (_cloneSettingsStore is null)
        {
            return;
        }

        CloneRoot = (await _cloneSettingsStore.LoadAsync()).Root ?? string.Empty;
    }

    /// <summary>Persists the clones-root override (AC-90); a blank field clears the override, keeping the default.</summary>
    [RelayCommand]
    private async Task SaveCloneSettingsAsync()
    {
        if (_cloneSettingsStore is null)
        {
            return;
        }

        var root = string.IsNullOrWhiteSpace(CloneRoot) ? null : CloneRoot.Trim();
        await _cloneSettingsStore.SaveAsync(new CloneSettings { Root = root });
        CloneRoot = root ?? string.Empty;
        CloneSettingsStatus = "Saved";
    }

    private async Task LoadTerminalSettingsAsync()
    {
        if (_terminalSettingsStore is null)
        {
            return;
        }

        var settings = await _terminalSettingsStore.LoadAsync();
        TerminalFontFamily = settings.FontFamily;
        TerminalFontSize = settings.FontSize;
        SyncTerminalFontSelectionFromFamily();
        _BuildTerminalShellChoices(settings.Shell);
    }

    /// <summary>
    /// (Re)builds the Options default-shell picker (#AC-25) from the shells detected now, and selects the one the
    /// saved <paramref name="configured"/> value names (its <see cref="ShellDescriptor.Id"/>, matched
    /// case-insensitively) — falling back to "OS default" when it is blank or no longer resolves on this machine.
    /// </summary>
    private void _BuildTerminalShellChoices(string configured)
    {
        var shells = ShellCatalog.Detect();

        TerminalShellChoices.Clear();
        var osDefaultLabel = shells.Count > 0 ? $"OS default ({shells[0].DisplayName})" : "OS default";
        TerminalShellChoices.Add(new TerminalShellChoice(osDefaultLabel, string.Empty));
        foreach (var shell in shells)
        {
            TerminalShellChoices.Add(new TerminalShellChoice($"{shell.DisplayName} ({shell.Id})", shell.Id));
        }
        // The escape hatch for a third-party shell not detected here (fish, nushell, a wrapper) — common on
        // Linux/macOS. Selecting it reveals a free-text box; the typed path/command is what gets persisted.
        TerminalShellChoices.Add(new TerminalShellChoice("Custom…", CustomShellChoiceValue));

        var value = configured?.Trim() ?? string.Empty;
        var detected = value.Length == 0
            ? null
            : TerminalShellChoices.FirstOrDefault(choice =>
                choice.Value != CustomShellChoiceValue && string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));

        if (value.Length == 0)
        {
            TerminalCustomShell = string.Empty;
            SelectedTerminalShell = TerminalShellChoices[0];
        }
        else if (detected is not null)
        {
            TerminalCustomShell = string.Empty;
            SelectedTerminalShell = detected;
        }
        else
        {
            // A saved path/command that is not a detected shell id — restore it as the custom entry. Set the text
            // before the selection so the reveal (OnSelectedTerminalShellChanged) already has it.
            TerminalCustomShell = value;
            SelectedTerminalShell = TerminalShellChoices.First(choice => choice.Value == CustomShellChoiceValue);
        }
    }

    /// <summary>Persists the TTY terminal-appearance settings (#40) edited in the Options dialog to <c>cockpit.json</c>, clamping the font size to the supported range.</summary>
    [RelayCommand]
    private async Task SaveTerminalSettingsAsync()
    {
        if (_terminalSettingsStore is null)
        {
            return;
        }

        var fontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily)
            ? "Cascadia Mono, Consolas, monospace"
            : TerminalFontFamily.Trim();
        var fontSize = Math.Clamp(TerminalFontSize, TerminalSettings.MinFontSize, TerminalSettings.MaxFontSize);

        // Custom persists the typed path/command; a detected shell persists its id; OS default persists blank.
        var shell = IsTerminalShellCustom
            ? TerminalCustomShell.Trim()
            : SelectedTerminalShell?.Value?.Trim() ?? string.Empty;

        await _terminalSettingsStore.SaveAsync(new TerminalSettings { FontFamily = fontFamily, FontSize = fontSize, Shell = shell });
        TerminalFontFamily = fontFamily;
        TerminalFontSize = fontSize;
        TerminalSettingsStatus = "Saved";
    }

    private async Task LoadVoiceSettingsAsync()
    {
        if (_voiceSettingsStore is null)
        {
            return;
        }

        var settings = await _voiceSettingsStore.LoadAsync();
        VoiceEnabled = settings.IsEnabled;
        VoiceModelName = settings.ModelName;
        // Reopen on the "Auto ★" item, a preset, or "Custom…" per what was saved (AC-68). On Auto, refresh the
        // effective model to the current recommendation so a hardware change since last save is reflected.
        _transcriptionModelAuto = settings.ModelAutoSelected;
        if (_transcriptionModelAuto && _transcriptionRecommendation is { } recommendation)
        {
            VoiceModelName = recommendation.Model;
        }

        _SyncTranscriptionModelFromName();
        // A GPU preference saved on a machine that can no longer load it (config moved, driver gone) has no matching
        // host-aware option and falls back to Auto rather than showing a dead entry.
        SelectedVoiceBackendPreference = VoiceBackendPreferences.FirstOrDefault(option => option.Value == settings.BackendPreference)
                                         ?? VoiceBackendPreferences[0];
        _UpdateTranscriptionAdvice();
        VoiceCleanupEnabled = settings.CleanupEnabled;
        // Suppress the per-property refresh hooks while loading: setting auto-detect, the server preference and the
        // model each triggers a voice-LLM refresh, and three overlapping refreshes racing on the model list is what
        // left the dropdown empty. OptionsAsync runs one refresh after the load instead.
        _suppressVoiceLlmHooks = true;
        VoiceAutoDetectLocalLlm = settings.AutoDetectLocalLlm;
        SelectedLocalLlmPreference = LocalLlmPreferences.FirstOrDefault(option => option.Value == settings.LocalLlmPreference)
                                     ?? LocalLlmPreferences[0];
        VoiceLlmModel = string.IsNullOrWhiteSpace(settings.VoiceLlmModel) ? AutoModel : settings.VoiceLlmModel;
        VoiceLlmBaseUrl = settings.VoiceLlmBaseUrl;
        _suppressVoiceLlmHooks = false;
        VoicePushToTalkKeyName = settings.PushToTalkKeyName;
        VoiceGlobalPushToTalk = settings.GlobalPushToTalk;
        // First load is app startup — capture what the hotkey actually armed with, so a later save can tell a real
        // change from a toggle-and-back. Reopening the Options dialog reloads but must not move the baseline.
        _voiceGlobalPushToTalkRunning ??= settings.GlobalPushToTalk;
        VoiceAutoSubmit = settings.AutoSubmitAfterVoice;
        VoiceOpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs;
        VoiceStopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking;
        VoiceStopReadAloudLevelThreshold = (decimal)settings.StopReadAloudLevelThreshold;
        SelectedReadAloudMode = ReadAloudModes.FirstOrDefault(mode => mode.Value == settings.ReadAloudMode) ?? ReadAloudModes[0];
        SelectedTurnAckMode = TurnAckModes.FirstOrDefault(mode => mode.Value == settings.TurnAckMode) ?? TurnAckModes[1];
        SelectedTtsVoice = TtsVoices.FirstOrDefault(voice => voice.Sid == settings.TtsVoiceSid) ?? TtsVoiceCatalog.Default;
        SelectedReadAloudLanguage = ReadAloudLanguages.FirstOrDefault(language => language.Code == settings.ReadAloudLanguage) ?? ReadAloudLanguages[0];
        SelectedSttLanguage = SttLanguages.FirstOrDefault(language => language.Code == settings.SttLanguage) ?? SttLanguages[0];

        // Re-seed the dropdown against the just-loaded model so it holds "Auto" + the saved model (even a
        // server-specific one) before the async probe returns — the box is never empty, not even for a blink.
        _PopulateVoiceLlmModels([]);

        // Show this machine's last calibration if it has ever been run here (AC-68 slice 3).
        if (_transcriptionCalibrationStore is not null && await _transcriptionCalibrationStore.LoadAsync() is { } calibration)
        {
            _ApplyCalibration(calibration);
        }
    }

    // Re-queries the audio backend so a freshly plugged-in device appears, keeping a "System default"
    // entry at the top, and reselects the saved device. Called when the Options dialog opens rather than
    // at startup: enumerating devices spins up the audio backend, which we only want to touch once the
    // operator actually goes to change it — not on every launch. No-op without a provider (previewer).
    private async Task _RefreshAudioDevicesAsync()
    {
        if (_audioDeviceProvider is null || _voiceSettingsStore is null)
        {
            return;
        }

        var settings = await _voiceSettingsStore.LoadAsync();
        // Enumerating spins up the native audio backend, which can block briefly on first use — run it off
        // the UI thread; the await resumes on the UI thread (captured context) to touch the collections.
        var provider = _audioDeviceProvider;
        var inputDevices = await Task.Run(provider.GetInputDevices);
        var outputDevices = await Task.Run(provider.GetOutputDevices);
        _PopulateDevices(InputDevices, inputDevices);
        _PopulateDevices(OutputDevices, outputDevices);
        SelectedInputDevice = InputDevices.FirstOrDefault(device => device.DeviceName == _NullIfEmpty(settings.InputDeviceName)) ?? InputDevices[0];
        SelectedOutputDevice = OutputDevices.FirstOrDefault(device => device.DeviceName == _NullIfEmpty(settings.OutputDeviceName)) ?? OutputDevices[0];
    }

    /// <summary>
    /// Resolves what the shared voice-LLM step would actually use (for the "auto will use…" summary) and refreshes
    /// the model dropdown from that same server's <c>/v1/models</c> when the Options dialog opens or the auto-detect
    /// toggle / server preference change. Seeded with the current selection and the advised models (gemma3:4b for
    /// Dutch, qwen2.5:3b as a safe fallback) so the list is never empty and the saved model stays selected even when
    /// the server is unreachable — both the resolver and catalog fail soft, never throwing.
    /// </summary>
    private async Task _RefreshVoiceLlmAsync()
    {
        // Coalesce: if a refresh is already running, ask it to run once more when it finishes rather than racing it
        // — overlapping refreshes each Clear()ing the model list is what emptied the dropdown.
        if (_voiceLlmRefreshing)
        {
            _voiceLlmRefreshQueued = true;
            return;
        }

        _voiceLlmRefreshing = true;
        try
        {
            do
            {
                _voiceLlmRefreshQueued = false;

                IReadOnlyList<string> discovered = [];
                using (var cts = new CancellationTokenSource(LlmProbeTimeout))
                {
                    try
                    {
                        var endpoint = await _ResolveVoiceLlmEndpointAsync(cts.Token);
                        _UpdateAutoSummary(endpoint);

                        // List from the server that will actually be used — the resolved one in auto mode, the
                        // manual URL otherwise — so the dropdown offers what is really installed there.
                        var listFrom = endpoint?.BaseUrl ?? VoiceLlmBaseUrl;
                        if (_modelCatalog is not null)
                        {
                            discovered = await _modelCatalog.ListModelsAsync(listFrom, cancellationToken: cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // A slow/hung local server: keep the seeded list below rather than blocking on it.
                    }
                }

                _PopulateVoiceLlmModels(discovered);
            }
            while (_voiceLlmRefreshQueued);
        }
        finally
        {
            _voiceLlmRefreshing = false;
        }
    }

    /// <summary>
    /// Rebuilds the model dropdown from the current selection + advised models (gemma3:4b for Dutch, qwen2.5:3b as
    /// a safe fallback) plus whatever the server reported, preserving the selection. Hooks are suppressed during
    /// the rebuild so the Clear()'s null-selection writeback through the ComboBox and the reselect below do not
    /// trigger another refresh mid-flight. The two advised models are literals, so the list is never empty.
    /// </summary>
    private void _PopulateVoiceLlmModels(IReadOnlyList<string> discovered)
    {
        // "Auto" is always first, so the dropdown is never empty and always shows that an automatic choice is on
        // the table; then the advised models and whatever the server reported.
        var desired = new List<string> { AutoModel };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AutoModel };
        foreach (var model in new[] { VoiceLlmModel, "gemma3:4b", "qwen2.5:3b" }.Concat(discovered))
        {
            if (!string.IsNullOrWhiteSpace(model) && seen.Add(model))
            {
                desired.Add(model);
            }
        }

        var selection = desired.FirstOrDefault(model => string.Equals(model, VoiceLlmModel, StringComparison.OrdinalIgnoreCase))
                        ?? AutoModel;

        var wasSuppressed = _suppressVoiceLlmHooks;
        _suppressVoiceLlmHooks = true;
        try
        {
            VoiceLlmModels.Clear();
            foreach (var model in desired)
            {
                VoiceLlmModels.Add(model);
            }

            VoiceLlmModel = selection;
        }
        finally
        {
            _suppressVoiceLlmHooks = wasSuppressed;
        }
    }

    /// <summary>Re-resolves and refreshes only the "auto will use…" summary — used when the preferred model changes, without rebuilding the dropdown the operator is interacting with.</summary>
    private async Task _RefreshVoiceLlmSummaryAsync()
    {
        using var cts = new CancellationTokenSource(LlmProbeTimeout);
        try
        {
            _UpdateAutoSummary(await _ResolveVoiceLlmEndpointAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            // Slow server — leave the last summary rather than hanging on the probe.
        }
    }

    private async Task<LocalLlmEndpoint?> _ResolveVoiceLlmEndpointAsync(CancellationToken cancellationToken) =>
        _localLlmEndpointResolver is null ? null : await _localLlmEndpointResolver.ResolveAsync(_CurrentVoiceLlmSettings(), cancellationToken);

    // The LLM-relevant subset of the current (possibly unsaved) Options edits, so the resolver reflects what the
    // operator is looking at rather than what is on disk.
    private VoiceSettings _CurrentVoiceLlmSettings() => new()
    {
        AutoDetectLocalLlm = VoiceAutoDetectLocalLlm,
        LocalLlmPreference = SelectedLocalLlmPreference.Value,
        VoiceLlmModel = _VoiceLlmModelSetting(),
        VoiceLlmBaseUrl = string.IsNullOrWhiteSpace(VoiceLlmBaseUrl) ? "http://localhost:11434" : VoiceLlmBaseUrl.Trim(),
    };

    // The model as it is stored: "Auto" (and blank) become the empty id the resolver reads as "let auto-detect choose".
    private string _VoiceLlmModelSetting() =>
        string.IsNullOrWhiteSpace(VoiceLlmModel) || string.Equals(VoiceLlmModel, AutoModel, StringComparison.OrdinalIgnoreCase)
            ? ""
            : VoiceLlmModel.Trim();

    // Only spelled out in auto mode; in manual mode the operator set the endpoint themselves, so there is nothing to
    // reveal. When a preferred model is set but the detected server does not have it, the line says so — otherwise
    // the dropdown (the preference) and this line (what is actually used) look like they disagree for no reason.
    private void _UpdateAutoSummary(LocalLlmEndpoint? endpoint)
    {
        if (!VoiceAutoDetectLocalLlm || endpoint is not { } resolved)
        {
            VoiceLlmAutoSummary = string.Empty;
            return;
        }

        var preferred = _VoiceLlmModelSetting();
        VoiceLlmAutoSummary = string.IsNullOrEmpty(preferred) || string.Equals(preferred, resolved.Model, StringComparison.OrdinalIgnoreCase)
            ? $"The voice LLM will use “{resolved.Model}” at {resolved.BaseUrl}"
            : $"“{preferred}” isn't on the detected server — using “{resolved.Model}” at {resolved.BaseUrl}";
    }

    private static void _PopulateDevices(ObservableCollection<AudioDeviceOption> target, IReadOnlyList<AudioDeviceInfo> devices)
    {
        target.Clear();
        target.Add(new AudioDeviceOption("System default", null));
        foreach (var device in devices)
        {
            var label = device.IsSystemDefault ? $"{device.Name} (default)" : device.Name;
            target.Add(new AudioDeviceOption(label, device.Name));
        }
    }

    private static string? _NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Persists the voice settings edited in the Options flyout to <c>cockpit.json</c>. Open sessions
    /// re-read the setting the next time they start a push-to-talk hold — no live-push needed, since
    /// <see cref="SessionPanelViewModel.BeginVoiceHold"/> only gates on the enabled flag it loaded once
    /// at session creation, the same "settings apply to new sessions" behaviour as the profile picker.
    /// </summary>
    [RelayCommand]
    private async Task SaveVoiceSettingsAsync()
    {
        if (_voiceSettingsStore is null)
        {
            return;
        }

        // Open-mic on/off is owned by the runtime toggle button, not this dialog — preserve its current
        // persisted value so saving the Options never flips the mic off behind the operator's back.
        var current = await _voiceSettingsStore.LoadAsync();

        await _voiceSettingsStore.SaveAsync(new VoiceSettings
        {
            IsEnabled = VoiceEnabled,
            ModelName = string.IsNullOrWhiteSpace(VoiceModelName) ? "large-v3-turbo" : VoiceModelName.Trim(),
            ModelAutoSelected = _transcriptionModelAuto,
            BackendPreference = SelectedVoiceBackendPreference.Value,
            CleanupEnabled = VoiceCleanupEnabled,
            AutoDetectLocalLlm = VoiceAutoDetectLocalLlm,
            LocalLlmPreference = SelectedLocalLlmPreference.Value,
            VoiceLlmModel = _VoiceLlmModelSetting(),
            VoiceLlmBaseUrl = string.IsNullOrWhiteSpace(VoiceLlmBaseUrl) ? "http://localhost:11434" : VoiceLlmBaseUrl.Trim(),
            PushToTalkKeyName = string.IsNullOrWhiteSpace(VoicePushToTalkKeyName) ? "F9" : VoicePushToTalkKeyName.Trim(),
            GlobalPushToTalk = VoiceGlobalPushToTalk,
            AutoSubmitAfterVoice = VoiceAutoSubmit,
            OpenMicEnabled = current.OpenMicEnabled,
            OpenMicSilenceTimeoutMs = VoiceOpenMicSilenceTimeoutMs > 0 ? VoiceOpenMicSilenceTimeoutMs : 800,
            StopReadAloudWhenSpeaking = VoiceStopReadAloudWhenSpeaking,
            StopReadAloudLevelThreshold = (double)VoiceStopReadAloudLevelThreshold,
            ReadAloudMode = SelectedReadAloudMode.Value,
            TurnAckMode = SelectedTurnAckMode.Value,
            TtsVoiceSid = SelectedTtsVoice.Sid,
            ReadAloudLanguage = SelectedReadAloudLanguage.Code,
            SttLanguage = SelectedSttLanguage.Code,
            InputDeviceName = SelectedInputDevice.DeviceName ?? "",
            OutputDeviceName = SelectedOutputDevice.DeviceName ?? "",
        });

        // Push the read-aloud settings to already-open sessions so toggling naturalization or the voice
        // takes effect immediately, rather than only on the next session (the enabled/PTT flags keep the
        // load-at-start behaviour, which the hold path re-reads).
        foreach (var session in Sessions)
        {
            session.ReadAloudMode = SelectedReadAloudMode.Value;
            session.TurnAckMode = SelectedTurnAckMode.Value;
            session.TtsVoiceSid = SelectedTtsVoice.Sid;
            session.ReadAloudLanguage = SelectedReadAloudLanguage.Code;
        }

        VoiceSettingsStatus = "Saved";

        // On Linux the global hotkey is a desktop-portal binding the compositor only takes at startup, so a change
        // to it there needs a restart — unlike Windows, where the re-arm below applies it live.
        VoiceGlobalPushToTalkNeedsRestart =
            ShouldOfferGlobalPushToTalkRestart(IsLinuxPlatform, _voiceGlobalPushToTalkRunning, VoiceGlobalPushToTalk);

        // The global hotkey is armed from these, and arming happened once at startup — so changing the key saved
        // it and left the hook on the old one, and switching global push-to-talk off left it running, both for
        // the rest of the session and both silently. Raised rather than called: VoicePushToTalkCoordinator takes
        // this view model, so injecting it back here is a circle the container walks forever — the same reason
        // the toasts go through ToastHost.
        VoiceSettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised once the voice settings are saved, so whatever was configured from them can re-apply. See the remarks on the raise site.</summary>
    public event EventHandler? VoiceSettingsSaved;

    /// <summary>Whether a "Restart now" affordance can do anything — false in the design-time constructor, where there is no real app to restart.</summary>
    public bool CanRestartApp => _appRestart is not null;

    /// <summary>Restarts the app so a saved change that only applies at startup (the Linux global hotkey) takes effect, without the operator closing and relaunching by hand. See <see cref="VoiceGlobalPushToTalkNeedsRestart"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanRestartApp))]
    private void RestartApp() => _appRestart?.Restart();

    /// <summary>
    /// Whether saving global push-to-talk should offer a restart: only on Linux (elsewhere the change applies
    /// live), and only when the saved value differs from what this process armed with at startup — so toggling it
    /// and back offers nothing. Pulled out so the platform-gated decision is testable off Linux.
    /// </summary>
    internal static bool ShouldOfferGlobalPushToTalkRestart(bool isLinux, bool? runningValue, bool savedValue) =>
        isLinux && runningValue is bool running && running != savedValue;

    [RelayCommand]
    private async Task RecordAudioAsync()
    {
        if (_captureService is null)
        {
            return;
        }

        _recordedPcm.Clear();
        _recordingCancellation = new CancellationTokenSource();
        AudioStatus = "Recording...";

        try
        {
            await foreach (var frame in _captureService.CaptureAsync(AudioFormat, _recordingCancellation.Token))
            {
                _recordedPcm.AddRange(frame.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when StopRecordingAudio cancels the capture stream.
        }

        AudioStatus = $"Recorded {_recordedPcm.Count} bytes.";
    }

    [RelayCommand]
    private void StopRecordingAudio()
    {
        _recordingCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task PlayAudioAsync()
    {
        if (_playbackService is null || _recordedPcm.Count == 0)
        {
            AudioStatus = "Nothing recorded yet.";
            return;
        }

        AudioStatus = "Playing...";
        await _playbackService.PlayAsync(_recordedPcm.ToArray(), AudioFormat);
        AudioStatus = "Playback done.";
    }

    /// <summary>
    /// Opens the New-session dialog — SDK vs TTY is now chosen inside it (#32) — and, once confirmed,
    /// mints the matching session: SDK (headless stream-json rendered as the chat UI) or TTY (the real
    /// interactive <c>claude</c> TUI in a terminal panel, the #9 experiment), started immediately with
    /// the chosen profile and start options.
    /// </summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_sessionFactory is null || _ttySessionFactory is null || _dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync();
        if (result is null)
        {
            return;
        }

        await _LaunchSessionFromResultAsync(result);
    }

    /// <summary>
    /// Opens a plain terminal pane (#AC-25) next to the AI sessions, running the operator's chosen default shell —
    /// or, when none is configured, the OS default the <see cref="ShellCatalog"/> detects. Reuses the whole TTY
    /// path: a terminal is another <see cref="TtyViewModel"/> in the <see cref="Sessions"/> collection, so the grid,
    /// reorder and lifecycle are the existing ones. Runtime-only, exactly like an AI session.
    /// </summary>
    [RelayCommand]
    private void NewTerminal()
    {
        if (_ttySessionFactory is null)
        {
            return;
        }

        var shell = _ResolveDefaultShell();
        if (shell is null)
        {
            return;
        }

        var terminal = _ttySessionFactory();
        AddSession(terminal, name: null, shell.DisplayName);
        terminal.LaunchTerminal(shell);
    }

    /// <summary>
    /// The shell a new terminal opens (#AC-25): the operator's configured default when it is set and still resolves
    /// on this machine (matched by <see cref="ShellDescriptor.Id"/> or absolute path, so a configured "pwsh" survives
    /// a machine where its path differs), otherwise the OS default — the first shell <see cref="ShellCatalog"/>
    /// detects. Null only when the machine has no resolvable shell at all, which is near-impossible.
    /// </summary>
    private ShellDescriptor? _ResolveDefaultShell()
    {
        var shells = ShellCatalog.Detect();

        // The effective configured value: the typed path/command when on "Custom…", else the picked shell's id.
        var configured = (IsTerminalShellCustom ? TerminalCustomShell : SelectedTerminalShell?.Value)?.Trim();
        if (string.IsNullOrEmpty(configured) || configured == CustomShellChoiceValue)
        {
            return shells.Count > 0 ? shells[0] : null;
        }

        var match = shells.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, configured, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.ExecutablePath, configured, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        // A custom third-party shell (a path or command not in the detected list); resolve it directly, falling
        // back to the OS default only when even that yields nothing (a blank command).
        return ShellCatalog.ForCommand(configured) ?? (shells.Count > 0 ? shells[0] : null);
    }

    /// <summary>
    /// Opens a session on <paramref name="profile"/> for a plugin (#69) — a workflow step, a shortcut — and hands it
    /// <paramref name="prompt"/> as its first input. The profile's own defaults decide model, permissions and effort:
    /// naming a profile means "the way I set that one up", and a caller who knew better would have said so.
    /// Returns the name the session carries, so the caller can say which one it started.
    /// <para>
    /// <paramref name="sessionName"/> names it outright, the way the New-session dialog's own name field does — a flow
    /// that starts a session on a ticket should not have to open it as "Claude — 14:22" and rename it a step later
    /// (#AC-312). Left blank, the profile and the clock name it as before.
    /// </para>
    /// </summary>
    public async Task<string> StartSessionForPluginAsync(SessionProfile profile, string? prompt, string? workingDirectory, string? sessionName = null)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? $"{profile.Label} — {DateTime.Now:HH:mm}" : sessionName.Trim();

        // An SDK session, always: a plugin's prompt is text handed to a session, and a TTY is a terminal a human
        // drives. Starting one and typing into it on someone's behalf is not the same act at all.
        // A plugin profile carries its start defaults in the generic OptionDefaults map; the typed Mode/Model/Effort
        // are the retired Claude-CLI vocabulary and unused for a plugin launch, so they pass app defaults here.
        var result = new NewSessionResult(
            SessionKind.Sdk,
            profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            name,
            WorkingDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            SdkLaunchOptions: profile.Defaults?.OptionDefaults,
            // No operator said which project this one is for and no session it descends from, so the folder answers
            // (AC-320) — the same rule an embedded run is placed by. Without it a plugin-started session belongs to
            // no project, and everything a project decides at start stays silent for it.
            ProjectId: await _ProjectIdForDirectoryAsync(workingDirectory))
        {
            // "<profile> — 14:22" is composed here; a name the caller actually passed is a decision (#AC-312).
            NameIsComposed = string.IsNullOrWhiteSpace(sessionName),
        };

        var paneId = await _LaunchSessionFromResultAsync(result);

        // The prompt goes in after the session exists, through the same seam a plugin's inject uses — a session that
        // is not up yet cannot be typed into, and pretending otherwise loses the prompt. Target the started pane by its
        // id rather than "the last one added", so a session opened concurrently cannot catch the prompt by accident.
        if (paneId is not null && !string.IsNullOrWhiteSpace(prompt))
        {
            Sessions.FirstOrDefault(session => session.PaneId == paneId)?.InjectText(prompt);
        }

        return name;
    }

    /// <summary>
    /// Opens the New-session dialog on a plugin's behalf (#AC-96), optionally pre-filled from <paramref name="prefill"/>,
    /// starts the session the operator confirms, and returns its <see cref="SessionPanelViewModel.PaneId"/> — or null when
    /// the operator cancels or nothing could be started. The whole dialog is shown, so the operator sees and can change
    /// every field before anything starts; a <see cref="NewSessionPrefill.InitialPrompt"/> is injected into the started
    /// session through the same seam a plugin's inject uses, so it lands in the composer for the operator to send.
    /// </summary>
    public async Task<string?> ShowNewSessionDialogForPluginAsync(NewSessionPrefill? prefill)
    {
        if (_dialogService is null)
        {
            return null;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync(
            prefill,
            project: await _ProjectLinkedAsAsync(prefill?.LinkedProject));
        if (result is null)
        {
            return null;
        }

        var paneId = await _LaunchSessionFromResultAsync(result);
        if (paneId is not null && !string.IsNullOrWhiteSpace(prefill?.InitialPrompt))
        {
            Sessions.FirstOrDefault(session => session.PaneId == paneId)?.InjectText(prefill.InitialPrompt);
        }

        return paneId;
    }

    /// <summary>
    /// The project a plugin's prefill named by its link (AC-419) — "the one tracked in YouTrack's AC" — handed to the
    /// dialog through the project parameter the operator's own project pick already uses (AC-164), so a preselected
    /// project brings its folder, profile, worktree default and MCP overlay exactly as picking it by hand would.
    /// Null for no link, a link nothing declares, or one two projects declare; the dialog then opens on no project.
    /// </summary>
    private async Task<Project?> _ProjectLinkedAsAsync(ProjectLink? link)
    {
        if (link is null)
        {
            return null;
        }

        // The projects list is filled by a fire-and-forget read at startup, so a plugin that opens this dialog early —
        // a shortcut pressed while the cockpit is still settling — can get here first and find it empty. Reading it now
        // in that case is the difference between "no project links that" and "the list was not there yet". Guarded like
        // _ProjectIdForDirectoryAsync's read for the same reason: an unreadable list costs a preselection, while an
        // exception escaping here would reach the host's catch and cancel the dialog outright — no session at all
        // because a convenience could not be worked out.
        if (Projects.Projects.Count == 0)
        {
            try
            {
                await Projects.LoadAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        return ProjectLinkMatch.For(Projects.Projects, link.FieldKey, link.Value);
    }

    /// <summary>
    /// Starts a session on <paramref name="project"/> with the project's own defaults and no dialog (AC-164) — the
    /// sidebar's ▶ and the launcher's Start. What it opens with is <see cref="ProjectQuickStart"/>'s to answer; this
    /// only launches it, through the same path the dialog's result takes.
    /// </summary>
    [RelayCommand]
    private async Task StartProjectSessionAsync(Project? project)
    {
        if (project is null)
        {
            return;
        }

        if (_projectQuickStart is not null && await _projectQuickStart.ComposeAsync(project) is { } result)
        {
            // A second session on the same project is named "Cockpit 2", not a second "Cockpit": the dialog path
            // numbers its generated names, and two identical rows in the sidebar is exactly the confusion that
            // numbering exists to prevent.
            // Only the name changes; that it is composed came with the result, and stays with it (#AC-324).
            await _LaunchSessionFromResultAsync(result with { SessionName = _UniqueSessionTitle(project.Name) });

            return;
        }

        // The project names no profile that still exists, so there is nothing to start it on. Ask rather than fail
        // quietly: the dialog opens on the project, leaving the operator only the choice the project cannot make.
        await NewSessionForProjectAsync(project);
    }

    /// <summary>
    /// Opens the New-session dialog on <paramref name="project"/> (AC-164) — the "New session…" next to the quick
    /// start, for when the operator wants to change something the project would otherwise decide.
    /// </summary>
    [RelayCommand]
    private async Task NewSessionForProjectAsync(Project? project)
    {
        if (project is null || _dialogService is null)
        {
            return;
        }

        if (await _dialogService.ShowNewSessionDialogAsync(project: project) is { } result)
        {
            await _LaunchSessionFromResultAsync(result);
        }
    }

    /// <summary><paramref name="title"/> if no session carries it, else "<paramref name="title"/> 2", "… 3" — the first free one.</summary>
    private string _UniqueSessionTitle(string title)
    {
        var taken = Sessions.Select(session => session.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(title))
        {
            return title;
        }

        var suffix = 2;
        while (taken.Contains($"{title} {suffix}"))
        {
            suffix++;
        }

        return $"{title} {suffix}";
    }

    /// <summary>Opens <paramref name="project"/>'s folder in the operating system's own file manager — the same shell hand-off the worktrees dialog uses.</summary>
    [RelayCommand]
    private void OpenProjectFolder(Project? project)
    {
        if (project?.SourceDirectory is not { Length: > 0 } directory || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No handler to open a folder (a headless or unusual environment) — better to do nothing than crash.
        }
    }

    /// <summary>Opens the project editor for <paramref name="project"/> from the sidebar, persisting through the same manager the Options tab uses.</summary>
    [RelayCommand]
    private Task EditProjectAsync(Project? project) =>
        project is null ? Task.CompletedTask : Projects.EditAsync(project);

    // Mints and starts the matching session (SDK chat or TTY terminal) from a confirmed result, recording
    // the result on the panel so the context-menu Duplicate can replay it. Returns the started session's PaneId
    // (#AC-96) so a caller that opened the dialog on a plugin's behalf can hand that id back — null when nothing
    // started (no factories, or isolation failed and running unisolated was declined).
    private async Task<string?> _LaunchSessionFromResultAsync(NewSessionResult result)
    {
        if (_sessionFactory is null || _ttySessionFactory is null)
        {
            return null;
        }

        SessionPanelViewModel session = result.Kind == SessionKind.Sdk ? _sessionFactory() : _ttySessionFactory();
        session.LaunchResult = result;
        AddSession(session, result.SessionName, result.Profile.Label, result.NameIsChosen);

        // AC-410: written now, before the session actually starts — see _PersistNewSessionPane for why this order
        // is the crash-safe one.
        _PersistNewSessionPane(session, result);

        return await _StartSessionAsync(session, result);
    }

    /// <summary>
    /// The starting half of a session launch (AC-410): worktree/working-directory resolution through to
    /// <see cref="ProjectsViewModel.MarkOpenedAsync"/> — everything <see cref="_LaunchSessionFromResultAsync"/>
    /// used to do after minting and attaching the panel. Split out so a restore (which only ever attaches,
    /// never starts) does not carry this half, and reused as-is by the fresh-launch path above.
    /// </summary>
    private async Task<string?> _StartSessionAsync(SessionPanelViewModel session, NewSessionResult result)
    {
        string paneId;
        string? startedWorkingDirectory;
        string? startedPermissionMode;
        if (session is SessionViewModel sdkSession)
        {
            string? workingDirectory;
            try
            {
                workingDirectory = await _ResolveIsolatedWorkingDirectoryAsync(sdkSession, result);
            }
            catch (OperationCanceledException)
            {
                // Isolation failed and running unisolated was declined — undo the half-added session (which also
                // removes its just-written pane record, via CloseSessionAsync) rather than starting it in the
                // operator's real working tree.
                await CloseSessionAsync(sdkSession);
                return null;
            }

            sdkSession.ProjectId = result.ProjectId;
            await sdkSession.StartConfiguredAsync(result.Profile, result.Mode, result.Model, result.Effort, result.EnabledMcpServerNames, workingDirectory, result.Resume, result.SdkLaunchOptionsWithInstructions, result.ReadingLevel);
            paneId = sdkSession.PaneId;
            startedWorkingDirectory = workingDirectory;
            startedPermissionMode = result.Mode.Value;
        }
        else
        {
            var ttySession = (TtyViewModel)session;
            string? workingDirectory;
            try
            {
                workingDirectory = await _ResolveIsolatedWorkingDirectoryAsync(ttySession, result);
            }
            catch (OperationCanceledException)
            {
                // Isolation failed and running unisolated was declined — undo the half-added session (which also
                // removes its just-written pane record, via CloseSessionAsync) rather than starting it in the
                // operator's real working tree.
                await CloseSessionAsync(ttySession);
                return null;
            }

            // Claude's permission-mode/model/effort are its own vocabulary, not every provider's — a plugin
            // TTY provider (Codex, say) gets its own declared options via PluginTtyOptions instead, and never
            // both for the same launch (see NewSessionResult.PluginTtyOptions).
            var isClaudeProfile = result.Profile.Provider is SessionProvider.ClaudeCli;
            ttySession.ProjectId = result.ProjectId;

            // AC-165: what the plugins give this session, resolved from the pane now that it has a project — the
            // same contribution the SDK route folds in at start, so a TTY session gets the same answer.
            var contributed = _sessionResourceResolver is null
                ? SessionResources.Empty
                : await _sessionResourceResolver.ResolveAsync(ttySession.PaneId);

            ttySession.LaunchConfigured(
                result.Profile,
                isClaudeProfile ? result.Mode.Value : null,
                isClaudeProfile ? result.Model.Value : null,
                isClaudeProfile ? result.Effort.Value : null,
                workingDirectory,
                result.Resume,
                result.TtyLaunchOptionsWithInstructions,
                // #44: the per-session MCP checklist, so a TTY session honours the operator's selection instead of
                // loading every eligible server (the same set the SDK path passes to StartConfiguredAsync above).
                result.EnabledMcpServerNames,
                contributed);
            paneId = ttySession.PaneId;
            startedWorkingDirectory = workingDirectory;
            startedPermissionMode = isClaudeProfile ? result.Mode.Value : null;
        }

        // A new session may have created (or reattached) a worktree; keep the status-bar counter current.
        _ = Worktrees.RefreshCountAsync();

        // AC-409: enough to bring this pane's session back after a restart or a crash. Written once here rather
        // than at two separate "session started"/"worktree coupled" moments: by this point isolation has already
        // resolved (session.WorktreeBranch is set when it applied), so a second write immediately after this one
        // would say nothing new. Fire-and-forget like the worktree count above: a session that has already started
        // must not wait on a state-file write.
        _ = _sessionStateRecorder?.RecordSessionStartedAsync(
            paneId,
            result.Profile,
            startedWorkingDirectory,
            worktreePath: session.WorktreeBranch is not null ? startedWorkingDirectory : null,
            worktreeBranch: session.WorktreeBranch,
            startedPermissionMode);

        // Record that this project was worked on, whichever door the session came through, so the overview can
        // lead with what is actually used. Fire-and-forget like the worktree count: a small config write must not
        // hold up a session that has already started, and a failed one costs an ordering, not the work.
        if (result.ProjectId is { Length: > 0 } projectId
            && Projects.Projects.FirstOrDefault(project => project.Id == projectId) is { } opened)
        {
            _ = Projects.MarkOpenedAsync(opened, DateTimeOffset.Now);
        }

        return paneId;
    }

    // Isolation is identical for both session kinds (Raymond 2026-07-19): both take a working directory, so both
    // isolate it the same way (AC-85). When asked and the folder is a git repository, a worktree is created for this
    // session on its own branch — keyed on the session's pane, so the same session identity is used whichever kind it
    // is — and the session runs there instead of in the folder as given; the branch shows as a header chip. A
    // non-repository folder (or no worktree manager) runs as given, never a silent pretend-isolation.
    private async Task<string?> _ResolveIsolatedWorkingDirectoryAsync(SessionPanelViewModel session, NewSessionResult result)
    {
        if (!result.IsolateInWorktree || _worktreeManager is null || string.IsNullOrWhiteSpace(result.WorkingDirectory))
        {
            return result.WorkingDirectory;
        }

        try
        {
            // Reattach: the folder is already a worktree the cockpit created — re-own it for this session and run
            // there, rather than nesting a new worktree inside it. Only ever a worktree whose owning session is gone:
            // stealing a live one would put two sessions on one working tree, so a folder that matches a *live*
            // worktree is left owned by it and this session runs in it as given rather than re-owning it.
            var existing = await _MatchingWorktreeAsync(result.WorkingDirectory);
            if (existing is not null)
            {
                var live = _liveSessions?.LiveSessionIds ?? Sessions.Select(s => s.PaneId).ToHashSet(StringComparer.Ordinal);
                if (live.Contains(existing.SessionId))
                {
                    return result.WorkingDirectory;
                }

                if (await _worktreeManager.ReattachAsync(existing.Path, session.PaneId) is { } reattached)
                {
                    session.WorktreeBranch = reattached.Branch;
                    return reattached.Path;
                }
            }

            if (await _worktreeManager.DetectRepositoryAsync(result.WorkingDirectory) is null)
            {
                return result.WorkingDirectory;
            }

            var worktree = await _worktreeManager.CreateForSessionAsync(session.PaneId, result.Profile.Label, result.WorkingDirectory);
            session.WorktreeBranch = worktree.Branch;
            return worktree.Path;
        }
        catch (Exception exception)
        {
            // Isolation was explicitly asked for but the worktree could not be created (a git error, a folder that
            // vanished). Running in the operator's real checkout is the exact working-tree contamination isolation
            // exists to prevent, so never fall back to it silently: ask, and only run unisolated on an explicit yes.
            // A no throws OperationCanceledException, which the launch path turns into a cancelled start rather than
            // contaminating the working tree.
            var runInFolder = _dialogService is not null && await _dialogService.ShowConfirmationDialogAsync(
                "Could not isolate this session",
                $"A git worktree could not be created for this session ({exception.Message}). Run it directly in '{result.WorkingDirectory}' instead? Its edits and commits would then land in that working tree, not an isolated one.",
                "Run in folder");
            if (runInFolder)
            {
                return result.WorkingDirectory;
            }

            throw new OperationCanceledException("Session start cancelled: worktree isolation failed and running unisolated was declined.");
        }
    }

    // The registered worktree whose folder is exactly this working directory, or null — the reattach probe. Uses the
    // same OS-aware path comparison the worktree engine does, so a case-only difference matches on Windows/macOS and
    // is distinct on Linux.
    //
    // Deliberately not WorktreeLookup (AC-320), which answers null for a path the platform rejects: here a path that
    // cannot be resolved must throw, because the caller turns that into "could not isolate this session — run in the
    // folder anyway?" rather than starting an unisolated session in silence.
    private async Task<WorktreeRecord?> _MatchingWorktreeAsync(string workingDirectory)
    {
        if (_worktreeManager is null)
        {
            return null;
        }

        var full = Path.GetFullPath(workingDirectory);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return (await _worktreeManager.ListAsync())
            .FirstOrDefault(record => string.Equals(Path.GetFullPath(record.Path), full, comparison));
    }

    /// <summary>Context-menu Rename: begin the sidebar row's inline rename.</summary>
    [RelayCommand]
    private void RenameSession(SessionPanelViewModel session) => session.BeginRename();

    /// <summary>
    /// Context-menu Set status (AC-32): edit this session's status line by hand through the dialog, seeded with its
    /// current value. Writes the result back to the same <see cref="SessionPanelViewModel.Statusline"/> the MCP
    /// <c>set_status</c> tool sets, so manual and agent updates stay one source of truth; a cancel leaves it as it was.
    /// </summary>
    [RelayCommand]
    private async Task SetSessionStatusAsync(SessionPanelViewModel session)
    {
        if (_dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowSetStatusDialogAsync(session.Statusline);
        if (result is not null)
        {
            session.Statusline = result;
        }
    }

    /// <summary>
    /// Context-menu "Resume later…" (AC-231): schedules one prompt for this session at a moment of the operator's
    /// choosing, the route that does not start from a warning. Silently unavailable where nothing can be scheduled.
    /// </summary>
    [RelayCommand]
    private async Task ScheduleSessionResumeAsync(SessionPanelViewModel session)
    {
        if (_dialogService is null || ScheduledResumes is not { } scheduler)
        {
            return;
        }

        var chosen = await _dialogService.ShowScheduleResumeDialogAsync(DateTimeOffset.Now.AddHours(1), "continue");
        if (chosen is not { } picked)
        {
            return;
        }

        // No pending line set from here: the session reads it off the scheduler, so it also disappears again when
        // the resume fires, lapses or is cancelled (AC-368).
        await scheduler.ScheduleAsync(new ScheduledResume(session.PaneId, picked.Moment, picked.Prompt, Reason: "Scheduled by hand"));
    }


    /// <summary>Context-menu Clear status (AC-32): wipe this session's status line, the same as the MCP setting it to empty.</summary>
    [RelayCommand]
    private void ClearSessionStatus(SessionPanelViewModel session) => session.Statusline = string.Empty;

    /// <summary>
    /// Reorders <paramref name="session"/> to sit at <paramref name="targetVisibleIndex"/> among the sessions the
    /// sidebar is actually showing (the active workspace's <see cref="VisibleSessions"/>) — the primitive behind
    /// both the drag-reorder (AC-115) and the Move up/down menu items.
    /// </summary>
    /// <remarks>
    /// The reorder lands in <see cref="_sidebarOrder"/>, never in <see cref="Sessions"/>: the session grid binds to
    /// <see cref="Sessions"/> and keeps its own positional cell layout, so touching that collection would rebuild
    /// panes and drag the grid tiles along with the strip — the very coupling this separation removes. The order is
    /// global and can interleave other workspaces' sessions, so the move anchors to the target visible row's real
    /// position rather than a raw ±1 index — otherwise a step could swap with a session hidden on another workspace
    /// and do nothing (or the wrong thing) on screen. Order is kept only in this in-memory list: sessions
    /// themselves do not survive a restart (there is no persisted session list), so neither does their order — by
    /// design for AC-115.
    /// </remarks>
    public void MoveSessionToVisibleIndex(SessionPanelViewModel session, int targetVisibleIndex)
    {
        var visible = VisibleSessions.ToList();
        var currentVisibleIndex = visible.IndexOf(session);
        if (currentVisibleIndex < 0
            || targetVisibleIndex < 0
            || targetVisibleIndex >= visible.Count
            || targetVisibleIndex == currentVisibleIndex)
        {
            return;
        }

        var from = _sidebarOrder.IndexOf(session);
        var to = _sidebarOrder.IndexOf(visible[targetVisibleIndex]);
        if (from >= 0 && to >= 0)
        {
            _sidebarOrder.RemoveAt(from);
            _sidebarOrder.Insert(to, session);
            OnPropertyChanged(nameof(VisibleSessions));
        }
    }

    /// <summary>Context-menu Move up: shift the session one place earlier in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionUp(SessionPanelViewModel session)
    {
        var index = VisibleSessions.ToList().IndexOf(session);
        if (index > 0)
        {
            MoveSessionToVisibleIndex(session, index - 1);
        }
    }

    /// <summary>Context-menu Move down: shift the session one place later in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionDown(SessionPanelViewModel session)
    {
        var visible = VisibleSessions.ToList();
        var index = visible.IndexOf(session);
        if (index >= 0 && index < visible.Count - 1)
        {
            MoveSessionToVisibleIndex(session, index + 1);
        }
    }

    /// <summary>Context-menu Duplicate: start a new session with the same profile/model/mode as this one (≈ Fork).</summary>
    [RelayCommand]
    private async Task DuplicateSessionAsync(SessionPanelViewModel session)
    {
        if (session.LaunchResult is { } result)
        {
            // The copy's name is composed here, so it is only as deliberate as the one it was copied from: a copy of
            // "default - 3" stays open to a ticket link relabelling it, a copy of a name you typed does not (#AC-310).
            await _LaunchSessionFromResultAsync(result with
            {
                SessionName = $"{session.Title} (copy)",
                NameIsComposed = session.HasGeneratedName,
            });
        }
    }

    /// <summary>Opens the Manage-profiles dialog from the sidebar, independent of creating a session (L2).</summary>
    [RelayCommand]
    private async Task ManageProfilesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowManageProfilesDialogAsync();
    }

    /// <summary>Opens the MCP-servers dialog (#26) from the sidebar to edit the shared MCP-server registry.</summary>
    [RelayCommand]
    private async Task OpenMcpServersAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowMcpServersDialogAsync();
    }

    /// <summary>Opens the Verify-runners dialog (AC-86) from the sidebar to register the per-project command the visual verify loop may run.</summary>
    [RelayCommand]
    private async Task OpenVerifyRunnersAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowVerifyRunnersDialogAsync();
    }

    /// <summary>Opens the Options dialog (#13) from the sidebar, passing this view model as its DataContext.</summary>
    [RelayCommand]
    private Task OptionsAsync() => _ShowOptionsAsync();

    /// <summary>Opens the projects manager (AC-161) — its own window, not a corner of Options.</summary>
    [RelayCommand]
    private async Task ManageProjectsAsync()
    {
        if (_dialogService is not null)
        {
            await _dialogService.ShowProjectsDialogAsync(Projects);
        }
    }

    /// <summary>
    /// Brings the projects overview to the front, opening it when it is not there (AC-162) — the sidebar's way in,
    /// so reaching it is not a matter of knowing that a workspace type exists and finding it in the "+" menu.
    /// </summary>
    [RelayCommand]
    private Task OpenProjectsWorkspaceAsync() => Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

    private async Task _ShowOptionsAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _RefreshAudioDevicesAsync();
        // Fire-and-forget: probing the local LLM server (resolve + /v1/models) is a network round-trip that must
        // not hold the dialog open. The model dropdown + "auto will use…" summary are observable, so they fill in
        // a moment later without blocking; a timeout inside keeps a slow/hung server from lingering.
        _ = _RefreshVoiceLlmAsync();
        await Plugins.LoadAsync();
        await _dialogService.ShowOptionsDialogAsync(this);
    }

    /// <summary>
    /// Opens the plugin store dialog (#62) with the "Available updates" filter preselected (#65) — the
    /// action button on a plugin-update toast, so the operator lands straight on the updates list instead
    /// of the full Options→Plugins tab. Skips the audio-device refresh <see cref="OptionsAsync"/> does
    /// since it is irrelevant here.
    /// </summary>
    public async Task OpenPluginStoreUpdatesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await Plugins.LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(Plugins, PluginStoreFilter.UpdatesAvailable);
    }

    /// <summary>
    /// Opens the plugin store from the sidebar (AC-76) — on the Updates filter when updates are waiting (the sidebar
    /// badge is showing), so a click on the "N updates" indicator lands straight on them; otherwise the normal browse.
    /// </summary>
    [RelayCommand]
    private async Task OpenPluginStoreAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await Plugins.LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(Plugins, Plugins.HasUpdateBadge ? PluginStoreFilter.UpdatesAvailable : null);
    }

    /// <summary>Opens the About dialog (#46) from the sidebar: app name, version, description and links.</summary>
    [RelayCommand]
    private async Task AboutAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAboutDialogAsync();
    }

    /// <summary>
    /// Opens the delegated-tasks view (#67): the work other sessions handed to a profile. Those tasks run as
    /// sessions with no tab of their own, so this is where they stay visible — and stoppable.
    /// </summary>
    [RelayCommand]
    private async Task ShowDelegatedTasksAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowDelegatedTasksDialogAsync();
    }

    [RelayCommand]
    private async Task ShowWorktreesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowWorktreesDialogAsync(Worktrees);

        // The dialog may have removed or reattached worktrees; bring the status-bar counter back in step.
        await Worktrees.RefreshCountAsync();
    }

    // Reattach (AC-85): start a new session in an existing worktree by opening the New-session dialog with its folder
    // pre-filled and isolation on, so starting re-owns that worktree (the resolve reattaches a known worktree path).
    private async Task _ReattachSessionAsync(WorktreeRecord record)
    {
        if (_dialogService is null)
        {
            return;
        }

        var result = await _dialogService.ShowNewSessionDialogAsync(
            new NewSessionPrefill(WorkingDirectory: record.Path), isolateInWorktree: true);
        if (result is not null)
        {
            await _LaunchSessionFromResultAsync(result);
        }
    }

    /// <summary>Opens the command palette (#: command palette): a searchable list of every app action and plugin command with its shortcut.</summary>
    [RelayCommand]
    private async Task ShowCommandPaletteAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowCommandPaletteDialogAsync(BuildPaletteCommands());
    }

    // Every command the palette can run: the built-in app actions (except the palette itself) and every
    // plugin-contributed command, each with its shortcut shown. Plugins appear here just by registering a
    // shortcut — one with no gesture is a palette-only command.
    internal IReadOnlyList<PaletteCommand> BuildPaletteCommands()
    {
        var commands = new List<PaletteCommand>();
        foreach (var descriptor in ShortcutCatalog.All)
        {
            if (descriptor.Action == ShortcutAction.CommandPalette)
            {
                continue;
            }

            commands.Add(new PaletteCommand(
                descriptor.Label,
                _PrettifyGesture(_shortcutSettings.GestureFor(descriptor.Action)),
                () => _InvokeAppAction(descriptor.Action)));
        }

        foreach (var shortcut in PluginShortcuts)
        {
            commands.Add(new PaletteCommand(
                shortcut.Title,
                _PrettifyGesture(_shortcutSettings.GestureForPlugin(shortcut.Id, shortcut.DefaultGesture)),
                shortcut.OnInvoke));
        }

        // One entry per widget rather than a single "Add widget" that reopens the gallery: the palette is a
        // search box, so naming the widget in it is the whole point — you type "clock" and it is placed, which
        // is one step where the gallery is two. Only while a dashboard is showing; a Sessions workspace has
        // nowhere to put one, and a command that cannot run is one to leave out rather than grey out.
        if (Workspaces.IsDashboardActive)
        {
            foreach (var widget in Workspaces.AvailableWidgets)
            {
                commands.Add(new PaletteCommand(
                    $"Add widget: {widget.Title}",
                    string.Empty,
                    () => Workspaces.PlaceWidgetCommand.Execute(widget)));
            }
        }

        // Debug-only (#73): a way to raise a sample consent prompt on the selected session so the AC-47 banner can
        // be tried before a real consumer wires one up. Hidden unless the debug controls are on.
        if (ShowDebugControls && _consentBroker is not null)
        {
            commands.Add(new PaletteCommand("Debug: test consent prompt (dangerous)", string.Empty, () => _TriggerTestConsent(dangerous: true)));
            commands.Add(new PaletteCommand("Debug: test consent prompt (low-risk)", string.Empty, () => _TriggerTestConsent(dangerous: false)));
        }

        return commands;
    }

    // "Ctrl+Shift+P" -> "Ctrl + Shift + P" for the palette's shortcut column; blank stays blank.
    private static string _PrettifyGesture(string gesture) =>
        string.IsNullOrWhiteSpace(gesture) ? string.Empty : gesture.Replace("+", " + ");

    /// <summary>
    /// Persists every options section in one go — the Options dialog's single footer Save (#13)
    /// replaces the six per-section Save buttons the flyout used to have.
    /// </summary>
    [RelayCommand]
    private async Task SaveAllSettingsAsync()
    {
        await SaveNotificationSettingsCommand.ExecuteAsync(null);
        await SaveTranscriptDisplaySettingsCommand.ExecuteAsync(null);
        await SaveUsagePillSettingsCommand.ExecuteAsync(null);
        await SaveSessionBehaviorSettingsCommand.ExecuteAsync(null);
        // Before the voice save, which is what raises VoiceSettingsSaved — the hotkey coordinator re-arms on that
        // and reads both sections, so a screenshot key saved after it would not be armed until the next launch.
        await SaveScreenshotSettingsCommand.ExecuteAsync(null);
        await SaveLayoutSettingsCommand.ExecuteAsync(null);
        await SaveVoiceSettingsCommand.ExecuteAsync(null);
        await SaveTerminalSettingsCommand.ExecuteAsync(null);
        await SaveShortcutSettingsCommand.ExecuteAsync(null);
        await SaveDebugSettingsCommand.ExecuteAsync(null);
        await SaveRenderingSettingsCommand.ExecuteAsync(null);
        await SaveWorktreeSettingsCommand.ExecuteAsync(null);
        await SaveCloneSettingsCommand.ExecuteAsync(null);
        AllSettingsStatus = "Saved";
    }

    private void AddSession(SessionPanelViewModel session, string? name, string profileLabel, bool nameIsChosen = false)
    {
        _sessionCounter++;
        // A session always lives on a Sessions workspace (Raymond): the one showing, else the first there is,
        // else a new one. Started while only a dashboard exists, it would otherwise run on a desk that cannot
        // show it — invisible rather than absent, which is the worse of the two. Deliberately not used on the
        // restore path (see _AttachRestoredSession): bringing a saved pane back must not activate the desk it
        // was on, which EnsureSessionWorkspace would do for a Dashboard/Projects workspace currently on screen.
        session.WorkspaceId = Workspaces.EnsureSessionWorkspace();
        // A friendly name from the dialog wins; otherwise fall back to "<profile> - <N>" so the sidebar
        // shows which profile — and therefore which provider — each session runs under. Whether that name is one
        // somebody meant is not worked out here: NewSessionResult.NameIsChosen says so, and this applies it (#AC-324).
        session.Title = string.IsNullOrWhiteSpace(name) ? $"{profileLabel} - {_sessionCounter}" : name.Trim();
        session.HasGeneratedName = !nameIsChosen;
        _AttachSession(session);
        SelectedSession = session;
    }

    /// <summary>
    /// The wiring every session panel needs once it is going to be shown, regardless of how it got here: preference
    /// seeding, the close/property-changed subscriptions, and joining <see cref="Sessions"/>. Shared by a freshly
    /// started session (<see cref="AddSession"/>) and one brought back after a restart
    /// (<see cref="_AttachRestoredSession"/>) — the two differ only in how <see cref="SessionPanelViewModel.WorkspaceId"/>,
    /// the title and selection are decided, which is why those stay in the callers.
    /// </summary>
    private void _AttachSession(SessionPanelViewModel session)
    {
        _SeedSessionPreferences(session);

        session.CloseRequested += OnSessionCloseRequested;
        // AC-410: harmless for a freshly started session — RestoreOffer stays null, so nothing on the banner can
        // ever raise this — and is what lets a restored one's "Resume"/"Start fresh" reach the cockpit.
        session.RestoreDecided += OnSessionRestoreDecided;

        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;

        Sessions.Add(session);
    }

    /// <summary>
    /// Attaches a session pane rebuilt from a saved <see cref="WorkspacePane"/> (AC-410): shown, but nothing
    /// started — <see cref="RestoreSessionPanesAsync"/> mints the panel and adopts its saved id before calling
    /// this. <paramref name="workspaceId"/> is set directly rather than through <see cref="Workspaces"/>'
    /// <c>EnsureSessionWorkspace</c>, which would switch the operator to that desk; restoring a pane on a
    /// workspace must not activate it. Deliberately does not set <see cref="SelectedSession"/> — the restore loop
    /// picks at most one session for that, once, across every pane it restores.
    /// </summary>
    private void _AttachRestoredSession(SessionPanelViewModel session, string workspaceId, WorkspacePane pane)
    {
        session.WorkspaceId = workspaceId;
        session.Title = string.IsNullOrWhiteSpace(pane.Title) ? "Session" : pane.Title;
        session.HasGeneratedName = !pane.NameIsChosen;
        session.ProjectId = pane.ProjectId;
        session.HasPersistedPane = true;
        _AttachSession(session);
    }

    /// <summary>
    /// The <see cref="WorkspacePane"/> record for a just-started AI session (AC-410) — the operator's
    /// <em>intention</em>: which profile and kind it runs under, and the folder it was asked to run in, before
    /// isolation may have moved it into a worktree. Written by <see cref="_PersistNewSessionPane"/> right after
    /// <see cref="AddSession"/>, before the session actually starts.
    /// </summary>
    private static WorkspacePane _BuildSessionPane(SessionPanelViewModel session, NewSessionResult result) =>
        new(session.PaneId, PaneKind.AiSession)
        {
            ProfileId = result.Profile.Label,
            SessionKind = result.Kind == SessionKind.Sdk ? PaneSessionKind.Sdk : PaneSessionKind.Tty,
            WorkingDirectory = result.WorkingDirectory,
            Title = session.Title,
            NameIsChosen = result.NameIsChosen,
            ProjectId = result.ProjectId,
        };

    /// <summary>
    /// Persists <paramref name="session"/>'s pane record right after <see cref="AddSession"/> — deliberately before
    /// <see cref="_StartSessionAsync"/> runs, not after: a crash in between leaves at most one config write, so the
    /// worst case is a pane that never comes back, not one that comes back describing a session that never
    /// actually started this way (AC-410). Fire-and-forget, the same as every other workspace-settings write.
    /// </summary>
    private void _PersistNewSessionPane(SessionPanelViewModel session, NewSessionResult result)
    {
        session.HasPersistedPane = true;
        _ = Workspaces.AddPaneAsync(session.WorkspaceId, _BuildSessionPane(session, result));
    }

    // AC-410: the restore plan composed for each pane brought back this run, kept by pane id — read by the banner
    // (SessionPanelViewModel.RestoreOffer, set from here) and again by _StartRestoredSessionAsync once the
    // operator picks a start, so the plan is composed exactly once per pane per run.
    private readonly Dictionary<string, SessionRestorePlan> _restorePlans = new(StringComparer.Ordinal);

    // AC-410: the working directory a restored pane actually starts in, resolved once here from the worktree
    // registry rather than left to the start path — the restore path runs with IsolateInWorktree: false (see
    // _BuildRestoreLaunchResult), so _ResolveIsolatedWorkingDirectoryAsync never gets a chance to look this up
    // itself. Null is a legitimate value (no working directory was ever known), so this is keyed by presence,
    // not by a non-null value.
    private readonly Dictionary<string, string?> _restoreWorkingDirectories = new(StringComparer.Ordinal);

    /// <summary>
    /// Brings back every AI-session pane saved on a Sessions workspace (AC-410), once <see cref="Workspaces"/> has
    /// loaded <c>cockpit.json</c>: for each, composes a restore plan, resolves its worktree (if any) from the
    /// registry, mints the matching panel through the factory its saved <see cref="PaneSessionKind"/> names, adopts
    /// the pane's saved id, and attaches it — shown, but nothing started. Chained after
    /// <c>Workspaces.InitializeAsync</c> in <c>App.axaml.cs</c>'s startup fire-and-forget.
    /// <para>
    /// Waits on <see cref="IWorktreeReconcileGate"/> first: <c>Program.cs</c> starts the startup worktree reconcile
    /// fire-and-forget so it never delays the window, and without this wait an operator who accepts a restore offer
    /// within about a second of launch could race the reconcile into removing the very worktree the offer is about
    /// to reattach.
    /// </para>
    /// <para>
    /// Never throws — the same contract <c>InitializeAsync</c> keeps, so a continuation chained after both always
    /// runs. A pane this run cannot make sense of (an id already restored, in the unlikely event of a
    /// hand-duplicated <c>cockpit.json</c>) is skipped rather than aborting every other pane's restore; the skip is
    /// logged so it leaves a trail instead of a pane that silently never comes back.
    /// </para>
    /// </summary>
    public async Task RestoreSessionPanesAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionFactory is null || _ttySessionFactory is null || _sessionStateStore is null)
        {
            return;
        }

        if (_worktreeReconcileGate is not null)
        {
            await _worktreeReconcileGate.WaitAsync(cancellationToken);
        }

        IReadOnlyList<SessionStateRecord> states;
        try
        {
            states = await _sessionStateStore.LoadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // ISessionStateStore.LoadAsync's own contract says it never throws, but a restore that somehow still
            // fails here must not take the rest of startup down with it — nothing restores this run, same as an
            // empty state file. Logged so "no panes came back" leaves a trail rather than silence.
            _logger?.LogWarning(exception, "Could not load session state; no AI-session panes will be restored this run.");
            return;
        }

        var activeWorkspaceId = Workspaces.Active?.Id;
        var selected = false;

        foreach (var (workspace, pane) in SessionRestoreRoster.Panes(Workspaces.Settings))
        {
            if (Sessions.Any(existing => existing.PaneId == pane.Id))
            {
                // A hand-duplicated pane id in cockpit.json must not produce two panels sharing one identity —
                // refused here, before a factory even mints one, rather than crashing the whole restore.
                continue;
            }

            try
            {
                var state = states.FirstOrDefault(record => record.PaneId == pane.Id);

                SessionRestorePlan? plan = null;
                if (_sessionRestorePlanner is not null)
                {
                    plan = await _sessionRestorePlanner.ComposeAsync(pane, state, cancellationToken);
                    _restorePlans[pane.Id] = plan;
                }

                SessionPanelViewModel session = pane.SessionKind == PaneSessionKind.Tty ? _ttySessionFactory() : _sessionFactory();
                session.AdoptPaneId(pane.Id);
                _AttachRestoredSession(session, workspace.Id, pane);
                session.RestoreOffer = plan;

                // AC-410: pane-id continuity (AdoptPaneId, above) means a restored pane's own id is the worktree's
                // owner id, so this is the same registry lookup a live session's own worktree would be found
                // under — not a probe of "does one exist", but "which one is already this pane's".
                var workingDirectory = state?.WorkingDirectory ?? pane.WorkingDirectory;
                if (_worktreeManager is not null
                    && (await _worktreeManager.ListAsync(cancellationToken)).FirstOrDefault(
                        record => string.Equals(record.SessionId, pane.Id, StringComparison.Ordinal)) is { } worktree)
                {
                    session.WorktreeBranch = worktree.Branch;
                    workingDirectory = worktree.Path;
                }

                _restoreWorkingDirectories[pane.Id] = workingDirectory;

                if (!selected && workspace.Id == activeWorkspaceId)
                {
                    SelectedSession = session;
                    selected = true;
                }
            }
            catch (Exception exception)
            {
                // One pane's restore failing (a planner it cannot compose against, a factory that throws) must
                // not cost every other pane its restore — the conservative outcome here is a pane that does not
                // come back, not a half-attached one or a startup that never finishes. Logged so this reads as a
                // warning in the log instead of a pane that silently never returns.
                _logger?.LogWarning(exception, "Could not restore the AI-session pane {PaneId}; it will not come back this run.", pane.Id);
            }
        }
    }

    /// <summary>
    /// What a restored pane starts with once the operator accepts the offer (AC-410) — mirrors
    /// <see cref="ProjectQuickStart.ComposeAsync"/>'s use of app-default mode/model/effort (the typed Claude
    /// vocabulary is migration-only; there is no dialog here to have overridden them). Null when
    /// <see cref="SessionRestorePlan.Profile"/> is null (<see cref="SessionRestoreAvailability.ProfileGone"/> or an
    /// <see cref="SessionRestoreAvailability.Unknown"/> plan with no profile at all) — there is nothing to start a
    /// session under, so the caller leaves the offer standing rather than starting the wrong thing.
    /// </summary>
    private NewSessionResult? _BuildRestoreLaunchResult(SessionRestorePlan plan, SessionResume resume)
    {
        if (plan.Profile is not { } profile)
        {
            return null;
        }

        var pane = plan.Pane;
        var isSdk = pane.SessionKind != PaneSessionKind.Tty;

        return new NewSessionResult(
            isSdk ? SessionKind.Sdk : SessionKind.Tty,
            profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            pane.Title,
            WorkingDirectory: _restoreWorkingDirectories.GetValueOrDefault(pane.Id, pane.WorkingDirectory),
            Resume: resume,
            PluginTtyOptions: isSdk ? null : profile.Defaults?.OptionDefaults,
            SdkLaunchOptions: isSdk ? profile.Defaults?.OptionDefaults : null,
            // Never true here (AC-410's documented pitfall): the working directory above and session.WorktreeBranch
            // were already resolved from the worktree registry at materialization time, in RestoreSessionPanesAsync.
            // Isolating again would either re-detect the same worktree through a redundant lookup or, worse, mint a
            // second one for a pane that already owns one.
            IsolateInWorktree: false,
            ReadingLevel: isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId: pane.ProjectId)
        {
            NameIsComposed = !pane.NameIsChosen,
        };
    }

    /// <summary>
    /// Starts a restored pane once the operator has decided (AC-410) — <see cref="SessionPanelViewModel.RestoreDecided"/>'s
    /// handler. "Resume" resolves to <see cref="SessionResume.BySessionId"/> when the plan's saved state actually
    /// names a conversation id, and to <see cref="SessionResume.New"/> otherwise (and always for "Start fresh") —
    /// the same fall-back <c>_BuildRestoreLaunchResult</c> would otherwise silently need twice. Clears
    /// <see cref="SessionPanelViewModel.RestoreOffer"/> only once the start actually returns a pane id: a cancelled
    /// isolation prompt (see <c>_StartSessionAsync</c>) closes the session outright, and a plan with no profile to
    /// start under leaves the offer standing rather than pretending a start happened.
    /// </summary>
    private async Task _StartRestoredSessionAsync(SessionPanelViewModel session, SessionRestoreChoice choice)
    {
        if (!_restorePlans.TryGetValue(session.PaneId, out var plan))
        {
            return;
        }

        var resume = choice == SessionRestoreChoice.Resume && plan.State?.ConversationId is { Length: > 0 } conversationId
            ? SessionResume.BySessionId(conversationId)
            : SessionResume.New;

        if (_BuildRestoreLaunchResult(plan, resume) is not { } result)
        {
            return;
        }

        if (await _StartSessionAsync(session, result) is not null)
        {
            session.RestoreOffer = null;
        }
    }

    /// <summary>
    /// AC-290's other half of a scheduled resume: when its pane is gone, or merely restored and not yet started,
    /// but the earlier conversation is one AC-410 already knows how to bring back, reopen it exactly the way the
    /// restore-offer banner's own "Resume conversation" would — <see cref="_StartRestoredSessionAsync"/> — and send
    /// the prompt the moment it lands. Wired as <see cref="ScheduledResumeCoordinator.ReopenAndSend"/>.
    /// <para>
    /// Deliberately does not compose a fresh restore plan for a pane <see cref="RestoreSessionPanesAsync"/> never
    /// saw this run: a pane closed on purpose already had its <c>WorkspacePane</c> record removed (<see
    /// cref="CloseSessionAsync"/>), so there is nothing left to reopen it with, and reopening it anyway would
    /// second-guess the operator's own close. The reachable case is a crash the operator was never asked about —
    /// restart materializes the restore offer, and a resume due after that restart can pick it straight back up.
    /// </para>
    /// <para>
    /// SDK sessions only, for now: a TTY's <c>PromptSink</c> is wired asynchronously by the view once its pty has
    /// actually come up (<c>TtyView.StartPty</c>), well after <see cref="_StartSessionAsync"/> already returned and
    /// <see cref="_StartRestoredSessionAsync"/> already cleared the offer — so <c>CanTakeAPrompt</c> reads false
    /// immediately afterwards every time, and attempting a TTY reopen here would start the pty, destroy its restore
    /// offer, and still have to report the resume as undelivered. Left for a follow-up that can wait for the pty
    /// rather than assume it is already there; an SDK session's runtime, by contrast, is up by the time
    /// <c>StartConfiguredAsync</c> returns.
    /// </para>
    /// <para>
    /// Gates on the saved conversation id directly rather than <see cref="SessionPanelViewModel.CanResumeConversation"/>
    /// (which only reflects <see cref="SessionRestoreAvailability.Known"/>): <see cref="_StartRestoredSessionAsync"/>
    /// decides <see cref="SessionResume.BySessionId"/> vs. <see cref="SessionResume.New"/> from the id string itself,
    /// and a provider that reports <c>Known</c> without actually supplying one — a contract violation at the plugin
    /// seam, but not one anything currently stops — must not fall through to a silent fresh start under this
    /// method's own toast claiming otherwise.
    /// </para>
    /// </summary>
    private async Task<bool> _ReopenAndSendResumeAsync(string paneId, string prompt)
    {
        if (Sessions.FirstOrDefault(session => session.PaneId == paneId) is not SessionViewModel session
            || session.RestoreOffer?.State?.ConversationId is not { Length: > 0 })
        {
            return false;
        }

        await _StartRestoredSessionAsync(session, SessionRestoreChoice.Resume);

        return session.RestoreOffer is null && session.CanTakeAPrompt && await session.SendPromptAsync(prompt);
    }

    /// <summary>A restore offer was resolved into a start (AC-410) — run the matching launch through the normal start path.</summary>
    private void OnSessionRestoreDecided(object? sender, SessionRestoreChoice choice)
    {
        if (sender is SessionPanelViewModel session)
        {
            _ = _StartRestoredSessionAsync(session, choice);
        }
    }

    /// <summary>
    /// Seeds a freshly built session with the live global preferences it must start on — transcript display (T7),
    /// usage-pill fields (AC-105), auto-close-on-exit (T10), diagnostic controls (#73), combine-queued-messages
    /// (AC-145, SDK only), and, for a TTY, terminal appearance (#40) and stacked layout (#54). Each is kept current
    /// afterwards by its own OnXChanged hook. Shared by the grid path (<see cref="AddSession"/>) and the embedded
    /// path (<see cref="Embed"/>): the settings a session starts on are the same wherever it is shown.
    /// </summary>
    private void _SeedSessionPreferences(SessionPanelViewModel session)
    {
        session.ShowTimestamps = ShowTimestamps;

        // AC-231: the one scheduler, so a session can offer to pick itself up when its allowance returns. Null in
        // the graphs that have none, and the offer simply never appears there.
        session.Resumes = ScheduledResumes;

        // AC-233: what the operator set for themselves, on top of what each provider declared. Null until loaded,
        // and every signal then follows its provider.
        session.UsageThresholds = UsageThresholds;

        // AC-231: how a session asks for a different moment than the one its allowance dictates. The cockpit owns
        // the dialogs, so it hands the asking down rather than the session reaching for one.
        session.AskForResumeMoment = _dialogService is { } dialogs
            ? (suggested, prompt) => dialogs.ShowScheduleResumeDialogAsync(suggested, prompt)
            : null;
        session.UsagePillVisibleFields = ComposeUsagePillFields();
        session.AutoCloseOnExit = AutoCloseOnExit;
        session.ShowDebugControls = ShowDebugControls;
        _WireScreenshots(session);

        // SDK/chat sessions only — a TTY session has no local send queue (AC-145).
        if (session is SessionViewModel sdkSession)
        {
            sdkSession.CombineQueuedMessages = CombineQueuedMessages;
        }

        // TTY-only appearance; no effect on an SDK session.
        if (session is TtyViewModel tty)
        {
            tty.TerminalFontFamily = TerminalFontFamily;
            tty.TerminalFontSize = TerminalFontSize;
            tty.IsVerticalLayout = StackSessionsVertically;
        }
    }

    /// <summary>
    /// Sets a session's agent/workflow statusline by its <see cref="SessionPanelViewModel.PaneId"/> (#AC-13) — the
    /// line shown under its title in the header and sidebar. Returns whether a live session matched; false is a
    /// no-op (the session may have closed), never an error. Must be called on the UI thread — the host API that a
    /// plugin/agent reaches this through marshals to it.
    /// </summary>
    public bool SetSessionStatusline(string paneId, string statusline)
    {
        if (FindSession(paneId) is not { } target)
        {
            return false;
        }

        target.Statusline = statusline ?? string.Empty;
        return true;
    }

    /// <summary>
    /// A session by its pane id, including embedded ones the grid deliberately does not list — so an embedded run's
    /// own <c>set_status</c>, a plugin acting on its embedded pane, and a consent routed to it all reach it (AC-152),
    /// not only grid sessions. Read the collections on the UI thread, like its callers do.
    /// </summary>
    public SessionPanelViewModel? FindSession(string paneId) =>
        _AllSessions().FirstOrDefault(session => session.PaneId == paneId);

    /// <summary>
    /// Every session the host holds, grid and embedded together (AC-391): an embedded agent (an Autopilot step, a
    /// plugin-run) is a full session with its own MCP token even though the grid deliberately never lists it, so a
    /// caller enumerating "every agent" — the workspace-presence roster, say — must not miss it the way iterating
    /// <see cref="Sessions"/> alone would. Same collections as <see cref="FindSession"/>; read them on the UI
    /// thread, like its callers do.
    /// </summary>
    public IEnumerable<SessionPanelViewModel> AllSessions() => _AllSessions();

    // Every session the host holds — the grid's, plus the embedded ones the grid deliberately does not list. The seam
    // both the pane-id lookup and the consent open/close routing search, so an embedded pane is never half-reached.
    private IEnumerable<SessionPanelViewModel> _AllSessions() =>
        Sessions.Concat(_embeddedSessions.Values.SelectMany(owned => owned));

    /// <summary>
    /// Renames a session — the title in its header and sidebar — by its <see cref="SessionPanelViewModel.PaneId"/>
    /// (#AC-13). A blank name is ignored. Returns whether a live session matched. Must be called on the UI thread.
    /// </summary>
    public bool SetSessionName(string paneId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Sessions.FirstOrDefault(session => session.PaneId == paneId) is not { } target)
        {
            return false;
        }

        target.Title = name.Trim();
        target.HasGeneratedName = false;
        return true;
    }

    /// <summary>
    /// Names a session the way <see cref="SetSessionName"/> does, but stands down when its name is one somebody
    /// chose (#AC-310) — how linking a ticket to a running session labels it without erasing the name the operator
    /// typed. Returns whether the session was renamed, so false covers both "no such session" and "it already has
    /// a name of its own". A suggested name counts as generated in its turn, so linking a second ticket to the same
    /// session relabels it rather than leaving it showing the first. Must be called on the UI thread.
    /// </summary>
    // FindSession, not Sessions: an embedded pane already reaches its statusline and its consent through the same
    // resolver, and an agent proposing a name for the session it is running in must not miss for being embedded
    // (AC-152, #AC-312).
    public bool SuggestSessionName(string paneId, string name) =>
        FindSession(paneId)?.SuggestName(name) ?? false;

    /// <summary>
    /// Edge-triggered attention routing: fires the presence-aware notifier once, on the transition
    /// into <see cref="SessionStatus.NeedsAttention"/> — not on every status touch while it stays
    /// there. The notifier itself decides present-toast vs away-webhook.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SessionPanelViewModel session)
        {
            return;
        }

        // The last background shell ending is the moment a session that was withheld below actually becomes
        // finished (AC-276). Its status does not change then — it is already Done — so without this the
        // notification would not merely be delayed but lost for good, on every session that ran one.
        if (e.PropertyName == nameof(SessionPanelViewModel.HasOutstandingBackgroundShells))
        {
            if (session.SessionStatus == SessionStatus.Done && !session.HasOutstandingBackgroundShells)
            {
                NotifySessionFinished(session);
            }

            return;
        }

        if (e.PropertyName != nameof(SessionPanelViewModel.SessionStatus))
        {
            return;
        }

        var previous = _lastStatus.GetValueOrDefault(session, SessionStatus.Idle);
        _lastStatus[session] = session.SessionStatus;

        if (session.SessionStatus == SessionStatus.NeedsAttention && previous != SessionStatus.NeedsAttention)
        {
            NotifyAttention(session);
        }

        // A turn just finished. Worth saying out loud only when you are not looking at that session — the
        // notifier makes that call, since it is the one that knows whether you are even at the PC.
        // A session with a backgrounded shell still running is not finished, whatever the status says (AC-276):
        // the status deliberately reaches Done there, because a dev server or a tail -f would otherwise pin it on
        // "working" forever — but announcing it as finished while it is still doing something is the very thing
        // this notification should not do. Sub-agents are not checked here because they never let this flank read
        // Busy → Done in the first place: on the SDK route the task list arrives before the turn's result, and on
        // the TTY route TtyActivityStatusTracker's settle delay holds the finish until the count that follows it
        // has had time to arrive. Both are load-bearing for that claim — see their own tests.
        // WorkingBackground counts as a working state here, not just Busy (AC-276). A session with sub-agents now
        // settles Busy → WorkingBackground → Done, and matching only on Busy would silently drop the notification
        // for exactly the sessions this ticket is about — the flicker would be gone and so would the announcement.
        if (session.SessionStatus == SessionStatus.Done
            && previous is SessionStatus.Busy or SessionStatus.WorkingBackground
            && !session.HasOutstandingBackgroundShells)
        {
            NotifySessionFinished(session);
        }

        // Anything other than idle means there is work in the cockpit again, so the next fall to complete
        // silence is news once more.
        if (session.SessionStatus != SessionStatus.Idle)
        {
            _allSessionsIdleNotified = false;
        }
    }

    /// <summary>
    /// Lets finished sessions fall back to idle once they have been quiet for the configured time, and announces
    /// that — per session, and once more when the last of them goes quiet so the cockpit as a whole is idle.
    /// Driven by a periodic sweep rather than a timer per session: one tick decides for all of them.
    /// </summary>
    /// <param name="now">The current time, injected so the sweep is testable without waiting for it.</param>
    internal void SweepIdleSessions(DateTimeOffset now)
    {
        var threshold = SessionIdleMinutes > 0 ? TimeSpan.FromMinutes(SessionIdleMinutes) : TimeSpan.Zero;

        foreach (var session in Sessions)
        {
            if (!SessionIdleDecision.BecomesIdle(session.SessionStatus == SessionStatus.Done, session.LastActivityUtc, now, threshold))
            {
                continue;
            }

            session.SessionStatus = SessionStatus.Idle;
            NotifySessionIdle(session, threshold);
        }

        if (!_allSessionsIdleNotified && Sessions.Count > 0 && Sessions.All(session => session.SessionStatus == SessionStatus.Idle))
        {
            _allSessionsIdleNotified = true;
            _ = _attentionNotifier?.NotifyAllSessionsIdleAsync();
        }
    }

    /// <summary>
    /// AC-439: recomputes which panes currently collide across a workspace boundary and stamps
    /// <see cref="SessionPanelViewModel.HasClaimCollision"/> on every one of them — an operator-only chip, never
    /// anything an agent's tool result carries. Driven by a timer in the view, on the same footing as the idle sweep
    /// and the resource sampler: the view model stays free of timers, and a test can call this whenever it likes. A
    /// no-op when no monitor was supplied (the design-time/unit-test graph), which reads as "no collisions" rather
    /// than an error.
    /// <para>
    /// <see cref="IClaimCollisionMonitor.PanesInCollision"/> canonicalizes every claimed resource, which for a
    /// path-shaped one means real filesystem calls (<see cref="System.IO.File.Exists(string)"/>,
    /// <see cref="System.IO.File.ResolveLinkTarget"/>) on strings an agent chose — a stalled network mount behind
    /// one claim must not stall this UI-thread timer for every pane. The computation therefore runs on the thread
    /// pool; only the property stamping below runs back on the UI thread once it completes.
    /// </para>
    /// </summary>
    internal async Task RefreshClaimCollisionsAsync()
    {
        if (_claimCollisionMonitor is null)
        {
            return;
        }

        var monitor = _claimCollisionMonitor;
        var colliding = await Task.Run(monitor.PanesInCollision).ConfigureAwait(true);
        foreach (var session in AllSessions())
        {
            session.HasClaimCollision = colliding.Contains(session.PaneId);
        }
    }

    /// <summary>A session asked to close itself (T10: an "exit" turn finished) — run the normal close flow.</summary>
    private void OnSessionCloseRequested(object? sender, EventArgs e)
    {
        if (sender is SessionPanelViewModel session)
        {
            _ = CloseSessionAsync(session);
        }
    }

    private void NotifyAttention(SessionPanelViewModel session)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var notification = new AttentionNotification(session.Title, session.SessionStatusLabel);
        // Fire-and-forget: notification delivery must not block the UI thread that raised the status
        // change. The notifier swallows and logs its own transport failures.
        _ = _attentionNotifier.NotifyAttentionAsync(notification);
    }

    private void NotifySessionFinished(SessionPanelViewModel session)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var notification = new AttentionNotification(session.Title, "Done");
        _ = _attentionNotifier.NotifySessionFinishedAsync(notification, ReferenceEquals(session, SelectedSession), IsWindowActive);
    }

    private void NotifySessionIdle(SessionPanelViewModel session, TimeSpan threshold)
    {
        if (_attentionNotifier is null)
        {
            return;
        }

        var minutes = (int)threshold.TotalMinutes;
        var notification = new AttentionNotification(session.Title, $"Idle for {minutes} minute(s)");
        _ = _attentionNotifier.NotifySessionIdleAsync(notification);
    }

    [RelayCommand]
    private void SelectSession(SessionPanelViewModel session)
    {
        SelectedSession = session;
    }

    /// <summary>
    /// Moves the selection to the previous session in <see cref="Sessions"/>, wrapping from the first
    /// to the last. No-op when there are no sessions; selects the only session when there is exactly
    /// one. Bound to the configurable <see cref="ShortcutAction.PreviousSession"/> shortcut (Ctrl+Shift+Up by default).
    /// </summary>
    [RelayCommand]
    public void SelectPreviousSession() => _StepSelection(-1);

    /// <summary>
    /// Moves the selection to the next session in <see cref="Sessions"/>, wrapping from the last to
    /// the first. No-op when there are no sessions. Bound to the configurable
    /// <see cref="ShortcutAction.NextSession"/> shortcut (Ctrl+Shift+Down by default).
    /// </summary>
    [RelayCommand]
    public void SelectNextSession() => _StepSelection(1);

    private void _StepSelection(int direction)
    {
        var count = Sessions.Count;
        if (count == 0)
        {
            return;
        }

        // No current selection → land on the first (next) or last (previous) session.
        var currentIndex = SelectedSession is null ? -1 : Sessions.IndexOf(SelectedSession);
        var startIndex = currentIndex < 0 ? (direction > 0 ? -1 : 0) : currentIndex;

        var nextIndex = ((startIndex + direction) % count + count) % count;
        SelectedSession = Sessions[nextIndex];
    }

    [RelayCommand]
    private async Task CloseSessionAsync(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        session.PropertyChanged -= OnSessionPropertyChanged;
        session.CloseRequested -= OnSessionCloseRequested;
        session.RestoreDecided -= OnSessionRestoreDecided;
        _lastStatus.Remove(session);

        Sessions.RemoveAt(index);

        // AC-410: a pane persisted at AddSession time (an AI session, started or merely restored) must stop
        // offering to come back once it is deliberately closed. A plain terminal never set HasPersistedPane, so
        // this is a no-op for it rather than a workspace write nothing asked for.
        if (session.HasPersistedPane)
        {
            _ = Workspaces.RemovePaneAsync(session.WorkspaceId, session.PaneId);
        }

        // AC-410: the plan and the resolved working directory are only ever read again by _StartRestoredSessionAsync
        // for this pane's own RestoreDecided — a closed pane raises neither again, so holding these past close
        // would only grow the two dictionaries for the life of the app.
        _restorePlans.Remove(session.PaneId);
        _restoreWorkingDirectories.Remove(session.PaneId);

        // Best-effort, for the same reason the worktree release below is: the panel is already out of the collection,
        // so a dispose that throws must not take the host-side teardown with it. The terminal couplings, the roster
        // entry, the unread inbox and the resource claims all live outside the session object, and each one skipped is
        // held for the life of the app — for a claim, that leaves neighbours working around a worktree nobody is on.
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The panel is already gone from the UI; what still matters is the teardown below.
        }

        // AC-34: this session may have been driving a terminal pane; releasing its couplings on close makes that pane's
        // "agent connected" bar disappear (SessionEnded raises CouplingChanged). It is the driver-side teardown the
        // pane's own PaneClosed cannot do — the mirror of the worktree release below, and it runs for every session.
        _terminals?.SessionEnded(session.PaneId);

        // AC-391: a closed pane must stop being remembered as a workspace-presence roster entry, or the roster only
        // ever grows for the life of the app and a reused pane id (unlikely, but not impossible) would inherit a
        // stale enrollment. Same "runs for every session" scope as the terminal-coupling release above.
        //
        // AC-392: and with it, whatever was still waiting in that pane's inbox. Nobody is left to read it — the CLI
        // that would have called read_inbox is gone — so holding it only grows for the life of the app, and a reused
        // pane id would inherit another session's unread mail. The append-only notify trail is deliberately not
        // touched: it is the durable record of what was sent, and a record a closing session can erase is not one.
        //
        // AC-393: and whatever it had claimed. This is the whole of what keeps a claim from outliving its agent —
        // there is no expiry and no heartbeat in phase 1 — so a session that ends without releasing must not leave
        // its neighbours working around a worktree nobody is on any more.
        _agentCoordinator?.Forget(session.PaneId);
        _agentMessages?.Forget(session.PaneId);
        _agentClaims?.Forget(session.PaneId);

        // Tear down the session's worktree now that its process is gone (AC-85): a clean one is removed with its
        // branch, one that holds work is kept and marked retained (cleanup-policy A). Keyed on the pane the worktree
        // was created for. Best-effort — closing a session must not fail on a worktree that will not release, and the
        // startup reconcile is the net that catches whatever slips through.
        if (_worktreeManager is not null && session.WorktreeBranch is not null)
        {
            try
            {
                await _worktreeManager.ReleaseAsync(session.PaneId);
            }
            catch (Exception)
            {
                // Left for the startup reconcile.
            }

            // A closed session's worktree was just removed or retained; keep the status-bar counter current.
            _ = Worktrees.RefreshCountAsync();
        }

        if (ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.Count == 0
                ? null
                : Sessions[Math.Min(index, Sessions.Count - 1)];
        }

        if (Sessions.Count == 0)
        {
            IsZoomed = false;
        }
    }

    // --- IEmbeddedSessionHost (AC-122): sessions a plugin workspace runs inside its own full-surface body. ---
    //
    // Embedded sessions are deliberately kept out of Sessions. A session there gets a hidden grid container that
    // would build a rival view over its pty (the TTY-rebuild hazard), plus a place in the sidebar and the
    // selection cycle, and it is counted where the grid counts sessions. Instead the host holds them here, keyed
    // by the plugin workspace that owns them, and tears them down when that workspace (or the app) closes.
    private readonly Dictionary<string, List<SessionPanelViewModel>> _embeddedSessions = new(StringComparer.Ordinal);

    // The end-signal behind each embedded session's IEmbeddedSession.Completion, keyed by the session it belongs to.
    // Completed by _TeardownEmbeddedSessionAsync whatever ends the session — a workspace close, an explicit close, a
    // self-close, or the isolation refusal below — so an embedder (Autopilot) awaiting the session can tell it died
    // rather than hang. Same UI-thread affinity as _embeddedSessions.
    private readonly Dictionary<SessionPanelViewModel, TaskCompletionSource<string?>> _embeddedSessionEnded = [];

    public IEmbeddedSession Embed(string workspaceId, EmbeddedSessionRequest request)
    {
        // Null only in a graph with no session machinery (design-time/tests); a real host has both. The registry
        // yields a null host in that graph, so WorkspaceContext throws before ever reaching this — but guard anyway
        // rather than trust a caller.
        if (_sessionFactory is null || _sessionProfileStore is null)
        {
            throw new InvalidOperationException("This host cannot embed sessions.");
        }

        var session = _sessionFactory();
        // The plugin workspace, not EnsureSessionWorkspace's forced Sessions desk: that would switch focus to a
        // Sessions tab and put the session where BelongsToActiveWorkspace shows it in the grid.
        session.WorkspaceId = workspaceId;
        // What the usage trail needs to tell this session's spend apart from an operator's own (AC-251). The
        // workspace cannot stand in for the run: a plugin runs every one of its runs in the same workspace, so
        // only the embedder knows which run this session is for.
        session.RunKind = UsageRunKind.Embedded;
        session.RunId = request.RunId;
        session.RunLabel = request.RunLabel;
        session.Title = string.IsNullOrWhiteSpace(request.ProfileId) ? "Session" : request.ProfileId;
        _SeedSessionPreferences(session);

        // Not OnSessionCloseRequested: that routes through CloseSessionAsync, which early-returns for a session that
        // is not in Sessions — an embedded one never is — and would leave its pty and child process running. Embedded
        // sessions tear down through their own path.
        session.CloseRequested += OnEmbeddedSessionCloseRequested;
        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;

        if (!_embeddedSessions.TryGetValue(workspaceId, out var owned))
        {
            owned = [];
            _embeddedSessions[workspaceId] = owned;
        }

        owned.Add(session);

        // The end-signal for this session's Completion; completed on teardown whatever ends it (carrying the reason when
        // the host ended it itself — the isolation refusal in the start below), so an embedder awaiting the session is
        // never left hanging and can show why it ended.
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _embeddedSessionEnded[session] = ended;

        // An autonomous run asks for its composer off from the first render (AC-174) so the operator does not type into
        // a session that drives itself; the surface's Intervene affordance re-enables it. Set before the view is built.
        if (request.StartWithInputDisabled)
        {
            session.IsInputEnabled = false;
        }

        // ViewLocator resolves SessionViewModel -> SessionView; the plugin body places this one Control. Because the
        // VM is not in Sessions, no second grid container fights it for the same pty.
        var view = new ContentControl { Content = session };

        // Start after the view exists. The pane id is stable from construction, so it is safe to hand back now while
        // the driver launches; a failed start leaves the session showing its own error rather than taking the app down.
        _ = _StartEmbeddedSessionAsync(session, request);

        return new EmbeddedSession(view, session, ended.Task, enabled => _SetEmbeddedInputEnabled(session, enabled), () => _CloseEmbeddedSessionAsync(session));
    }

    public void CloseForWorkspace(string workspaceId)
    {
        if (!_embeddedSessions.TryGetValue(workspaceId, out var owned))
        {
            return;
        }

        _embeddedSessions.Remove(workspaceId);
        foreach (var session in owned)
        {
            _ = _TeardownEmbeddedSessionAsync(session);
        }
    }

    private void OnEmbeddedSessionCloseRequested(object? sender, EventArgs e)
    {
        // A self-closing embedded session (an "exit" turn with auto-close on) ends through the same path a body's
        // explicit IEmbeddedSession.CloseAsync uses, so whichever fires first tears it down and the other is a no-op.
        if (sender is SessionPanelViewModel session)
        {
            _ = _CloseEmbeddedSessionAsync(session);
        }
    }

    // Ends one embedded session: drop it from its workspace's list and tear it down. Idempotent — a session that is
    // no longer listed (already closed, whether by CloseForWorkspace, a self-close, or an earlier CloseAsync) is not
    // torn down twice, which is what lets the body's CloseAsync and the session's own CloseRequested both fire safely.
    private Task _CloseEmbeddedSessionAsync(SessionPanelViewModel session, string? endReason = null)
    {
        foreach (var (workspaceId, owned) in _embeddedSessions)
        {
            if (owned.Remove(session))
            {
                if (owned.Count == 0)
                {
                    _embeddedSessions.Remove(workspaceId);
                }

                return _TeardownEmbeddedSessionAsync(session, endReason);
            }
        }

        return Task.CompletedTask;
    }

    private async Task _StartEmbeddedSessionAsync(SessionViewModel session, EmbeddedSessionRequest request)
    {
        if (_sessionProfileStore is null)
        {
            return;
        }

        try
        {
            var profiles = await _sessionProfileStore.LoadAsync();

            // The workspace may have closed (or the app quit) while the profile store was read. A teardown in that
            // window disposed a session that had no runtime yet — a no-op — so starting it now would spawn a pty and
            // child process nothing tracks. Bail out if this session is no longer one we own.
            if (!_IsEmbeddedSessionLive(session))
            {
                return;
            }

            // A profile's identity is its Label (SessionProfile has no id), so the request's ProfileId is matched by
            // label; a request that names nothing, or names one that is gone, falls back to the first configured
            // profile so the workspace still starts on a real one.
            var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, request.ProfileId, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault();
            if (profile is null)
            {
                // No profiles configured at all: the session stays in its unstarted state rather than crash.
                return;
            }

            // Isolate first when asked (AC-85): an embedded run that edits files — Autopilot — gets its own worktree
            // and branch rather than the operator's real checkout, keyed on this session's pane like the dialog path.
            string? workingDirectory;
            try
            {
                workingDirectory = await _ResolveEmbeddedWorkingDirectoryAsync(session, request, profile);
            }
            catch (Exception isolationFailure)
            {
                // Isolation was asked for and could not be done. Never fall back to the operator's real checkout — that
                // is the contamination isolation exists to prevent. Say why, then stand the session down through the
                // same close path the refusal below uses: that releases any worktree and completes its Completion with
                // the reason, so an awaiting run (Autopilot's step wait) treats it as a failed step it can explain rather
                // than hanging on a session that never reports.
                var reason = $"Could not isolate this run: {isolationFailure.Message}";
                session.Statusline = reason;
                await _CloseEmbeddedSessionAsync(session, reason);
                return;
            }

            // Which project this run works on (AC-320), before the start rather than after: the launch asks every
            // plugin what it gives a starting session, and that answer may depend on the project, so a project
            // established afterwards would arrive too late to be used.
            await _ApplyEmbeddedProjectAsync(session, request);

            // An SDK session on the requested permission mode (default "ask"), the requested model where the profile
            // offers a choice (AC-174 — a CEO plan picks one per step; null keeps the app default), and app-default
            // effort, with the profile's own start defaults in the generic OptionDefaults map — the same shape
            // StartSessionForPluginAsync builds. When the request names an MCP set, the session is restricted to exactly
            // those servers (AC-174 minimal-MCP-per-step: fewer tokens, tighter least-privilege); an empty set keeps the
            // host's usual selection. A self-driving run (AC-152) asks for a more autonomous mode, and when it opts into
            // the "worktree is the boundary" stance (PreApproveAllTools, AC-215) its SDK tool permissions — including
            // shell and edits — are auto-allowed here rather than prompted; the host's ConsentBroker still gates the
            // host's own MCP tools (terminal, worktree, verify), which are never in the pre-approval set.
            await session.StartConfiguredAsync(
                profile,
                _ResolveEmbeddedPermissionMode(request),
                SessionOptionCatalog.ModelForValue(request.Model),
                SessionOptionCatalog.DefaultEffort,
                enabledMcpServerNames: request.McpServers is { Count: > 0 } servers
                    ? servers.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null,
                workingDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                resume: null,
                launchOptions: _EmbeddedLaunchOptions(profile, request),
                // Pre-authorize the run's own control tools (AC-215) so a self-driving step never stalls mid-run on a
                // permission prompt for autopilot_step_done / autopilot_validate it has no one to answer — and, when the
                // run opts into "worktree is the boundary" (Raymond 2026-07-23), auto-allow every tool so an autonomous
                // isolated run can run its work (Bash, edits, git) without a prompt it cannot answer.
                preApprovedTools: request.PreApprovedTools,
                preApproveAllTools: request.PreApproveAllTools);

            // Closed while the driver was launching: the teardown that ran then disposed a session whose runtime did
            // not exist yet, so tear it down now that it does — or its pty and child process outlive the workspace.
            if (!_IsEmbeddedSessionLive(session))
            {
                await _TeardownEmbeddedSessionAsync(session);
                return;
            }

            // Confinement was asked for, so run the agent only when the session actually started AND its provider keeps
            // its file tools inside the directory it runs in. Refuse rather than risk contamination (Raymond: safety over
            // function): close the session — releasing any worktree and completing its Completion so an awaiting embedder
            // unblocks — and say why. The check precedes the brief, so the agent never runs.
            if (_EmbeddedConfinementRefusal(request, profile.Label, session.IsSessionReady, session.Capabilities.ConfinesFileAccessToWorkingDirectory) is { } refusal)
            {
                session.Statusline = refusal;
                await _CloseEmbeddedSessionAsync(session, refusal);
                return;
            }

            // The runtime is up now (StartConfiguredAsync awaited it), so an opening turn submitted here cannot race the
            // "session has not started yet" gate a message sent right after EmbedSession would hit (AC-174). This is how
            // an autonomous embedded run — an Autopilot step agent — is set going without a human: its task brief is the
            // first turn. The CEO planning round leaves this null and waits for the operator instead.
            if (request.InitialUserMessage is { Length: > 0 } opening && session.IsSessionReady)
            {
                session.InjectAndSubmit(opening);
            }
        }
        catch (Exception)
        {
            // A failed embedded start must not take the app down — and it must not leave the session's Completion
            // unresolved either, or an embedder awaiting it (an Autopilot step) hangs forever. If this session is still
            // one we own, close it with a reason so its Completion resolves and the awaiting run records a failed step
            // instead of hanging until the workspace is closed. Best-effort: the start's own failure handler never
            // rethrows, even if the teardown itself faults.
            try
            {
                if (_IsEmbeddedSessionLive(session))
                {
                    await _CloseEmbeddedSessionAsync(session, "The embedded session failed to start.");
                }
            }
            catch (Exception)
            {
                // Nothing more to do; the session surfaces its own failed state.
            }
        }
    }

    // Why an embedded run that asked to be confined must not start — null when it may proceed (AC-174, AC-191).
    //
    // The gate fires on the same condition that puts the confine flag in the launch options (_EmbeddedLaunchOptions'
    // addConfine): asking a provider to confine and then not checking that it does is worse than no gate at all, because
    // the caller builds trust on the answer. Two paths ask for it and both are covered: an isolated run (its own
    // worktree, AC-85) and a run confined to the folder as given without a worktree — the non-git Autopilot path and the
    // CEO validator that reads a run's accumulated work (AC-174).
    //
    // Three ways it fails, all refused: (1) there is no working directory to be held to, so the promise is empty; (2) the
    // provider does not confine — a local model reaches files through MCP servers rooted elsewhere, and a Claude profile
    // stops confining in a permission-bypassing mode — so handing it the brief would let an autonomous,
    // prompt-injectable agent write outside the folder the caller chose; (3) the start failed, which leaves Capabilities
    // at their pre-start default (SessionPanelViewModel seeds the fullest-featured set, whose confines flag is true), so
    // a stale "confined" reading must never be taken as licence to run — readiness is checked first.
    // Internal (not private) so a test can drive the refusal without standing up a session.
    internal static string? _EmbeddedConfinementRefusal(EmbeddedSessionRequest request, string profileLabel, bool isSessionReady, bool confinesFileAccess)
    {
        if (!request.IsolateInWorktree && !request.ConfineFileToolsToWorkingDirectory)
        {
            return null;
        }

        // Named for what the run asked for, so the operator reads the refusal against the thing they set up: an isolated
        // run is about its worktree and their real checkout, a confined one about the folder it was pointed at.
        var (attempt, boundary, exposure) = request.IsolateInWorktree
            ? ("isolate", "the worktree", "allowed to edit your real checkout")
            : ("confine", "its working directory", "allowed to reach files outside the folder it was given");

        // Confinement without a folder to confine to is not confinement. An isolated run cannot get here — resolving its
        // worktree throws when there is no directory — but a run confined to the folder as given takes that folder
        // verbatim, and an empty one leaves which directory it lands in to whatever the start falls back on rather than
        // to the caller. A provider that confines natively would then vouch honestly for a folder nobody chose, so the
        // capability check below would wave through a run bounded to somewhere nobody asked for.
        if (!request.IsolateInWorktree && string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return $"Could not {attempt} this run: it asked to be held to its working directory but was given none, so it was refused rather than run wherever the cockpit happens to be.";
        }

        if (isSessionReady && confinesFileAccess)
        {
            return null;
        }

        // The way out covers both routes a bypass mode can arrive by: a step carries the run's autonomy mode, while the
        // validating CEO names none and so runs on whatever its profile stored. Naming only one would send half the
        // refusals looking in the wrong place.
        return isSessionReady
            ? $"Could not {attempt} this run: the \"{profileLabel}\" profile does not confine its file tools to {boundary}, so it was refused rather than {exposure}. A Claude profile stops confining in a permission-bypassing mode — take the profile, or the Autopilot autonomy mode it runs under, off \"bypassPermissions\" — and a local model never confines, so route work that needs autonomous shell to a Codex profile."
            : $"Could not {attempt} this run: its session did not start, so it was refused rather than run unconfined.";
    }

    // The launch options an embedded session starts with: the profile's own defaults, plus the request's hidden system
    // prompt (AC-180) folded in under its well-known key so it rides the options map to whichever driver runs the
    // session — each applies it its own way. A blank prompt leaves the profile defaults untouched. When the request
    // names its own permission mode, the profile's stored permission-mode default is dropped so the explicit request
    // mode is the one that reaches the driver (see the comment on dropProfilePermissionMode below).
    // Internal (not private) so a test can drive the request-mode-vs-profile-default precedence directly.
    internal static IReadOnlyDictionary<string, string>? _EmbeddedLaunchOptions(SessionProfile profile, EmbeddedSessionRequest request)
    {
        var defaults = profile.Defaults?.OptionDefaults;
        var addPrompt = !string.IsNullOrWhiteSpace(request.AppendSystemPrompt);
        // An isolated embedded run asks its driver to confine file tools to the worktree (AC-174): a CLI provider that
        // already confines ignores it; a local model honours it by re-rooting its file servers there and refusing every
        // escape channel, then vouches confinement so the fail-closed gate lets it run. The flag rides the options map so
        // it reaches every provider without a signature change.
        var addConfine = request.IsolateInWorktree || request.ConfineFileToolsToWorkingDirectory;
        // The embedded run's explicit permission mode (an Autopilot step's autonomy mode, AC-152) is a deliberate
        // per-run choice and must win over the profile's own stored permission-mode default. The host carries that
        // explicit mode as the typed StartAsync permissionMode, but the driver's launch-option merge
        // (PluginSessionDriverAdapter._MergePermissionMode) keeps a permission-mode already present in the launch options
        // over that typed fold — by design, so an operator's launch-time dropdown choice is never overridden. If the
        // profile's stored permission-mode (e.g. a "work" profile saved on bypassPermissions) were left in these launch
        // options, it would defeat the explicit request mode (acceptEdits): the driver would start in bypass, the session
        // would report unconfined, and an isolate-in-worktree run's fail-closed confinement gate would be unpassable on
        // that profile — so Autopilot could never run on it. Drop the profile's permission-mode default here whenever the
        // request names its own mode, leaving the explicit request mode the one that reaches the driver. Provider-neutral:
        // keyed on the well-known permission-mode option, not on any one provider or brand.
        var dropProfilePermissionMode = !string.IsNullOrWhiteSpace(request.PermissionMode) && defaults is { Count: > 0 };
        if (!addPrompt && !addConfine && !dropProfilePermissionMode)
        {
            return defaults;
        }

        var options = defaults is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        if (dropProfilePermissionMode)
        {
            options.Remove(Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.PermissionMode);
        }

        if (request.AppendSystemPrompt is { } prompt && !string.IsNullOrWhiteSpace(prompt))
        {
            options[Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.AppendSystemPrompt] = prompt.Trim();
        }

        if (addConfine)
        {
            options[Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory] = "true";
        }

        return options;
    }

    // Gives an embedded session the project it is working on (AC-320). Deliberately does not mark that project as
    // opened the way a New-session launch does: that ordering is "what the operator works on", and a run started by
    // an automation is not the operator opening it.
    // Internal (not private) so a test can drive the resolution against a session directly.
    internal async Task _ApplyEmbeddedProjectAsync(SessionPanelViewModel session, EmbeddedSessionRequest request)
    {
        if (request.WorkingDirectory is { Length: > 0 } directory)
        {
            session.ProjectId = await _ProjectIdForDirectoryAsync(directory);
        }
    }

    // The project a session belongs to when nobody said which (AC-320): no operator picked one and there is no
    // session it descends from, so the folder it runs in answers — a project is identified by the folder it owns.
    // Shared by the two routes in that position, an embedded run and a plugin-started session, so they cannot drift
    // into placing the same folder on different projects.
    //
    // The directory as requested, never the isolated one a start derives from it: a run's own worktree belongs to no
    // project, the repository it was cut from does.
    private async Task<string?> _ProjectIdForDirectoryAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        IReadOnlyList<WorktreeRecord> worktrees = [];
        try
        {
            // The projects list is filled by a fire-and-forget read at startup, so a workspace that embeds as its body
            // is built — a plugin workspace restored as the active tab — can get here first and find it empty. Read it
            // now in that case rather than answer "no project" on a race.
            if (Projects.Projects.Count == 0)
            {
                await Projects.LoadAsync();
            }

            if (_worktreeManager is not null)
            {
                worktrees = await _worktreeManager.ListAsync();
            }
        }
        catch (Exception)
        {
            // Neither read is worth a session. The registry is only how a run pointed straight at a worktree finds its
            // repository, and an unread projects list costs the same one answer — while an exception here would fail
            // the start outright: an embedded start's catch stands the whole session down.
        }

        return EmbeddedSessionProject.Resolve(Projects.Projects, worktrees, directory)?.Id;
    }

    // Embedded isolation (AC-85/AC-174), the automated counterpart of _ResolveIsolatedWorkingDirectoryAsync. A run that
    // does not ask to be isolated runs in the folder as given. A run that does asks for a promise it must not silently
    // break: when no worktree can be made — no worktree manager, no directory, or a directory that is not a git
    // repository — this throws to the start's own catch, which stands the run down with the reason rather than let it
    // edit the operator's real checkout. A worktree that fails to create throws the same way (Raymond: safety over
    // function — an isolated run that cannot be isolated must not run).
    private async Task<string?> _ResolveEmbeddedWorkingDirectoryAsync(SessionPanelViewModel session, EmbeddedSessionRequest request, SessionProfile profile)
    {
        if (!request.IsolateInWorktree)
        {
            return request.WorkingDirectory;
        }

        // A run's shared worktree (AC-174, Raymond 2026-07-22): the run already created one worktree and every step runs
        // in it so their work accumulates on one branch. Run there as-is — do not create a per-step worktree, and do not
        // set WorktreeBranch (this session does not own the worktree; the run does, and the run's lifetime keeps it), so
        // closing the step does not tear the shared worktree down. The isolation gate still runs (the caller kept
        // IsolateInWorktree true), so a non-confining provider is still refused here.
        if (!string.IsNullOrWhiteSpace(request.WorktreePath))
        {
            return request.WorktreePath;
        }

        if (_worktreeManager is null)
        {
            throw new InvalidOperationException("worktree isolation is not available here (no worktree manager).");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || await _worktreeManager.DetectRepositoryAsync(request.WorkingDirectory) is null)
        {
            throw new InvalidOperationException("the working directory is not a git repository, so no isolated worktree can be created.");
        }

        var worktree = await _worktreeManager.CreateForSessionAsync(session.PaneId, profile.Label, request.WorkingDirectory);
        session.WorktreeBranch = worktree.Branch;
        return worktree.Path;
    }

    /// <summary>
    /// Creates one git worktree for a multi-session run (AC-174, Raymond 2026-07-22) — backs
    /// <see cref="Cockpit.Plugins.Abstractions.ICockpitHost.CreateRunWorktreeAsync"/>. Returns its path and branch, or
    /// null when there is no worktree manager or <paramref name="repositoryDirectory"/> is not a git repository. Keyed to
    /// a fresh id (not a session pane), so it is the run's to reuse across every step and persists as the merge-ready
    /// deliverable after the run.
    /// </summary>
    public async Task<Cockpit.Plugins.Abstractions.Workspaces.PluginWorktreeInfo?> CreateRunWorktreeAsync(string repositoryDirectory, string? label, CancellationToken cancellationToken)
    {
        if (_worktreeManager is null
            || string.IsNullOrWhiteSpace(repositoryDirectory)
            || await _worktreeManager.DetectRepositoryAsync(repositoryDirectory, cancellationToken) is null)
        {
            return null;
        }

        var worktree = await _worktreeManager.CreateForSessionAsync(Guid.NewGuid().ToString("N"), label, repositoryDirectory, cancellationToken: cancellationToken);
        return new Cockpit.Plugins.Abstractions.Workspaces.PluginWorktreeInfo(worktree.Path, worktree.Branch);
    }

    // Where a freshly isolated session forked from, when that is not simply "the latest" (AC-349): the source branch
    // was updated first, or it could not be and the fork is older than the remote. Silent in the ordinary case —
    // there is no news in a session starting on the tip everyone expects it to. Driven by the manager's event rather
    // than by the record each creation returns, so a start that is cancelled or fails afterwards still leaves the
    // operator knowing their own branch moved. Raised on ToastHost rather than through IToastService for the same
    // circular-dependency reason as the update toast above.
    private void _ToastWorktreeSource(WorktreeSourceRefresh refresh)
    {
        if (refresh.Notice is not { } notice)
        {
            return;
        }

        // Information for the two outcomes where everything went as it should — the branch was brought forward, or
        // the session started from the upstream and left the branch alone. The rest are cases where the session is
        // running on an older base than it could have, which is the kind of thing worth catching an eye.
        var severity = refresh.Outcome is WorktreeSourceOutcome.FastForwarded or WorktreeSourceOutcome.ForkedFromUpstream
            ? ToastSeverity.Information
            : ToastSeverity.Warning;

        // Marshalled, because this is reached from a plugin-driven run start as well as from the dialog, and there
        // the await continuation carries no UI context: ToastHost.Add touches a bound collection and starts a
        // DispatcherTimer, neither of which survives being done off the UI thread.
        _OnUiThread(() => ToastHost.Add(notice, severity, null, null));
    }

    // The permission mode an embedded run starts in: the request's named mode (matched case-insensitively), else the
    // app default ("ask"). A named mode that is not recognised falls back to the default rather than failing the start.
    private static PermissionModeOption _ResolveEmbeddedPermissionMode(EmbeddedSessionRequest request) =>
        string.IsNullOrWhiteSpace(request.PermissionMode)
            ? SessionOptionCatalog.DefaultPermissionMode
            : SessionOptionCatalog.AllPermissionModes.FirstOrDefault(mode => string.Equals(mode.Value, request.PermissionMode, StringComparison.OrdinalIgnoreCase))
                ?? SessionOptionCatalog.DefaultPermissionMode;

    /// <summary>Whether <paramref name="session"/> is still an embedded session this host owns — false once its workspace closed and it was torn down, which is how a start racing that teardown knows to stand down.</summary>
    private bool _IsEmbeddedSessionLive(SessionPanelViewModel session) =>
        _embeddedSessions.Values.Any(owned => owned.Contains(session));

    // Backs IEmbeddedSession.SetInputEnabled (AC-174): toggles the embedded session's composer, marshalled to the UI
    // thread since a plugin (the Autopilot run's Intervene affordance) can call it from anywhere.
    private static void _SetEmbeddedInputEnabled(SessionViewModel session, bool enabled)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            session.IsInputEnabled = enabled;
        }
        else
        {
            Dispatcher.UIThread.Post(() => session.IsInputEnabled = enabled);
        }
    }

    private async Task _TeardownEmbeddedSessionAsync(SessionPanelViewModel session, string? endReason = null)
    {
        session.PropertyChanged -= OnSessionPropertyChanged;
        session.CloseRequested -= OnEmbeddedSessionCloseRequested;
        _lastStatus.Remove(session);

        // Signal the session's end to anyone awaiting its Completion (Autopilot's step wait) before disposing it, so a
        // waiter unblocks whether the session finished its work or is being torn down for any other reason — carrying the
        // reason when the host ended it itself (isolation refused), else null. Idempotent: a second teardown finds none.
        if (_embeddedSessionEnded.Remove(session, out var ended))
        {
            ended.TrySetResult(endReason);
        }

        // Best-effort for the same reason as the grid close path — and more so here, because this runs
        // fire-and-forget (`_ = _TeardownEmbeddedSessionAsync(session)`), so a throwing dispose would skip the teardown
        // below and take the exception with it into a task nobody observes.
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The session is already unhooked and its waiters released; what still matters is the teardown below.
        }

        // Mirror CloseSessionAsync's driver-side teardown: release any terminal couplings, forget the agent-presence
        // enrollment, the pane's unread inbox and its resource claims, and release the session's worktree.
        _terminals?.SessionEnded(session.PaneId);
        _agentCoordinator?.Forget(session.PaneId);
        _agentMessages?.Forget(session.PaneId);
        _agentClaims?.Forget(session.PaneId);
        if (_worktreeManager is not null && session.WorktreeBranch is not null)
        {
            try
            {
                await _worktreeManager.ReleaseAsync(session.PaneId);
            }
            catch (Exception)
            {
                // Left for the startup reconcile, same as the grid close path.
            }
        }
    }

    /// <summary>
    /// Close affordance entry point (#11): a busy session flips its sidebar row to an inline Close/Keep
    /// prompt first, so a running turn is never killed on a single click; an idle/waiting/done session
    /// closes straight away.
    /// </summary>
    [RelayCommand]
    private async Task RequestCloseSessionAsync(SessionPanelViewModel session)
    {
        if (session.RequiresCloseConfirmation)
        {
            session.IsConfirmingClose = true;
            return;
        }

        await CloseSessionAsync(session);
    }

    /// <summary>Confirms a pending close from the inline prompt and tears the session down.</summary>
    [RelayCommand]
    private async Task ConfirmCloseSessionAsync(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
        await CloseSessionAsync(session);
    }

    /// <summary>Dismisses the inline close prompt, keeping the session.</summary>
    [RelayCommand]
    private void CancelCloseSession(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
    }

    [RelayCommand]
    private void ToggleZoom()
    {
        IsZoomed = !IsZoomed;
    }

    /// <summary>
    /// Disposes every live session on app shutdown so each child claude process is killed and releases
    /// its MCP permission-server connection — otherwise those open SSE streams keep the server (and the
    /// whole process) alive after the window closes (bug #32).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Stop the hourly update timer (AC-188) so it does not keep ticking against a disposed view model.
        _periodicUpdateTimer?.Stop();

        // The key holder is a process-wide singleton, so leaving this wired would keep the whole view model alive
        // past its window (AC-41). The worktree manager is one too, and its notice handler holds this view model
        // just as firmly (AC-349).
        _secretKeyHolder.UnprotectedSecretsWritten -= OnUnprotectedSecretsWritten;
        if (_worktreeManager is not null)
        {
            _worktreeManager.SourceRefreshed -= _ToastWorktreeSource;
        }

        foreach (var session in Sessions.ToList())
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnSessionCloseRequested;
            await session.DisposeAsync();
        }

        // Embedded sessions (AC-122) live outside Sessions, so they need disposing here too or their pty outlives
        // the app.
        foreach (var session in _embeddedSessions.Values.SelectMany(owned => owned))
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnEmbeddedSessionCloseRequested;
            await session.DisposeAsync();
        }

        _embeddedSessions.Clear();
        Sessions.Clear();
        _lastStatus.Clear();
    }
}
