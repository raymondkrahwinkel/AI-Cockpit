using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Cockpit.App.Docking;
using Cockpit.App.Diagnostics;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Whiteboard;
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
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Abstractions.Shell;
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
using Cockpit.Infrastructure.Configuration;
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

// Reuses the existing `SessionViewModel`/`SessionView` per panel — this view model only adds the manager layer around
// it (#32).
public partial class CockpitViewModel : ViewModelBase, ISingletonService, IAsyncDisposable, IPluginContributionSink, IEmbeddedSessionHost
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<SessionViewModel>? _sessionFactory;
    private readonly Func<TtyViewModel>? _ttySessionFactory;
    private readonly ISessionProfileStore? _sessionProfileStore;
    private readonly ITtySessionProviderResolver? _ttyProviderResolver;
    private readonly IWorktreeManager? _worktreeManager;
    private readonly ITerminalAccessRegistry? _terminals;
    private readonly IDiagramAccessRegistry? _diagrams;
    private readonly IWhiteboardAccessRegistry? _whiteboards;
    private readonly IWorkspaceAgentCoordinator? _agentCoordinator;
    private readonly IAgentMessageInbox? _agentMessages;
    private readonly IAgentResourceClaims? _agentClaims;
    private readonly IAgentLineBudget? _agentLineBudget;
    private readonly IClaimCollisionMonitor? _claimCollisionMonitor;
    private readonly LiveSessionRegistry? _liveSessions;
    private readonly ISessionDialogService? _dialogService;
    // AC-512: "Run setup again" (Help menu) reopens it; null (design-time/tests, or nothing registered) is a no-op.
    private readonly IFirstRunWizard? _firstRunWizard;
    private readonly HelpService? _help;
    // AC-512: the seam behind OpenGuideCommand — defaults to the real ExternalLink.TryOpen (also covering the
    // parameterless design-time constructor below), replaceable in tests (see ExternalLinkTests' own remark on
    // why a real URL cannot be exercised there directly).
    private readonly Func<string, bool> _tryOpenExternalLink = ExternalLink.TryOpen;
    private readonly SessionStateRecorder? _sessionStateRecorder;
    private readonly ISessionStateStore? _sessionStateStore;
    private readonly SessionRestorePlanner? _sessionRestorePlanner;
    private readonly IWorktreeReconcileGate? _worktreeReconcileGate;
    private readonly ILogger<CockpitViewModel>? _logger;
    private OptionsOpenMeasurement? _optionsOpenMeasurement;

    // Composes what a session started from a project opens with (AC-164).
    private readonly ProjectQuickStart? _projectQuickStart;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly IShortcutSettingsStore? _shortcutSettingsStore;
    private readonly IBackupService? _backupService;
    // The assistant's own memory files, loose from the rest of the cockpit backup (AC-657) — same service the
    // assistant's remember/note_state MCP tools write through.
    private readonly IAssistantMemory? _assistantMemory;
    private readonly IAppRestartService? _appRestart;
    private readonly IUpdateService? _updates;
    private readonly IUpdateSettingsStore? _updateSettingsStore;
    // A static singleton, so the subscription is unwired in DisposeAsync — a view model that outlived its window would
    // otherwise be kept alive by it, and refresh a dead Security tab (AC-41).
    private readonly ISecretKeyHolder _secretKeyHolder = SecretKeyHolder.Shared;
    private ShortcutSettings _shortcutSettings = ShortcutSettings.Default;
    private readonly ITranscriptDisplaySettingsStore? _transcriptDisplaySettingsStore;
    private readonly IUsagePillSettingsStore? _usagePillSettingsStore;
    private readonly ISessionBehaviorSettingsStore? _sessionBehaviorSettingsStore;
    private readonly IScreenshotSettingsStore? _screenshotSettingsStore;
    private readonly ILayoutSettingsStore? _layoutSettingsStore;
    private readonly IDockPanelRegistry? _dockPanelRegistry;
    private readonly IDebugSettingsStore? _debugSettingsStore;
    private readonly DiagnosticsBackgroundService? _diagnosticsBackgroundService;
    private readonly IDelegationMcpToggle? _delegationMcpToggle;
    private readonly ISessionResourceResolver? _sessionResourceResolver;
    private readonly IConsentBroker? _consentBroker;
    private readonly ResourceMonitor? _resourceMonitor;
    // Stops a slow SampleResourcesAsync read from overlapping the next tick — a second walk of ResourceMonitor's
    // per-process state would corrupt the CPU delta. UI-thread only, so a plain field is enough.
    private bool _samplingResources;
    private readonly IVoiceSettingsStore? _voiceSettingsStore;
    private readonly ITerminalSettingsStore? _terminalSettingsStore;
    private readonly IWorktreeSettingsStore? _worktreeSettingsStore;
    private readonly ICloneSettingsStore? _cloneSettingsStore;
    private readonly IAudioDeviceProvider? _audioDeviceProvider;
    private readonly IAudioCaptureService? _audioCapture;

    // Only for the voice preview in Options (PreviewVoiceCommand). Sessions reach the queue through their own
    // panel; this holds it because Options has no session to borrow one from.
    private readonly IVoicePlaybackQueue? _voicePlaybackQueue;
    private CancellationTokenSource? _micTestCancellation;
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

    // Handed in by the app at startup rather than taken through the constructor, so the unit-test and design-time
    // graphs — which build this view-model from the container — never construct a scheduler, never touch the config
    // file, and never leave one running behind a test (AC-234).
    public ScheduledResumeCoordinator? ScheduledResumes { get; set; }

    // The operator's own usage thresholds (AC-233), loaded once and handed to each session as it is created.
    // Null in the graphs that never load them, and every signal then warns where its provider said.
    public UsageThresholdSettings? UsageThresholds { get; set; }

    // The usage-threshold settings screen (AC-233), rendered from what the providers declared. Handed in by the
    // app at startup for the same reason the scheduler is: the test and design-time graphs build a cockpit
    // without one and touch no config.
    public UsageThresholdsViewModel? UsageThresholdSettings { get; set; }

    // Options → Profiles (AC-1001), replacing the standalone ManageProfilesDialog window. Handed in by the app
    // at startup, same reason as the two above — the profile store it needs is not part of this view model's own
    // constructor. Null in the test/design-time graphs, where the category simply shows nothing to edit.
    public ManageProfilesDialogViewModel? Profiles { get; set; }

    // Options → MCP Servers (AC-1002), replacing the standalone McpServersDialog window — same reasoning as
    // Profiles above: the server store it needs is not part of this view model's own constructor. Null in the
    // test/design-time graphs, where the category simply shows nothing to edit.
    public McpServersViewModel? McpServers { get; set; }

    // True once `ApplyOptionsAsync` has refused to write one or more sections (a plugin's `TryStage`, Profiles,
    // or MCP Servers) — read by `OptionsDialog.OnApplyAndClose` to keep the dialog open and the error visible
    // instead of closing over it. Everything that did validate is still committed on the same click (AC-1082).
    public bool OptionsApplyBlocked { get; private set; }

    // The nav tag of the first section that refused, e.g. "profiles" or "plugin:discord" — read by
    // `OptionsDialog.OnApplyAndClose` to jump the sidebar there instead of leaving the operator to search the
    // 15+ categories for whichever one is blocking (AC-1082).
    public string? OptionsApplyBlockedCategoryTag { get; private set; }

    // One row per plugin with a registered settings view (AC-1005), rebuilt on every `BeginOptionsEdit` — the
    // PLUGINS group in the Options sidebar renders straight from this instead of a per-plugin dialog.
    public ObservableCollection<PluginOptionsRowViewModel> PluginOptionsRows { get; } = [];

    // Set by `ApplyOptionsAsync` when a plugin's own `TryStage` refuses the save (AC-1005) — same role as
    // `Profiles.StatusMessage`, just for a plugin row instead of the profile list.
    public string? PluginSettingsError { get; private set; }

    // Kept apart from `Sessions` on purpose: the session grid binds straight to `Sessions` and keeps its own positional
    // cell layout, so reordering the strip must never touch `Sessions` — moving an item there rebuilds its pane (a
    // fresh TTY with no pty → a black terminal) and drags the grid tiles along with the strip (AC-115).
    private readonly List<SessionPanelViewModel> _sidebarOrder = [];

    // AC-561: the strip binds to this collection itself, not to a fresh snapshot handed back on every read — an
    // ItemsControl only tears down and rebuilds the containers for entries an Add/Remove/Move actually touches, so a
    // session with an open ContextMenu that is not part of the change keeps its own Border alive and the popup stays
    private readonly ObservableCollection<SessionPanelViewModel> _visibleSessions = [];

    // Left-menu accordion sections contributed by plugins (#14), shown under the session list.
    public ObservableCollection<PluginSideSection> PluginSideSections { get; } = [];

    // Left-menu launcher buttons contributed by plugins (#14); clicking one runs the plugin's action (typically opening
    // a dialog).
    public ObservableCollection<PluginSideButton> PluginSideButtons { get; } = [];

    // Controls contributed by plugins to every session's header bar, each built per session from that session's own
    // context.
    public ObservableCollection<PluginSessionHeaderItem> PluginSessionHeaderItems { get; } = [];

    // Controls contributed by plugins to every session's banner strip under the transcript (AC-802), each built per
    // session from that session's own context.
    public ObservableCollection<PluginSessionBannerItem> PluginSessionBanners { get; } = [];

    // What plugins can *do* to one session (#: session actions) — gathered into the single menu in every session's
    // header, rather than a button each.
    public ObservableCollection<PluginSessionAction> PluginSessionHeaderActions { get; } = [];

    // Plugin-registered sources of supervised background activities (AC-82) — the status bar shows a counter per source
    // (only while it has activities) and a panel with a Kill per item.
    public ObservableCollection<ISupervisedActivitySource> PluginSupervisedActivities { get; } = [];

    // Sessions-toolbar buttons contributed by plugins (AC-91) — global quick actions shown next to the workspace gear.
    public ObservableCollection<PluginToolbarAction> PluginToolbarActions { get; } = [];

    // The operator's left-menu preference per plugin (#72): where it sits, and whether it shows there at all.
    // Read from the plugin registrations at startup and refreshed when the manager changes one. A plugin the
    // operator never touched is absent, which is what keeps discovery order the default.
    private readonly Dictionary<string, PluginMenuPreference> _pluginMenuPreferences = new(StringComparer.Ordinal);

    // Raised when the left-menu order or visibility changed (#72) — the cue for the sidebar to rebuild.
    public event EventHandler? PluginMenuChanged;

    // Everything the plugins put in the left menu — launcher buttons and inline sections alike — in the order and
    // visibility the operator chose (#72); ties keep the order the plugins were discovered in.
    public IReadOnlyList<PluginMenuEntry> VisibleMenuEntries =>
        PluginSideButtons.Select(button => new PluginMenuEntry(button.PluginId, button, null))
            .Concat(PluginSideSections.Select(section => new PluginMenuEntry(section.PluginId, null, section)))
            .Where(entry => !_IsHiddenInMenu(entry.PluginId))
            // OrderBy is stable, and the buttons come first above — so a plugin contributing both keeps its button
            // above its own section, where a launcher belongs.
            .OrderBy(entry => _MenuOrderOf(entry.PluginId))
            .ToList();

    // AC-937: the entries drawn directly in the sidebar — a pinned plugin, or any section (an inline accordion
    // behind a flyout would be the wrong control, so a section never collapses regardless of its plugin's pin).
    public IReadOnlyList<PluginMenuEntry> PinnedMenuEntries =>
        VisibleMenuEntries.Where(entry => entry.Section is not null || _IsPinnedToSidebar(entry.PluginId)).ToList();

    // AC-937: the entries collapsed behind "Plugins ›" — an unpinned button, and only a button; see PinnedMenuEntries.
    public IReadOnlyList<PluginMenuEntry> CollapsedMenuEntries =>
        VisibleMenuEntries.Where(entry => entry.Section is null && !_IsPinnedToSidebar(entry.PluginId)).ToList();

    // The toolbar buttons in the operator's chosen order/visibility (#72) — the same hide/order rules as the left
    // menu, so a plugin hidden there does not surface a toolbar button either. This is the one list the strip draws
    // from (AC-772): a cockpit-owned action is an entry here like any other, not a second registry beside it.
    public IReadOnlyList<PluginToolbarAction> VisibleToolbarActions =>
        PluginToolbarActions
            .Where(action => !_IsHiddenInMenu(action.PluginId))
            .OrderBy(action => _MenuOrderOf(action.PluginId))
            .ToList();

    // A toolbar action threw (AC-772 criterion 6). Recorded under the action's own title rather than the plugin's
    // display name, which this view model does not carry: the title is what the operator clicked, so it is what
    // makes the banner entry recognisable.
    internal void ReportToolbarActionFailure(string pluginId, string title, string error) =>
        _pluginDiagnostics?.Record(pluginId, title, "toolbar-action", error);

    // Applies a menu preference the plugin manager just persisted, and tells the sidebar to rebuild (#72).
    // Preserves whatever pin (AC-937) the plugin already had — this three-argument member is the #72 order/hide
    // write, which says nothing about pinning.
    public void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu) =>
        ApplyPluginMenuPreference(pluginId, menuOrder, hiddenInMenu, _IsPinnedToSidebar(pluginId));

    // AC-937: same, also setting whether the plugin is pinned top-level in the sidebar.
    public void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu, bool pinnedToSidebar)
    {
        _pluginMenuPreferences[pluginId] = new PluginMenuPreference(menuOrder, hiddenInMenu, pinnedToSidebar);
        PluginMenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private int _MenuOrderOf(string pluginId) =>
        _pluginMenuPreferences.TryGetValue(pluginId, out var preference) ? preference.Order : 0;

    private bool _IsHiddenInMenu(string pluginId) =>
        _pluginMenuPreferences.TryGetValue(pluginId, out var preference) && preference.Hidden;

    private bool _IsPinnedToSidebar(string pluginId) =>
        _pluginMenuPreferences.TryGetValue(pluginId, out var preference) && preference.Pinned;

    private sealed record PluginMenuPreference(int Order, bool Hidden, bool Pinned);

    // Keyboard shortcuts contributed by plugins (#: shortcuts), dispatched alongside the built-in app-action shortcuts.
    public ObservableCollection<PluginShortcut> PluginShortcuts { get; } = [];

    // The currently-active shortcuts (app actions + plugin shortcuts) the view matches key presses against.
    public IReadOnlyList<ShortcutBinding> ActiveShortcuts { get; private set; } = [];

    // Rows for the Options → Shortcuts tab: the editable app-action gestures, then the read-only plugin-contributed
    // ones.
    public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; } = [];

    // Per-plugin settings views (#14) keyed by plugin folder id, opened from any of the gears — the plugin manager's,
    // the left-menu button's, a plugin dialog's — or by the plugin itself.
    public Dictionary<string, PluginSettingsRegistration> PluginSettings { get; } = [];

    // Settings-saved callbacks (#52) keyed by plugin folder id, registered via `ICockpitHost.OnSettingsSaved` and run
    // once the host has performed that plugin's staged write (AC-1004).
    private readonly Dictionary<string, List<Action>> _settingsSavedHandlers = [];

    // The "Plugins" Options tab (#14): install/enable/disable/remove installed plugins.
    public PluginManagerViewModel Plugins { get; }

    // The delegated-tasks view (#67): work other sessions handed to a profile, which has no tab of its own.
    public DelegatedTasksViewModel DelegatedTasks { get; }

    // The operator's read-only view on the agent line (AC-397) — the only window on traffic they are not part of.
    public AgentLineInspectorViewModel AgentLineInspector { get; } = new();

    // The git worktrees the cockpit created (AC-85): the status-bar counter and the management dialog read this one
    // shared view model.
    public WorktreesViewModel Worktrees { get; }

    // The operator's projects (AC-161): the Options tab that manages them and the sidebar section that starts them read
    // this one shared view model.
    public ProjectsViewModel Projects { get; }

    // The workspace tab strip and the active workspace's panes.
    public WorkspacesViewModel Workspaces { get; }

    // It asks because none of it comes back: a dashboard's whole arrangement, or every session tied to it.
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
            // Counted apart rather than folded into "sessions", which is the drift this method used to warn about in
            // its own comment: "3 sessions, which will be stopped" reads as a lie the moment one of the three never
            // started (AC-410).
            var onDesk = Sessions.Where(session => session.WorkspaceId == workspace.Id).ToList();
            var started = onDesk.Count(session => !session.HasRestoreOffer)
                // Embedded sessions are never restored-but-unstarted (AC-410's "Niet" list), so they always belong on
                // the started side.
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

    // "3 widgets" / "1 session", or null when there is nothing to lose — an empty workspace needs no warning about what
    // it holds.
    private static string? _Count(int count, string noun) =>
        count == 0 ? null : count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // How many sessions a plugin workspace runs embedded in its body — kept out of `Sessions`, so the
    // close-confirmation counts them here or it undercounts what the workspace is about to stop.
    private int _EmbeddedSessionCount(string workspaceId) =>
        _embeddedSessions.TryGetValue(workspaceId, out var owned) ? owned.Count : 0;

    // Its sessions go first, through the ordinary close path so each is disposed the way it would be on its own —
    // otherwise they keep running with a WorkspaceId pointing at a workspace that no longer exists: no tab shows them,
    // nothing can reach them, and their pty and child process outlive the desk they belonged to.
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

    // Answers "no" without asking when there is no dialog service (design-time/tests): a graph with no way to ask must
    // not answer yes on the operator's behalf.
    public Task<string?> PickDashboardToImportAsync() =>
        _dialogService is null ? Task.FromResult<string?>(null) : _dialogService.PickDashboardToImportAsync();

    // Picks where to write a dashboard; null without a dialog service, or when the operator backed out.
    public Task<string?> PickDashboardExportPathAsync(string suggestedName) =>
        _dialogService is null ? Task.FromResult<string?>(null) : _dialogService.PickDashboardExportPathAsync(suggestedName);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        _dialogService is null
            ? Task.FromResult(false)
            : _dialogService.ShowConfirmationDialogAsync(title, message, confirmLabel);

    // Whether the session grid applies: sessions exist AND a Sessions workspace is active. A dashboard owns
    // the content area while it is selected, so the grid must stand down even though the sessions themselves
    // keep running — they are hidden, not closed.
    public bool ShowSessionGrid => HasSessionsHere && Workspaces.IsSessionsActive;

    // The "no sessions yet" prompt: only on a Sessions workspace, since a dashboard cannot hold a session and has its
    // own empty state.
    public bool ShowSessionEmptyState => !HasSessionsHere && Workspaces.IsSessionsActive;

    // Whether the workspace now showing holds any session. Deliberately not `HasSessions`: a fresh
    // second workspace has to greet you with the empty state, even while the first one is full of running
    // sessions.
    public bool HasSessionsHere => VisibleSessions.Any();

    // Owns the live toast collection (#61); `Toasts` below is what `CockpitView.axaml`'s overlay actually binds to.
    public ToastHostViewModel ToastHost { get; } = new();

    // Toasts currently shown by the overlay (#61), fed by `Services.ToastService` via `ToastHost`.
    public ObservableCollection<ToastViewModel> Toasts => ToastHost.Toasts;

    // A dismissible banner shown when one or more plugins failed to load (#14) — the app keeps running; details are in
    // Options → Plugins.
    [ObservableProperty]
    private string _pluginFailureBanner = string.Empty;

    // True while the plugin-failure banner should be shown.
    [ObservableProperty]
    private bool _hasPluginFailures;

    // A dismissible banner (AC-208) shown when one or more plugins are sitting at awaiting-approval — new, or their
    // bytes changed since last approved — so that state is visible without opening Plugin store → Installed.
    [ObservableProperty]
    private string _pendingApprovalBanner = string.Empty;

    // True while the pending-approval banner should be shown.
    [ObservableProperty]
    private bool _hasPendingApprovals;

    // Keep the safe-mode banner visible for the whole plugin-free run; restarting exits safe mode (AC-478).
    public bool IsSafeMode => _safeMode;

    // The safe-mode banner's text (AC-478); empty (and so invisible, see `IsSafeMode`) on an ordinary run.
    public string SafeModeBanner => _safeMode
        ? "Safe mode — no plugins were loaded. Plugin manager still works: disable the one that is crashing, then restart."
        : string.Empty;

    // Refresh the plugin-issue banner after phase 2 and on later diagnostics so it never reflects only startup state
    // (#184).
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

    // AC-516: the badge-carrying overload. Kept as its own tiny explicit-interface method (rather than folding a
    // nullable badge into the call above) so the plain path above stays exactly what it was.
    void IPluginContributionSink.AddPluginSideButton(string pluginId, string title, Action onInvoke, SideMenuButtonBadge? badge) =>
        _OnUiThread(() => PluginSideButtons.Add(new PluginSideButton(pluginId, title, onInvoke, badge)));

    void IPluginContributionSink.AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        _OnUiThread(() => PluginSessionHeaderItems.Add(new PluginSessionHeaderItem(createView)));

    void IPluginContributionSink.AddPluginSessionBannerItem(Func<IPluginSessionContext, Control> createView) =>
        _OnUiThread(() => PluginSessionBanners.Add(new PluginSessionBannerItem(createView)));

    void IPluginContributionSink.AddPluginSessionHeaderAction(PluginSessionAction action) =>
        _OnUiThread(() => PluginSessionHeaderActions.Add(action));

    void IPluginContributionSink.AddSupervisedActivityProvider(ISupervisedActivitySource source) =>
        _OnUiThread(() => PluginSupervisedActivities.Add(source));

    void IPluginContributionSink.AddToolbarAction(string pluginId, ToolbarAction action) =>
        _OnUiThread(() => PluginToolbarActions.Add(new PluginToolbarAction(pluginId, action)));

    void IPluginContributionSink.AddPluginShortcut(PluginShortcut shortcut) =>
        _OnUiThread(() => PluginShortcuts.Add(shortcut));

    // Registration touches only this plain dictionary — never a bound ObservableCollection — and every caller is an
    // Avalonia UI-thread callback in practice (a plugin's Initialize).
    void IPluginContributionSink.AddPluginSettings(string pluginId, string pluginName, Func<Control> createView) =>
        PluginSettings[pluginId] = new PluginSettingsRegistration(pluginId, pluginName, createView);

    void IPluginContributionSink.AddPluginSettings(string pluginId, string pluginName, Func<Control> createView, string? category) =>
        PluginSettings[pluginId] = new PluginSettingsRegistration(pluginId, pluginName, createView, category);

    public bool HasPluginSettings(string pluginId) => PluginSettings.ContainsKey(pluginId);

    // There is no standalone settings window for a plugin any more: this deep-links into Options on that plugin's own
    // sidebar row instead, the same shared Save/Close transaction every other category uses, so a change saved through
    // this gear runs the same settings-saved handlers as one saved from any other route (AC-1005).
    public async Task OpenPluginSettingsAsync(string pluginId)
    {
        if (!PluginSettings.ContainsKey(pluginId))
        {
            return;
        }

        await _ShowOptionsAsync($"plugin:{pluginId}");
    }

    // The widget supplies the form's content and the host puts it in the same Save/Close dialog a plugin's own settings
    // use — a widget never builds a window.
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

    // Unlike the three contributions above, registration here touches only this private dictionary — never a bound
    // ObservableCollection — and both members are reached exclusively from Avalonia UI-thread callbacks in practice (a
    // contribution's own constructor, and the settings dialog's Save click), so no dispatcher hop is needed.
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

    // False when no session is open, driving the empty-state welcome screen vs. the session grid (#31).
    public bool HasSessions => Sessions.Count > 0;

    // Column count for the adaptive session grid (#24): one session fills the width; two or more lay out in two columns
    // (so 3–4 form a 2×2), rather than the old fixed two that left a single session pinned to the left half.
    public int GridColumns => VisibleSessions.Count() <= 1 ? 1 : 2;

    // The Zoom toggle only makes sense in the grid layout with more than one session — a single session
    // already fills the pane, single-session layout has no grid to zoom out of, and focus+rail (AC-445)
    // already shows one session large with no grid underneath either.
    public bool ShowZoomButton => !SingleSessionLayout && !FocusRailLayout && VisibleSessions.Count() > 1;

    [ObservableProperty]
    private SessionPanelViewModel? _selectedSession;

    // True while the grid is collapsed to show only `SelectedSession` at full width.
    [ObservableProperty]
    private bool _isZoomed;

    // Options' "show one session at a time" (#24) — the cockpit-wide default, persisted to `LayoutSettings`.
    [ObservableProperty]
    private bool _globalSingleSessionLayout;

    // Options' "stack sessions vertically" — the cockpit-wide default.
    [ObservableProperty]
    private bool _globalStackSessionsVertically;

    // Options' focus+rail layout (AC-441/444/445) — the cockpit-wide default. The effective value is `FocusRailLayout`.
    [ObservableProperty]
    private bool _globalFocusRailLayout;

    // The cockpit-wide default divider weight for the focus+rail split (AC-443).
    [ObservableProperty]
    private double _globalFocusRailWeight = LayoutSettings.DefaultFocusRailWeight;

    // What the active workspace actually does: its own override, else Options' default. Everything that
    // arranges panes reads this; nothing writes it.
    public bool SingleSessionLayout =>
        Workspaces?.Active is { SingleSessionLayout: { } single } active && active.Type == WorkspaceType.Sessions
            ? single
            : GlobalSingleSessionLayout;

    // The active workspace's stacking, its own override else Options'.
    public bool StackSessionsVertically =>
        Workspaces?.Active is { StackSessionsVertically: { } stack } active && active.Type == WorkspaceType.Sessions
            ? stack
            : GlobalStackSessionsVertically;

    // The active workspace's focus+rail choice (AC-441/444), its own override else Options'.
    public bool FocusRailLayout =>
        Workspaces?.Active is { FocusRailLayout: { } rail } active && active.Type == WorkspaceType.Sessions
            ? rail
            : GlobalFocusRailLayout;

    // The active workspace's focus/rail divider weight, its own override else Options'.
    public double FocusRailWeight =>
        Workspaces?.Active is { FocusRailWeight: { } weight } active && active.Type == WorkspaceType.Sessions
            ? weight
            : GlobalFocusRailWeight;

    // Two-way for the Sessions ⚙: whether this desk follows Options. Unticking it starts the override from
    // what the desk is doing right now, so taking control changes nothing until the operator changes
    // something — a checkbox that rearranges your sessions the moment you tick it is one nobody ticks twice.
    public bool WorkspaceFollowsGlobalLayout
    {
        get => Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions
            || (sessions.SingleSessionLayout is null && sessions.StackSessionsVertically is null && sessions.FocusRailWeight is null && sessions.FocusRailLayout is null);
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == WorkspaceFollowsGlobalLayout)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(
                sessions.Id,
                value ? null : SingleSessionLayout,
                value ? null : StackSessionsVertically,
                value ? null : FocusRailWeight,
                value ? null : FocusRailLayout);
            _OnEffectiveLayoutChanged();
        }
    }

    // Two-way for the Sessions ⚙'s own "show one session at a time" — writes this workspace's override, never
    // Options. The three layout modes exclude each other (AC-445): turning this on turns the other two off,
    // the same way `WorkspaceFollowsGlobalLayout` already resets all three.
    public bool WorkspaceSingleSessionLayout
    {
        get => SingleSessionLayout;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == SingleSessionLayout)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(
                sessions.Id, value, value ? false : StackSessionsVertically, FocusRailWeight, value ? false : FocusRailLayout);
            _OnEffectiveLayoutChanged();
        }
    }

    // Two-way for the Sessions ⚙'s own "stack sessions vertically" — writes this workspace's override, never
    // Options. Mutually exclusive with the other two modes (AC-445), see `WorkspaceSingleSessionLayout`.
    public bool WorkspaceStackSessionsVertically
    {
        get => StackSessionsVertically;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == StackSessionsVertically)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(
                sessions.Id, value ? false : SingleSessionLayout, value, FocusRailWeight, value ? false : FocusRailLayout);
            _OnEffectiveLayoutChanged();
        }
    }

    // Two-way for the Sessions ⚙'s own focus/rail divider weight (AC-443) — writes this workspace's override, never
    // Options.
    public double WorkspaceFocusRailWeight
    {
        get => FocusRailWeight;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == FocusRailWeight)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(sessions.Id, SingleSessionLayout, StackSessionsVertically, value, FocusRailLayout);
            _OnEffectiveLayoutChanged();
        }
    }

    // Two-way for the Sessions ⚙'s own focus+rail choice (AC-441/444/445) — writes this workspace's override,
    // never Options. Mutually exclusive with the other two modes (AC-445), see `WorkspaceSingleSessionLayout`.
    public bool WorkspaceFocusRailLayout
    {
        get => FocusRailLayout;
        set
        {
            if (Workspaces?.Active is not { } sessions || sessions.Type != WorkspaceType.Sessions || value == FocusRailLayout)
            {
                return;
            }

            _ = Workspaces.SetSessionLayoutAsync(
                sessions.Id, value ? false : SingleSessionLayout, value ? false : StackSessionsVertically, FocusRailWeight, value);
            _OnEffectiveLayoutChanged();
        }
    }

    // True whenever the multi-pane grid is showing (two or more sessions, not the single-pane/zoom layout, nor the
    // focus+rail layout — the rail auto-fits and has no drag-to-cell or column/row gutters of its own, AC-444 #1):
    // every pane then carries the drag-reorder grip, and the column/row gutters between them are resizable (AC-696).
    public bool StackSessionsInStack => !ShowSinglePane && !ShowFocusRail && VisibleSessions.Count() >= 2;

    // When true, closing the window hides it to the system tray and keeps the app running (#33).
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    // Width in pixels of the left sidebar column (#49), dragged via the `GridSplitter` in `CockpitView.axaml` and
    // persisted so it survives a restart.
    [ObservableProperty]
    private double _sidebarWidth = LayoutSettings.DefaultSidebarWidth;

    // When true the left sidebar is collapsed out of view; the session content takes its space.
    [ObservableProperty]
    private bool _sidebarCollapsed;

    // Width in pixels of the right dock rail's expanded panel (AC-951) — the sidebar's mirror image, same
    // drag/persist wiring via `SetDockRailWidthAsync` and `CockpitView.axaml.cs`.
    [ObservableProperty]
    private double _dockRailWidth = LayoutSettings.DefaultDockRailWidth;

    // Which dock panel is open, by id; null collapses the rail to its 40px strip. Toggled by clicking a rail
    // tab — the same tab again closes it, a different one switches straight to it (one panel open at a time).
    [ObservableProperty]
    private string? _openDockPanelId;

    // Whether the Assistant is docked into the rail instead of its own floating window (AC-950 [c]). Nothing
    // sets this true yet — the field round-trips through LayoutSettings from day one so that sub does not have
    // to hunt down every save call site below.
    [ObservableProperty]
    private bool _assistantDocked;

    // AC-962: how wide the assistant's drop zone is drawn along this window's right edge while its floating chat
    // is being dragged — the overlap between the screen band the rail belongs to and this window. Zero means no
    // drag is running, which is the only state in which the zone is not on screen at all.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssistantDropZoneVisible))]
    private double _assistantDropZoneWidth;

    // Whether the pointer stands inside that zone right now — what makes it light up rather than merely show.
    [ObservableProperty]
    private bool _isAssistantDropZoneActive;

    public bool IsAssistantDropZoneVisible => AssistantDropZoneWidth > 0;

    // What the rail's tab strip lists — read straight off the registry rather than copied into a collection of
    // our own, the same reasoning `WorkspacesViewModel.AvailableWidgets` follows.
    public IReadOnlyList<DockPanelRegistration> DockPanels => _dockPanelRegistry?.Panels ?? [];

    // Whether the rail has anything to offer at all. With no panel registered there is nothing to click, and a
    // 40px strip of empty chrome against the right edge is worse than no rail — so the whole column stands down
    // (AC-953: the Assistant's tab is withdrawn while it is undocked, which is exactly when that happens).
    public bool HasDockPanels => DockPanels.Count > 0;

    // AC-951: the rail reads the registry directly, so it needs telling when that changes — a panel can arrive
    // (or, since AC-953, be withdrawn) long after this view model is built.
    private void _WireDockPanelChanges()
    {
        if (_dockPanelRegistry is not { } registry)
        {
            return;
        }

        registry.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(DockPanels));
            OnPropertyChanged(nameof(HasDockPanels));
        };
    }

    [ObservableProperty]
    private string _layoutSettingsStatus = string.Empty;

    // Mirrors `Cockpit.Core.Debugging.DebugSettings.ShowDebugControls` (#73): show the controls
    // that exist to investigate the cockpit itself — the TTY header's Redraw — rather than to do the work.
    // Off by default; pushed to open sessions so a change takes effect without reopening them.
    [ObservableProperty]
    private bool _showDebugControls;

    // Mirrors `Cockpit.Core.Debugging.DebugSettings.LogDiagnosticSnapshots` (AC-718): a background service writes
    // one diagnostics line to the log every few seconds while this is on. Off by default; pushed to the service
    // immediately so it takes effect without reopening the dialog.
    [ObservableProperty]
    private bool _logDiagnosticSnapshots;

    // Whether the orchestrator (delegation) MCP is offered to sessions (AC-40). It is a cockpit-hosted server, no
    // longer listed in the MCP-servers manager, so this Options toggle is where it is turned on or off. On by
    // default; the change is persisted and takes effect on the next session's servers.
    [ObservableProperty]
    private bool _orchestratorMcpEnabled = true;

    [ObservableProperty]
    private string _debugSettingsStatus = string.Empty;

    // Whether a backup keeps the keys, tokens and webhooks that live in the settings (#70). Off by design: the
    // archive's whole use is that you can put it somewhere — a cloud folder, another machine — and a thing you can
    // put anywhere must not be a key ring.
    [ObservableProperty]
    private bool _backupIncludesCredentials;

    // Never a default.
    [ObservableProperty]
    private bool _backupIncludesProfiles;

    [ObservableProperty]
    private string _backupStatus = string.Empty;

    // The assistant's own memory files, exported/restored on their own (AC-657) — separate from the status line
    // above, since the two archives are unrelated and a status about one must not read as being about the other.
    [ObservableProperty]
    private string _assistantMemoryBackupStatus = string.Empty;

    // The plugins this backup will carry — their binaries and everything they saved.
    public ObservableCollection<BackupPluginViewModel> BackupPlugins { get; } = [];

    // The build this cockpit is (#71): the version, and the commit — which is a nightly's only identity.
    [ObservableProperty]
    private string _currentBuild = string.Empty;

    // Look for a newer build when the cockpit starts. On: an update nobody is told about is an update nobody installs.
    [ObservableProperty]
    private bool _checkForUpdatesOnStartup = true;

    // Also hear about the nightly build of main. Off, and it means what it says: main, as it was last night.
    [ObservableProperty]
    private bool _includeNightlyBuilds;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    // Where the newer build is, or empty — what the Download button opens.
    [ObservableProperty]
    private string _updateUrl = string.Empty;

    // The newer build's name/version, shown as the headline of the persistent update banner (AC-73).
    [ObservableProperty]
    private string _updateName = string.Empty;

    // The startup toast auto-dismisses before the window has focus and is missed; the banner stays until "Open release"
    // or dismiss, and comes back when a build newer than the dismissed one turns up — so the same release never nags
    // while a genuinely newer one still gets through (AC-73).
    [ObservableProperty]
    private bool _updateBannerVisible;

    // Whether a download for "Update now"/"Install on next start" is in flight (AC-388). Drives the banner's/Options'
    // progress indicator directly (AC-379: a rendered-view test asserts the control itself, not this field) and
    // disables both buttons, so a second click cannot start a second transfer over the first.
    [ObservableProperty]
    private bool _isUpdateDownloading;

    // 0-100 progress for the download `IsUpdateDownloading` is tracking (AC-388) (AC-368).
    [ObservableProperty]
    private int _updateDownloadProgress;

    // The version of the release now on offer, and of the one the operator last dismissed from the banner.
    // A version identifies a build on its own: a nightly is packed as `-nightly.&lt;run&gt;`, so the rolling tag
    // it is published under repeats but the version does not.
    private string _offeredRelease = string.Empty;
    private string _dismissedRelease = string.Empty;

    // The channel the operator picked, or null while nobody has (AC-387). Held apart from
    // `IncludeNightlyBuilds` — which shows the channel in force, chosen or derived — so that saving the
    // settings for an unrelated reason cannot turn a derived channel into a choice behind the operator's back.
    private UpdateChannel? _chosenChannel;

    // True while the stored settings are being applied, so filling the controls does not read as using them.
    private bool _loadingUpdateSettings;

    // Which of the two update settings the operator has decided for. Kept apart rather than as one "touched" flag:
    // they are stored together but chosen separately, and one flag for both means changing either one claims the
    // other as well.
    private bool _startupChoiceMade;
    private bool _channelChoiceMade;

    // Two plain flags rather than awaiting the read: both this and the read run on the UI thread, and awaiting the same
    // task from two places says nothing about which of them resumes first — a save that woke up first would still be
    // writing settings it had not learned yet.
    private bool _updateSettingsRead;
    private bool _updateSettingsSavePending;

    // How often the background re-check for a newer build runs while the cockpit is open (AC-188) — the startup look
    // is a single shot, this catches a release cut hours after the window opened.
    private static readonly TimeSpan PeriodicUpdateCheckInterval = TimeSpan.FromHours(1);

    // The hourly update-check timer (AC-188), on the same DispatcherTimer footing as the plugin/managed-CLI check in
    // App; null until StartPeriodicUpdateChecks runs, stopped in DisposeAsync.
    private DispatcherTimer? _periodicUpdateTimer;

    public bool CanCheckForUpdates => _updates is not null;

    // Whether this copy can fetch a newer build over itself (AC-385) — true only for one the updater installed.
    public bool CanUpdateItself { get; }

    public bool HasUpdate => UpdateUrl.Length > 0;

    // Whether "Update now"/"Install on next start" show, in the banner and in Options (AC-388): a build must be on
    // offer, and this copy must be one the updater can replace (AC-379).
    public bool ShowSelfUpdateButtons => CanUpdateItself && HasUpdate;

    // The pre-AC-388 fallback: a build is on offer but this copy cannot fetch it, so the release page is the whole
    // offer.
    public bool ShowOpenReleaseButton => !CanUpdateItself && HasUpdate;

    // Global TTY terminal font family (#40) — one setting for every TTY session, not per-profile or per-session.
    [ObservableProperty]
    private string _terminalFontFamily = "Cascadia Mono, Consolas, monospace";

    // Global TTY terminal font size in points (#40), clamped to
    // `Cockpit.Core.Terminal.TerminalSettings.MinFontSize`-`Cockpit.Core.Terminal.TerminalSettings.MaxFontSize` on
    // save.
    [ObservableProperty]
    private int _terminalFontSize = 13;

    // Selected item in the Options font-family dropdown (#40) — a curated family or `CustomFontChoice`.
    [ObservableProperty]
    private string _terminalFontSelection = "Cascadia Mono, Consolas, monospace";

    // True when the font-family dropdown is on "Custom…" (#40), revealing the free-text box bound to
    // `TerminalCustomFontFamily`.
    [ObservableProperty]
    private bool _isTerminalFontCustom;

    // Free-text font family entered when the dropdown is on "Custom…" (#40); mirrored into `TerminalFontFamily` while
    // custom is active.
    [ObservableProperty]
    private string _terminalCustomFontFamily = string.Empty;

    [ObservableProperty]
    private string _terminalSettingsStatus = string.Empty;

    // The worktree-root override (AC-85); blank uses the default. Bound in Options → Sessions.
    [ObservableProperty]
    private string _worktreeRoot = string.Empty;

    [ObservableProperty]
    private string _worktreeSettingsStatus = string.Empty;

    // The default worktree root, shown as the folder field's placeholder so a blank value clearly means "use the
    // default".
    public string WorktreeRootPlaceholder { get; private set; } = string.Empty;

    // The clones-root override (AC-90); blank uses the default.
    [ObservableProperty]
    private string _cloneRoot = string.Empty;

    [ObservableProperty]
    private string _cloneSettingsStatus = string.Empty;

    // The default clones root, shown as the folder field's placeholder so a blank value clearly means "use the
    // default".
    public string CloneRootPlaceholder { get; private set; } = string.Empty;

    // Sentinel item in the font-family dropdown (#40) that switches to a free-text box for any font not in the curated
    // list.
    public const string CustomFontChoice = "Custom…";

    // Curated monospace font choices offered by the Options dialog's Terminal font-family dropdown; any font not listed
    // is reachable via `CustomFontChoice`.
    public IReadOnlyList<string> TerminalFontFamilies { get; } =
    [
        "Cascadia Mono, Consolas, monospace",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "DejaVu Sans Mono",
        "Courier New",
    ];

    // Items for the Options font-family dropdown (#40): the curated families plus the "Custom…" sentinel.
    public IReadOnlyList<string> TerminalFontChoices => [.. TerminalFontFamilies, CustomFontChoice];

    // ── AC-67: macOS render-backend selector (Auto / Metal / OpenGL / Software) ──────────────────────────────
    private readonly IRenderingSettingsStore? _renderingSettingsStore;

    // The backend the app actually started on (what it is rendering with now), so a save can tell whether
    // the operator's choice differs and a restart is needed. Fixed for the session — only a restart re-reads it.
    private RenderBackendChoice _startupRenderBackend = RenderBackendChoice.Auto;

    // Selected item in the Options render-backend dropdown (AC-67): Auto / Metal / OpenGL / Software.
    [ObservableProperty]
    private string _renderBackendSelection = "Auto";

    // True once a saved render-backend choice differs from what this process started on — reveals "Restart now".
    [ObservableProperty]
    private bool _renderBackendNeedsRestart;

    [ObservableProperty]
    private string _renderingSettingsStatus = string.Empty;

    // The render-backend choices offered by the Options dropdown.
    public IReadOnlyList<string> RenderBackendChoices { get; } = ["Auto", "Metal", "OpenGL", "Software"];

    // True on macOS, where the render backend is a real choice; gates the setting's visibility.
    public bool IsMacOsPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // Whether to show the render-backend setting (AC-67): where it does something — macOS — plus in any dev
    // build, so it can be verified on a Windows/Linux dev machine even though it is inert there for release users.
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

    // Persists the render-backend choice (AC-67). Avalonia fixes the backend once at startup, so a save that
    // changes it from what this process started on flags `RenderBackendNeedsRestart` to offer a restart.
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

    // The default-shell choices for the Options terminal picker (#AC-25): an "OS default" entry first, then every
    // shell `ShellCatalog` detected on this machine. Rebuilt on load so it reflects what is installed.
    public ObservableCollection<TerminalShellChoice> TerminalShellChoices { get; } = [];

    // The chosen default shell a new terminal opens (#AC-25). Its `TerminalShellChoice.Value` is
    // persisted to `Cockpit.Core.Terminal.TerminalSettings.Shell` on save; "OS default" persists blank,
    // "Custom…" persists whatever the operator typed in `TerminalCustomShell`.
    [ObservableProperty]
    private TerminalShellChoice? _selectedTerminalShell;

    // True when the shell picker is on "Custom…" (#AC-25), revealing the free-text box for a third-party shell
    // path/command.
    [ObservableProperty]
    private bool _isTerminalShellCustom;

    // Free-text shell path or command entered when the picker is on "Custom…" (#AC-25) — e.g.
    [ObservableProperty]
    private string _terminalCustomShell = string.Empty;

    // Sentinel `TerminalShellChoice.Value` for the "Custom…" entry that reveals the free-text shell box; any shell not
    // in the detected list is reachable through it.
    public const string CustomShellChoiceValue = "custom";

    // Reveals the custom-shell box when the picker is on "Custom…" (#AC-25), mirroring the font-family "Custom…"
    // pattern.
    partial void OnSelectedTerminalShellChanged(TerminalShellChoice? value) =>
        IsTerminalShellCustom = value is not null && value.Value == CustomShellChoiceValue;

    // Maps the dropdown selection to the effective font family (#40): "Custom…" reveals the free-text box and uses its
    // value, any other choice is used directly.
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

    // While the dropdown is on "Custom…" (#40), keeps the effective font family in sync with the free-text box.
    partial void OnTerminalCustomFontFamilyChanged(string value)
    {
        if (IsTerminalFontCustom && !string.IsNullOrWhiteSpace(value))
        {
            TerminalFontFamily = value;
        }
    }

    // Aligns the dropdown/custom-box state with the effective `TerminalFontFamily` (#40) — used after loading from the
    // store so a saved custom font reopens in the "Custom…" state.
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

    // Pushes the terminal font family to every open TTY session as it changes (#40), so Options → Terminal applies live
    // without a restart.
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

    // Pushes the terminal font size to every open TTY session as it changes (#40), same live-apply as
    // `OnTerminalFontFamilyChanged`.
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

    partial void OnGlobalFocusRailWeightChanged(double value) => _OnEffectiveLayoutChanged();

    partial void OnGlobalFocusRailLayoutChanged(bool value) => _OnEffectiveLayoutChanged();

    // Re-reads what the active desk is doing and pushes it everywhere. One place, because the effective value
    // moves for three different reasons — Options changed, this workspace's override changed, or a different
    // workspace became active — and every one of them has to re-dock the TTY headers (#54) and re-lay the grid.
    internal void _OnEffectiveLayoutChanged()
    {
        OnPropertyChanged(nameof(SingleSessionLayout));
        OnPropertyChanged(nameof(StackSessionsVertically));
        OnPropertyChanged(nameof(FocusRailWeight));
        OnPropertyChanged(nameof(FocusRailLayout));
        OnPropertyChanged(nameof(WorkspaceFollowsGlobalLayout));
        OnPropertyChanged(nameof(WorkspaceSingleSessionLayout));
        OnPropertyChanged(nameof(WorkspaceStackSessionsVertically));
        OnPropertyChanged(nameof(WorkspaceFocusRailWeight));
        OnPropertyChanged(nameof(WorkspaceFocusRailLayout));
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(ShowFocusRail));
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

    // True when only the selected session should be shown full-size — either the persisted single layout (#24) or a
    // transient Zoom.
    public bool ShowSinglePane => SingleSessionLayout || IsZoomed;

    // True when the grid should draw as one big focus pane plus a miniature rail (AC-441/444) rather than
    // the adaptive grid. `ShowSinglePane` still wins when both are somehow on (Zoom, or a config predating
    // AC-445's mutual exclusivity) — with one visible pane there is nothing left to put in a rail.
    public bool ShowFocusRail => FocusRailLayout && !ShowSinglePane;

    partial void OnIsZoomedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSinglePane));
        OnPropertyChanged(nameof(StackSessionsInStack));
        RefreshPaneVisibility();
    }


    [ObservableProperty]
    private string _audioStatus = "Ready.";

    // Whether a local OS toast is shown when a session needs attention while you are present (independent of Discord).
    [ObservableProperty]
    private bool _localNotificationsEnabled = true;

    // Whether the Discord webhook is POSTed when a session needs attention while you are away (independent of local
    // toasts).
    [ObservableProperty]
    private bool _discordNotificationsEnabled;

    // Discord webhook URL POSTed to when the operator is away. Empty disables the away channel.
    [ObservableProperty]
    private string _webhookUrl = string.Empty;

    // Idle minutes before the operator counts as "away" (when the PC is not locked).
    [ObservableProperty]
    private int _idleThresholdMinutes = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    // Minutes a finished session stays "done" before it falls back to idle.
    [ObservableProperty]
    private int _sessionIdleMinutes = (int)SessionIdleDecision.DefaultIdleThreshold.TotalMinutes;

    // Whether a session that finished its turn announces itself when the operator is not watching it.
    [ObservableProperty]
    private bool _notifyOnSessionFinished = true;

    // Whether a session announces that it has gone idle.
    [ObservableProperty]
    private bool _notifyOnSessionIdle;

    // Whether one message is sent when the last session goes idle — nothing is running any more.
    [ObservableProperty]
    private bool _notifyWhenAllSessionsIdle;

    // AC-634: whether the branch a session is on is watched for a failing CI check.
    [ObservableProperty]
    private bool _notifyOnCiFailure = true;

    // Whether the cockpit window is the focused one. Set by the window itself (it is the only thing that knows),
    // and read by the finished-session notification: a session you are looking at does not need to announce itself.
    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private string _notificationSettingsStatus = string.Empty;

    // Whether the Options dialog is holding edits the operator has not applied (AC-999). Replaces the "Saved"
    // indicator this used to be: under a staged model that word was a lie the moment it appeared, since nothing
    // is written until Apply and the dialog closes on the same click.
    [ObservableProperty]
    private bool _hasPendingOptionChanges;

    [ObservableProperty]
    private string _shortcutSettingsStatus = string.Empty;

    // When true, every transcript row shows its arrival timestamp (T7). Applied to all open sessions.
    [ObservableProperty]
    private bool _showTimestamps;

    [ObservableProperty]
    private string _transcriptDisplaySettingsStatus = string.Empty;

    // Which metrics the header's usage pill shows (AC-105), as three toggles composed into the saved field list.
    [ObservableProperty]
    private bool _showUsagePillContext = true;

    [ObservableProperty]
    private bool _showUsagePillSessionUsage;

    // #1105 A2: one toggle for every rolling allowance window a provider reports, replacing the earlier pair of
    // Claude-specific five-hour/weekly toggles that left a differently-shaped window (Codex's 7d) undrawable.
    [ObservableProperty]
    private bool _showUsagePillRateWindows;

    [ObservableProperty]
    private string _usagePillSettingsStatus = string.Empty;

    // When true, sending "exit" closes the session after its turn completes (T10). Applied to all open sessions.
    [ObservableProperty]
    private bool _autoCloseOnExit;

    // When true, messages queued mid-turn are sent together as one follow-up turn instead of one-per-turn (AC-145).
    [ObservableProperty]
    private bool _combineQueuedMessages;

    // On by default, and the operator's decision rather than each agent's: this is the consent for that turn, and the
    // session it is spent on is not the one paying for it (AC-615).
    [ObservableProperty]
    private bool _wakeAgentsByDefault = true;

    // AC-1086: the shared budget over all sessions together, as a share of the machine. Each session had a cap of
    // its own and nothing ever added them up, so several well-behaved sessions could still promise more than exists.
    [ObservableProperty]
    private int _memoryBudgetPercent = MemoryPressure.DefaultBudgetPercent;

    [ObservableProperty]
    private string _sessionBehaviorSettingsStatus = string.Empty;

    // Master switch for voice input (push-to-talk dictation).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCalibration))]
    private bool _voiceEnabled;

    private readonly ITranscriptionAdvisor? _transcriptionAdvisor;

    // Effective ggml model name fed to the speech-to-text service, e.g. "large-v3-turbo", "small", "tiny".
    // Driven by the Options dropdown (`SelectedTranscriptionModel`): a curated model sets it directly,
    // the "Custom…" choice mirrors `VoiceCustomModelName`. Smaller models download and transcribe faster.
    [ObservableProperty]
    private string _voiceModelName = "large-v3-turbo";

    // Sentinel item in the transcription-model dropdown (AC-68) that reveals a free-text box for any ggml
    // name not in the curated list — quantized variants like `large-v3-turbo-q5_0`, or a model added later.
    public const string CustomModelChoice = "Custom…";

    // Curated Whisper models offered by the Options → Voice → Transcribe dropdown (AC-68), each with a short
    // accuracy-vs-load hint. Prefixed at runtime with an "Auto ★" recommendation and suffixed with `CustomModelChoice`.
    private static readonly IReadOnlyList<TranscriptionModelOption> _curatedModels =
    [
        new("large-v3-turbo", "most accurate · heaviest"),
        new("large-v3-turbo-q5_0", "turbo accuracy · quantized, lighter"),
        new("medium", "≈1pt less accurate on NL · lighter"),
        new("small", "fast · light"),
        new("base", "faster · less accurate"),
        new("tiny", "fastest · least accurate"),
        new(CustomModelChoice, "enter any ggml name", IsCustom: true),
    ];

    // Items for the model dropdown (AC-68): an "Auto ★" recommendation (when an advisor is present), then the
    // curated models, then "Custom…". Built once at construction — the recommendation is fixed for the session.
    public ObservableCollection<TranscriptionModelOption> TranscriptionModelChoices { get; } = new();

    // The per-machine recommendation (AC-68 slice 2); null in the design-time/test graph with no advisor.
    private TranscriptionRecommendation? _transcriptionRecommendation;

    // Whether the model dropdown is on the "Auto ★" item — persisted as
    // `Cockpit.Core.Voice.VoiceSettings.ModelAutoSelected`.
    private bool _transcriptionModelAuto;

    // Selected item in the transcription-model dropdown (AC-68) — the "Auto ★" recommendation, a curated
    // model, or the "Custom…" sentinel. Drives `VoiceModelName` and toggles `IsTranscriptionModelCustom`.
    [ObservableProperty]
    private TranscriptionModelOption? _selectedTranscriptionModel;

    // True when the model dropdown is on "Custom…" (AC-68), revealing the free-text box bound to
    // `VoiceCustomModelName`.
    [ObservableProperty]
    private bool _isTranscriptionModelCustom;

    // Free-text ggml model entered when the dropdown is on "Custom…" (AC-68); mirrored into `VoiceModelName` while
    // custom is active.
    [ObservableProperty]
    private string _voiceCustomModelName = string.Empty;

    // Host-aware Whisper backend choices offered by the Options → Voice → Transcribe combo box (AC-68).
    // Built from `ITranscriptionAdvisor`: always Auto and CPU, plus a single GPU option only when a GPU
    // runtime actually loads here — so a non-NVIDIA machine is never offered CUDA.
    public ObservableCollection<VoiceBackendPreferenceOption> VoiceBackendPreferences { get; } = new();

    [ObservableProperty]
    private VoiceBackendPreferenceOption _selectedVoiceBackendPreference = new("Auto (recommended)", VoiceBackendPreference.Auto);

    // One-line explanation of what the chosen transcription backend does on this machine (AC-68); recomputed
    // when the selection changes. Slice 2 makes the Auto recommendation hardware-aware and richer.
    [ObservableProperty]
    private string _transcriptionAdvice = string.Empty;

    // A short badge line describing the detected transcription hardware (AC-68), e.g. "Vulkan GPU available"
    // or "No GPU acceleration detected — CPU only". Slice 2 adds GPU brand and display-adapter facts.
    [ObservableProperty]
    private string _transcriptionHardware = string.Empty;

    // Builds the host-aware backend list and the initial model/advice state (AC-68). Called from both
    // constructors; without an advisor (design-time/tests) it offers Auto + CPU only.
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

    // Recomputes the one-line advice (AC-68). For "Auto" the recommendation's reason is the richest
    // explanation (why CPU on a single GPU that draws the screen); an explicit CPU/GPU choice gets the generic note.
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

    // Maps the dropdown selection to the effective model (AC-68): "Custom…" reveals the free-text box and
    // uses its value, any curated model is used directly.
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

    // While the model dropdown is on "Custom…" (AC-68), keeps the effective model in sync with the box.
    partial void OnVoiceCustomModelNameChanged(string value)
    {
        if (IsTranscriptionModelCustom && !string.IsNullOrWhiteSpace(value))
        {
            VoiceModelName = value.Trim();
        }
    }

    // Aligns the model dropdown/custom-box with the effective `VoiceModelName` (AC-68) — used
    // after loading so a saved custom model reopens in the "Custom…" state, and a preset reopens selected.
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

    // True while a calibration runs — shows the overlay and disables Run (AC-68).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCalibration))]
    private bool _isCalibrating;

    // The current step's text ("CPU: measuring… (2/3)", a result note, or an error) (AC-68).
    [ObservableProperty]
    private string _calibrationStatus = string.Empty;

    // 0..100 for the overlay bar while a step reports a real fraction (a model download); else indeterminate.
    [ObservableProperty]
    private double _calibrationProgressValue;

    // True when the current step has no honest percentage (loading, warming up, measuring) — the bar spins.
    [ObservableProperty]
    private bool _calibrationProgressIndeterminate = true;

    // Whether measured results exist to show the comparison bars and verdict (AC-68).
    [ObservableProperty]
    private bool _hasCalibration;

    // One row per measured backend (CPU, GPU), fastest first — the comparison bars.
    public ObservableCollection<CalibrationResultRow> CalibrationResults { get; } = [];

    // Full-scale (ms) for the speed bars: the slowest backend, so the bars read relative to each other.
    [ObservableProperty]
    private double _calibrationSpeedMaxMs = 1;

    // Full-scale (ms) for the hitch bars, floored so a smooth result still shows a short bar.
    [ObservableProperty]
    private double _calibrationHitchMaxMs = 32;

    // Which backend Auto runs on, in words ("Auto runs on GPU (Vulkan)") — so the resolved choice is visible (AC-68).
    [ObservableProperty]
    private string _calibrationChosenText = string.Empty;

    // The model the backend comparison was timed with, so those numbers are read against a known model (AC-68).
    [ObservableProperty]
    private string _calibrationModelText = string.Empty;

    // Per-model measured rows on the chosen backend (AC-68) — the accuracy-vs-speed table.
    public ObservableCollection<CalibrationModelRow> CalibrationModelResults { get; } = [];

    // Full-scale (ms) for the model bars: the slowest measured model.
    [ObservableProperty]
    private double _calibrationModelMaxMs = 1;

    // The model the verdict suggests, in words ("Suggested model: small") (AC-68).
    [ObservableProperty]
    private string _calibrationModelRecommendation = string.Empty;

    // Why that model is suggested (AC-68).
    [ObservableProperty]
    private string _calibrationModelAdvice = string.Empty;

    // Whether a measured model ladder exists to show its table (AC-68).
    [ObservableProperty]
    private bool _hasModelLadder;

    // The verdict's one-line reasoning (AC-68).
    [ObservableProperty]
    private string _calibrationRationale = string.Empty;

    // Calibration needs the model, so it can run only when voice is on and a calibrator is present (AC-68).
    public bool CanRunCalibration => _transcriptionCalibrator is not null && VoiceEnabled && !IsCalibrating;

    // Whether the "Run calibration" affordance is offered at all — only in a graph that has a calibrator.
    public bool ShowCalibration => _transcriptionCalibrator is not null;

    // Measures every backend this machine can use — the CPU and, if a GPU runtime loads, the GPU — each in its own
    // child process, then picks one with a CPU preference and remembers it (AC-68). A failed measurement is reported
    // on the status line, never thrown into the dialog.
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

    // Cancels a running calibration — the blocking overlay's escape hatch, so a wedged child (a stalled
    // download, a native load that hangs) can never trap the operator behind it (AC-68).
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

    // Selectable dictation languages for speech-to-text — "Auto-detect" plus common fixed languages.
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

    // Input (microphone) devices offered by the Options combo box; the first entry is the system default.
    public ObservableCollection<AudioDeviceOption> InputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedInputDevice = new("System default", null);

    // Output (playback) devices for read-aloud (#35); the first entry is the system default.
    public ObservableCollection<AudioDeviceOption> OutputDevices { get; } = new() { new("System default", null) };

    [ObservableProperty]
    private AudioDeviceOption _selectedOutputDevice = new("System default", null);

    // Avalonia `Key` enum name for the push-to-talk hotkey, e.g. "F9".
    [ObservableProperty]
    private string _voicePushToTalkKeyName = "F9";

    // When true, the push-to-talk hotkey also fires while the cockpit window has no focus (#34), via
    // `VoicePushToTalkCoordinator`. Off by default — opt-in like voice itself.
    [ObservableProperty]
    private bool _voiceGlobalPushToTalk;

    // Shown next to global push-to-talk on Linux once the operator has saved a change to it (#34): there the hotkey is
    // a desktop-portal binding the compositor only picks up at startup, so unlike on Windows — where
    // `VoicePushToTalkCoordinator` re-arms it live — the change takes effect only after a restart.
    [ObservableProperty]
    private bool _voiceGlobalPushToTalkNeedsRestart;

    // The global push-to-talk value this process actually armed with at startup — the baseline the save
    // compares against, so toggling it and back leaves nothing to restart for. Null until first loaded.
    private bool? _voiceGlobalPushToTalkRunning;

    // When true a finished transcript is submitted straight after injection instead of waiting for a manual send.
    [ObservableProperty]
    private bool _voiceAutoSubmit;

    // What the global hotkey is really triggered by, in the words of whoever bound it — or why nothing is. Read
    // back rather than assumed: under Wayland the compositor owns the binding and the key above is a hint it may
    // ignore, and on macOS there is no implementation at all. Empty while global push-to-talk is off.
    [ObservableProperty]
    private string _voiceGlobalHotkeyTrigger = string.Empty;

    // Mirrors `Cockpit.Core.Screenshots.ScreenshotSettings.GlobalHotkeyEnabled` (AC-220): whether the
    // screenshot key fires while the cockpit has no focus. Off by default — a desktop-wide key is taken from
    // every other application, so it is the operator's to grant. The composer button works either way.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyConflict))]
    private bool _screenshotGlobalHotkeyEnabled;

    // Mirrors `Cockpit.Core.Screenshots.ScreenshotSettings.HotkeyKeyName` — the Avalonia `Key` name for the screenshot
    // hotkey, e.g.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyConflict))]
    private string _screenshotHotkeyKeyName = "F8";

    // What the screenshot hotkey is really triggered by, in the words of whoever bound it.
    [ObservableProperty]
    private string _screenshotHotkeyTrigger = string.Empty;

    // Mirrors `Cockpit.Core.Screenshots.ScreenshotSettings.PreviewEnabled` (AC-566): whether
    // confirming a selection opens a preview of the exact image before it is sent, instead of injecting it
    // straight away. Off by default — not everyone wants the extra window.
    [ObservableProperty]
    private bool _screenshotPreviewEnabled;

    // Names two desktop-wide keys that want the same key, or empty when there is no clash (AC-220). Shown live
    // while the operator is typing a key rather than after saving, since after saving one of the two features
    // has already silently stopped working — which is the whole failure this exists to prevent.
    public string HotkeyConflict =>
        GlobalHotkeyConflictCheck.Describe(_ConfiguredGlobalHotkeys()) ?? string.Empty;

    // The bindings as the settings screen currently reads — what would be armed if it were saved now.
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
        ScreenshotPreviewEnabled = settings.PreviewEnabled;
    }

    // Persists the screenshot settings edited in Options (AC-220).
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
            PreviewEnabled = ScreenshotPreviewEnabled,
        });
    }

    // Mirrors `Cockpit.Core.Voice.VoiceSettings.StopReadAloudWhenSpeaking` (AC-9).
    [ObservableProperty]
    private bool _voiceStopReadAloudWhenSpeaking;

    // Decimal because that is what NumericUpDown binds.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceStopReadAloudThresholdValue))]
    private decimal _voiceStopReadAloudLevelThreshold = 0.15m;

    // The barge-in threshold as a 0..1 double, for the `MicLevelMeter` marker (the setting itself is a decimal so
    // NumericUpDown can bind it).
    public double VoiceStopReadAloudThresholdValue => (double)VoiceStopReadAloudLevelThreshold;

    // Two-way bound to the "Test microphone" toggle; flipping it starts/stops a live level meter for setting the
    // barge-in threshold by eye (AC-9).
    [ObservableProperty]
    private bool _isTestingMic;

    // AC-1000: the Options sidebar's search box. Dialog-local UI state, not a persisted setting — never staged,
    // so Cancel does not need to (and must not) restore it.
    [ObservableProperty]
    private string _optionsSearchText = string.Empty;

    // Live microphone level (0..1 RMS) during the mic test, driving the `MicLevelMeter` fill.
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

    // Stops the mic test and releases the microphone. Called from the dialog's close handler so it never stays open.
    public void StopMicTest()
    {
        if (IsTestingMic)
        {
            IsTestingMic = false;
        }
    }

    // Mirrors `Cockpit.Core.Voice.VoiceSettings.OpenMicSilenceTimeoutMs`: trailing silence (ms) that ends an open-mic
    // utterance.
    [ObservableProperty]
    private int _voiceOpenMicSilenceTimeoutMs = 800;

    // The open-mic coordinator, wired at startup, exposing the runtime on/off toggle bound to the sidebar mic button
    // (open-mic is turned on/off live, not via a settings checkbox).
    [ObservableProperty]
    private OpenMicCoordinator? _openMic;

    // The screenshot coordinator, wired at startup (AC-220). Held so every session panel can be handed the
    // capture its composer button runs, and so a platform that cannot capture at all is said once rather than
    // discovered per button.
    [ObservableProperty]
    private ScreenshotCoordinator? _screenshots;

    partial void OnScreenshotsChanged(ScreenshotCoordinator? value)
    {
        _WireScreenshotsEverywhere();

        if (value is { } screenshots)
        {
            _ = _RewireScreenshotsWhenSupportSettlesAsync(screenshots);
        }
    }

    // Wires every session again once the platform has finished saying whether it can capture (AC-326).
    private async Task _RewireScreenshotsWhenSupportSettlesAsync(ScreenshotCoordinator screenshots)
    {
        await screenshots.SupportSettled.ConfigureAwait(true);

        _WireScreenshotsEverywhere();
    }

    // Every session there is — the assistant included, which sits in neither collection and would otherwise keep a
    // greyed-out button for the rest of the run (AC-630).
    private void _WireScreenshotsEverywhere()
    {
        foreach (var session in Sessions)
        {
            _WireScreenshots(session);
        }

        if (_assistantSession is { } assistant)
        {
            _WireScreenshots(assistant);
        }
    }

    // Hands a session panel the capture behind its composer button — and, where the platform has none, the sentence
    // that says so.
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

    // Selectable read-aloud voices (#35) offered by the Options flyout combo box — SupertonicTTS speaker choices.
    public IReadOnlyList<TtsVoiceOption> TtsVoices => TtsVoiceCatalog.Voices;

    // AC-546 removed the old "Test read-aloud" button along with the read-aloud modes it previewed, and that went one
    // step too far: what the button rendered (Verbatim / Naturalized / Summarized) is gone, but "let me hear this voice
    // first" is a different question and the catalogue now offers ten speakers instead of two.
    [RelayCommand]
    private void PreviewVoice()
    {
        if (_voicePlaybackQueue is null)
        {
            return;
        }

        _voicePlaybackQueue.StopAll();
        _voicePlaybackQueue.Enqueue(
            [_VoiceSampleSentence(SelectedReadAloudLanguage.Code)],
            SelectedTtsVoice.Sid,
            SelectedReadAloudLanguage.Code);
    }

    // Spoken in whichever language is selected: hearing the voice read the language you will actually use it in is
    // the point, and a Dutch sentence in an English preview says nothing about how Dutch will sound.
    private static string _VoiceSampleSentence(string languageCode) =>
        string.Equals(languageCode, "nl", StringComparison.OrdinalIgnoreCase)
            ? "Dit is de stem waarmee de assistent je antwoord voorleest."
            : "This is the voice the assistant reads your answer in.";

    // SupertonicTTS speaker used for read-aloud (#35).
    [ObservableProperty]
    private TtsVoiceOption _selectedTtsVoice = TtsVoiceCatalog.Default;

    // Preferred read-aloud base language (#35): the voice leans to it and unmarked text speaks in it, keeping foreign
    // terms in their language.
    public IReadOnlyList<SttLanguageOption> ReadAloudLanguages { get; } =
    [
        new("English", "en"),
        new("Dutch", "nl"),
    ];

    [ObservableProperty]
    private SttLanguageOption _selectedReadAloudLanguage = new("English", "en");

    // Mirrors `Cockpit.Core.Voice.VoiceSettings.TtsSpeed` (AC-708). Decimal because that is what NumericUpDown
    // binds; clamped to 0.5–2.0 where it is actually used (`SherpaOnnxTextToSpeechService`), not here.
    [ObservableProperty]
    private decimal _voiceTtsSpeed = 1.0m;

    [ObservableProperty]
    private string _voiceSettingsStatus = string.Empty;

    // True on Linux, where the physical key for global push-to-talk is bound by the desktop's own
    // Shortcuts settings rather than configurable in-app (#34) — drives the Options-flyout hint text.
    public bool IsLinuxPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // Gates the Windows-only "New terminal (administrator)" action (AC-967).
    public bool IsWindowsPlatform { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // AC-691: the portal re-request button only makes sense where a portal is what's arming the hotkey.
    // X11 uses the same keyboard hook Windows does — nothing there to lose permission for — so the button
    // is Wayland-only, not Linux-wide like the hint text above it.
    public bool IsLinuxWayland { get; } = ShouldShowHotkeyPortalRetry(
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
        LinuxSession.IsWayland(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    // Pulled out so the platform gate is testable off a Wayland session — the same reasoning as
    // ShouldOfferGlobalPushToTalkRestart below.
    internal static bool ShouldShowHotkeyPortalRetry(bool isLinux, bool isWayland) => isLinux && isWayland;

    // Pushes the timestamp toggle to every open session as it changes, so the switch takes effect live.
    partial void OnShowTimestampsChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.ShowTimestamps = value;
        }
    }

    partial void OnShowUsagePillContextChanged(bool value) => ApplyUsagePillFields();

    partial void OnShowUsagePillSessionUsageChanged(bool value) => ApplyUsagePillFields();

    partial void OnShowUsagePillRateWindowsChanged(bool value) => ApplyUsagePillFields();

    // The chosen usage-pill fields in display order, composed from the three toggles.
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

        if (ShowUsagePillRateWindows)
        {
            fields.Add(UsagePillField.RateWindows);
        }

        return fields;
    }

    // Pushes the usage-pill field selection to every open session as a toggle changes, so it takes effect live.
    private void ApplyUsagePillFields()
    {
        var fields = ComposeUsagePillFields();
        foreach (var session in Sessions)
        {
            session.UsagePillVisibleFields = fields;
        }
    }

    // Pushes the auto-close-on-exit toggle to every open session as it changes.
    partial void OnAutoCloseOnExitChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.AutoCloseOnExit = value;
        }
    }

    // Pushes the combine-queued-messages toggle to every open SDK/chat session as it changes (AC-145); TTY sessions
    // have no send queue.
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

    // Pushes the operator's wake decision to the coordinator as it changes (AC-615). Live rather than read at
    // session start: turning wakes off has to reach the sessions that are already open, which is the case the
    // operator is most likely to be reaching for the toggle in.
    partial void OnWakeAgentsByDefaultChanged(bool value) => _agentCoordinator?.SetDefaultWakeConsent(value);

    // Keeps each session's `SessionViewModel.IsSelected` in sync with the active selection.
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

    // Sets each session's `SessionPanelViewModel.IsPaneVisible` for the current layout: all visible in the
    // multi-session grid, only the selected one in single-pane mode (#24 / Zoom).
    private void RefreshPaneVisibility()
    {
        var single = ShowSinglePane;
        foreach (var session in Sessions)
        {
            var here = BelongsToActiveWorkspace(session);
            session.IsOnActiveDesk = here;
            session.IsPaneVisible = here && (!single || session.IsSelected);
        }
    }

    // Two Sessions workspaces are separate desks, so each shows only its own — but the sessions of the others keep
    // running: they are hidden, never removed from `Sessions`.
    private bool BelongsToActiveWorkspace(SessionPanelViewModel session)
    {
        // Asked before the no-active-workspace shortcut below, which answers "true" — the assistant has no desk
        // at all (AC-543) and must not be drawn as a pane on any of them, least of all on a graph that has not
        // finished deciding which workspace is showing.
        if (session.BelongsToNoWorkspace)
        {
            return false;
        }

        if (Workspaces.Active is not { } active)
        {
            return true;
        }

        // A dashboard shows no sessions at all; and a session with no workspace — created before workspaces existed, or
        // in the design-time graph — belongs to the first desk that can actually show one.
        return active.Type == WorkspaceType.Sessions
            && SessionWorkspacePlacement.Resolve(
                session,
                SessionWorkspacePlacement.FirstSessionsWorkspaceId(Workspaces.Settings)) == active.Id;
    }

    // The sessions on the workspace now showing, in the sidebar's own order — what the strip lists, so it never offers
    // a session the grid is hiding.
    public IEnumerable<SessionPanelViewModel> VisibleSessions
    {
        get
        {
            _SyncVisibleSessions();
            return _visibleSessions;
        }
    }

    // Brings `_sidebarOrder` back in line with `Sessions`: drops sessions that have closed and appends any that
    // appeared, keeping the operator's chosen order for everything already tracked.
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

    // Diffs against the target order rather than clearing and re-adding: a session already at the right slot is left
    // untouched, one that only moved gets a single in-place `ObservableCollection{T}.Move` (which Avalonia's
    // ItemsControl honours by relocating the existing row container, not discarding it), and only a session that
    private bool _syncing;

    private void _SyncVisibleSessions()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            _SyncVisibleSessionsCore();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void _SyncVisibleSessionsCore()
    {
        _ReconcileSidebarOrder();
        var target = _sidebarOrder.Where(BelongsToActiveWorkspace).ToList();

        for (var i = _visibleSessions.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(_visibleSessions[i]))
            {
                _visibleSessions.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var session = target[i];
            if (i < _visibleSessions.Count && ReferenceEquals(_visibleSessions[i], session))
            {
                continue;
            }

            var existingIndex = _visibleSessions.IndexOf(session);
            if (existingIndex >= 0)
            {
                _visibleSessions.Move(existingIndex, i);
            }
            else
            {
                _visibleSessions.Insert(i, session);
            }
        }

        // Stamped on every reconcile, not just on change: the focus-rail's ordering (AC-444 #2) reads this
        // straight off the pane, so it has to track "the sidebar's own order" exactly, including a plain
        // renumber when nothing moved but the set behind it did.
        for (var i = 0; i < target.Count; i++)
        {
            target[i].SidebarIndex = i;
        }
    }

    // Called from both constructors, right after `Workspaces` is built — the design-time/test graph needs this exactly
    // as much as the real one, and wiring it in only one of them is how the two quietly drift apart.
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

            // A desk can arrange itself differently from the last one, so switching re-reads the effective layout
            // and re-docks the TTY headers — the same work Options changing does, for the same reason.
            // It ends in RefreshPaneVisibility, which is also what keeps the other desks' sessions alive but unshown.
            _OnEffectiveLayoutChanged();
        };

    // Parameterless constructor kept for the Avalonia previewer/Screenshotter design-time context — seeds three sample
    // sessions across different providers and statuses so the render shows the overview + grid without a real DI-backed
    // session behind each one (AC-953).
    public CockpitViewModel(IDockPanelRegistry? dockPanelRegistry = null)
    {
        // AC-951: without a registry the dock rail's tab strip renders empty in the previewer/screenshotter — the
        // same reason the shortcut rows above are seeded by hand here. A caller passes one when the scene needs a
        // panel that actually opens (AC-953's docked assistant).
        _dockPanelRegistry = dockPanelRegistry ?? new DockPanelRegistry();
        _WireDockPanelChanges();

        // First: selecting a session below raises pane-visibility, which asks which workspace is active.
        Workspaces = new WorkspacesViewModel();
        _WireWorkspaceVisibility();

        var waiting = new SessionViewModel { Title = "Session 1", ActiveProfileLabel = "work (Claude)", SessionStatus = SessionStatus.NeedsAttention };
        var busy = new SessionViewModel { Title = "Session 2", ActiveProfileLabel = "local (Ollama)", SessionStatus = SessionStatus.Busy };
        var tty = new TtyViewModel { Title = "Session 3", ActiveProfileLabel = "personal (Claude TTY)", SessionStatus = SessionStatus.Busy };

        _AttachSession(waiting);
        _AttachSession(busy);
        _AttachSession(tty);
        _sessionCounter = Sessions.Count;
        SelectedSession = waiting;
        Plugins = new PluginManagerViewModel();
        DelegatedTasks = new DelegatedTasksViewModel();
        Worktrees = new WorktreesViewModel();
        Projects = new ProjectsViewModel();
        Security = new SecurityOptionsViewModel(new UnprotectedSecrets());
        AssistantOptions = new AssistantOptionsViewModel();
        Diagnostics = new DiagnosticsViewModel(null, _BuildSessionDescriptors);

        // Seed the Options → Shortcuts rows from the catalog defaults; without a settings store the DI path
        // that normally builds them never runs, and the tab would render empty in the previewer/screenshotter.
        _RebuildShortcutRows();

        // No advisor in the design-time/previewer graph: the Transcribe page then offers Auto + CPU only.
        _InitVoiceTranscriptionOptions();

    }

    // The Security tab: encrypting the credentials in cockpit.json at rest, and the migration either way.
    public SecurityOptionsViewModel Security { get; }

    // The Options → Voice "Assistant" block (AC-543): the master switch, the Assistant Profile slot, the hotkey, and
    // read-replies-aloud.
    public AssistantOptionsViewModel AssistantOptions { get; }

    // The assistant chip at the bottom of the sidebar (AC-543).
    [ObservableProperty]
    private AssistantIndicatorViewModel? _assistantIndicator;

    // A save wrote a credential in the clear (AC-41). Re-read the banner state on the UI thread — the event comes
    // off whatever thread the save ran on, and the Security VM's properties feed a binding.
    private void OnUnprotectedSecretsWritten(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = Security.RefreshAsync());

    // Turns encryption on from the awareness banner (AC-41) and says how it went with a toast.
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

    // The Debug tab's diagnostics panel (AC-58): render backend, memory, GC, platform and crash logs, as copyable text.
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
        // AC-510[b]: DI's own singleton, handed straight to PluginManagerViewModel below so the plugin store
        // dialog and the first-run wizard's provider step share exactly one install path.
        IPluginProvisioningService? pluginProvisioningService = null,
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
        DiagnosticsBackgroundService? diagnosticsBackgroundService = null,
        IBackupService? backupService = null,
        IAssistantMemory? assistantMemory = null,
        IUpdateService? updateService = null,
        IUpdateSettingsStore? updateSettingsStore = null,
        IUpdateSupportProbe? updateSupportProbe = null,
        IWorkflowTemplateLibrary? workflowTemplateLibrary = null,
        ISecretProtectionService? secretProtection = null,
        IWorkspaceSettingsStore? workspaceSettingsStore = null,
        IWidgetRegistry? widgetRegistry = null,
        IDockPanelRegistry? dockPanelRegistry = null,
        IConsentBroker? consentBroker = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ITranscriptionAdvisor? transcriptionAdvisor = null,
        ITranscriptionCalibrator? transcriptionCalibrator = null,
        ITranscriptionCalibrationStore? transcriptionCalibrationStore = null,
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
        IShellAccessSwitch? shellAccessSwitch = null,
        IShellAccessSettingsStore? shellAccessSettingsStore = null,
        ITerminalAccessRegistry? terminals = null,
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        ISessionProfileStore? sessionProfileStore = null,
        // AC-794: what the Security tab's node-scope checklist offers to tick, alongside sessionProfileStore above.
        // `Projects` (this same constructor's own ProjectsViewModel) is not reused for this — it is built after
        // Security below, and its own store is private, so Security gets the raw store directly instead.
        IProjectStore? projectStore = null,
        IAssistantSettingsStore? assistantSettingsStore = null,
        IAssistantProfileStore? assistantProfileStore = null,
        IWorkspaceTypeRegistry? workspaceTypeRegistry = null,
        ProjectQuickStart? projectQuickStart = null,
        IScreenshotSettingsStore? screenshotSettingsStore = null,
        ISessionResourceResolver? sessionResourceResolver = null,
        IWorkspaceAgentCoordinator? agentCoordinator = null,
        IAgentMessageInbox? agentMessages = null,
        IAgentResourceClaims? agentClaims = null,
        IAgentLineBudget? agentLineBudget = null,
        IAgentNotifyAuditLog? agentNotifyTrail = null,
        IClaimCollisionMonitor? claimCollisionMonitor = null,
        SessionStateRecorder? sessionStateRecorder = null,
        ISessionStateStore? sessionStateStore = null,
        SessionRestorePlanner? sessionRestorePlanner = null,
        IWorktreeReconcileGate? worktreeReconcileGate = null,
        PluginManager? pluginManager = null,
        ILogger<CockpitViewModel>? logger = null,
        // AC-545: only so a spawn the assistant asked for starts on the route the profile is set to, the way the
        // New-session dialog would have (SessionKindDefaults). Optional like every neighbour here — absent, the
        // resolver's own rule falls back to SDK, which is what a graph with no TTY providers can start anyway.
        ITtySessionProviderResolver? ttyProviderResolver = null,
        // AC-575: only so the assistant's consent-bypass list in Options can be filled from sources the host has
        // actually stamped, rather than from free text the operator types in. Read-only here — nothing on this
        // view model writes the trail.
        IConsentAuditLog? consentAuditLog = null,
        // AC-512: the first-run wizard's own strand injects the real implementation; null here (design-time/tests,
        // or a host build that has not registered one yet) leaves "Run setup again" a no-op rather than a crash.
        IFirstRunWizard? firstRunWizard = null,
        // AC-1033: the knowledge base behind Help ▸ Documentation. Null in the design-time and unit-test graph,
        // the same as the wizard above, which leaves that menu entry a no-op rather than a crash.
        HelpService? help = null,
        Func<string, bool>? tryOpenExternalLink = null,
        // AC-790: the network-node master switch and its shared secret, and the mounted-endpoint hosts whose live
        // off-loopback addresses the Security tab reads to show the operator what to type into a second Cockpit.
        INodeEndpointSettingsStore? nodeEndpointSettingsStore = null,
        IEnumerable<ICockpitInternalMcpProvider>? internalMcpProviders = null,
        // Absent in the design-time/unit-test graph, where the pairing controls stay inert rather than the dialog
        // failing (AC-792).
        INodePairingBroker? nodePairingBroker = null,
        INodePairingClient? nodePairingClient = null,
        INodePairingEndpoint? nodePairingEndpoint = null,
        IMcpServerStore? mcpServerStore = null,
        // AC-793: the second entrance to the same handshake — finding a node on the network instead of typing
        // its address. Absent in the design-time/unit-test graph the same way the pairing pair above is; the
        // Security tab's Discover button then does nothing rather than the dialog failing to open.
        INodeDiscoveryClient? nodeDiscoveryClient = null,
        // AC-795: the controller's reach into a paired node's sessions. Absent in the design-time/unit-test graph
        // like the pairing halves above, and the node cards on the Security tab then do not appear at all.
        INodeSessionsClient? nodeSessionsClient = null,
        // AC-927: where the launch routes say which MCP servers a session really got, so its header can name
        // those. Absent in the design-time/unit-test graph, where the header keeps showing the selection alone.
        SessionMcpMounts? sessionMcpMounts = null)
    {
        // Without a store this is the default single Sessions workspace and nothing persists — which is exactly what
        // the unit-test and design-time graphs want, and is why the tab strip stays hidden there.
        Workspaces = new WorkspacesViewModel(workspaceSettingsStore, widgetRegistry, ToastHost, workspaceTypeRegistry);
        _WireWorkspaceVisibility();

        // AC-951: the dock rail's tab strip reads `DockPanels` off the registry directly; the panel registered here is
        // host-internal, so — unlike the widget/workspace-type registries above — there is no late-arriving plugin to
        // wait for (AC-950).
        _dockPanelRegistry = dockPanelRegistry;
        _WireDockPanelChanges();

        // The Security tab (encrypting the credentials at rest). Absent in the design-time/unit-test graph, and
        // the tab simply reports "not encrypted" then rather than the dialog failing to open at all.
        Security = new SecurityOptionsViewModel(
            secretProtection ?? new UnprotectedSecrets(),
            screenLockSettingsStore,
            terminalAccessSwitch,
            terminalAccessSettingsStore,
            shellAccessSwitch,
            shellAccessSettingsStore,
            nodeEndpointSettingsStore,
            internalMcpProviders,
            nodePairingBroker,
            nodePairingClient,
            nodePairingEndpoint,
            mcpServerStore,
            nodeDiscoveryClient,
            sessionProfileStore,
            projectStore,
            nodeSessionsClient);
        _ = Security.RefreshAsync();

        // Options → Voice → Assistant (AC-543). Absent in the design-time/unit-test graph the same way Security is,
        // where the page renders its defaults rather than the dialog failing to open.
        AssistantOptions = new AssistantOptionsViewModel(assistantSettingsStore, assistantProfileStore, consentAuditLog);
        _ = AssistantOptions.RefreshAsync();

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
        _assistantMemory = assistantMemory;
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
        _diagrams = diagrams;
        _whiteboards = whiteboards;
        _liveSessions = liveSessions;
        Worktrees = worktrees ?? new WorktreesViewModel();
        Projects = projects ?? new ProjectsViewModel();
        _projectQuickStart = projectQuickStart;

        // Before the first load below, so every card it builds carries them (AC-772) — these are what let one
        // ProjectCardView serve both the Projects workspace and the Manage-projects window.
        Projects.CardActions = new ProjectCardActions(
            StartProjectSessionCommand,
            NewSessionForProjectCommand,
            EditProjectCommand,
            OpenProjectFolderCommand,
            ShareProjectCommand,
            SyncProjectNowCommand);

        // The sidebar's Projects section (AC-164) is on screen from startup, so the list is read now rather than
        // when Options opens — which used to be the only thing that needed it. Fire-and-forget like every other
        // startup read here; the section simply stays hidden until it lands.
        _ = Projects.LoadAsync();
        // The panes are one source of "which sessions are live" (their pane ids, what worktrees are keyed on); the
        // shared registry adds the ones that run without a pane, today the delegated tasks (AC-106) (AC-654).
        IReadOnlySet<string> LivePaneIds() => _AllSessions().Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        liveSessions?.SetSource(LivePaneIds);
        Worktrees.LiveSessionIds = liveSessions is { } registry ? () => registry.LiveSessionIds : LivePaneIds;
        Worktrees.SessionNames = _SessionNames;
        Worktrees.RestoreOfferPaneIds = _RestoreOfferPaneIds;
        Worktrees.ReattachRequested += record => _ = _ReattachSessionAsync(record);
        _ = Worktrees.RefreshCountAsync();
        _worktreeSettingsStore = worktreeSettingsStore;
        WorktreeRootPlaceholder = worktreeSettingsStore?.DefaultRoot ?? string.Empty;
        _ = LoadWorktreeSettingsAsync();
        _cloneSettingsStore = cloneSettingsStore;
        CloneRootPlaceholder = cloneSettingsStore?.DefaultRoot ?? string.Empty;
        _ = LoadCloneSettingsAsync();
        _audioDeviceProvider = audioDeviceProvider;
        _audioCapture = audioCapture;
        _voicePlaybackQueue = voicePlaybackQueue;
        _pluginDiagnostics = pluginDiagnostics;
        _pluginDialogHost = pluginDialogHost;
        _shortcutSettingsStore = shortcutSettingsStore;
        // The full plugin manager needs its store/installer/bootstrap, store dependencies, the dialog host
        // and the diagnostics; when they are absent (unit tests that don't exercise plugins) the design-time
        // manager is used, so the tab is inert.
        Plugins = pluginRegistrationStore is not null && pluginInstaller is not null && pluginBootstrap is not null
                && pluginStoreConfigStore is not null && pluginStoreClient is not null && pluginDialogHost is not null
                && pluginDiagnostics is not null
            ? new PluginManagerViewModel(pluginRegistrationStore, pluginInstaller, pluginBootstrap, dialogService, pluginStoreConfigStore, pluginStoreClient, PluginSettings, pluginDiagnostics, this, appRestartService, workflowTemplateLibrary, pluginProvisioningService)
            : new PluginManagerViewModel();
        // a plugin's fire-and-forget AddMcpServer completing on a background continuation) — without this, the banner
        // would keep reporting the state at startup while the Plugin manager moved on, the exact divergence the ticket
        // rules out (#184).
        if (_pluginDiagnostics is not null)
        {
            _pluginDiagnostics.Changed += () => _OnUiThread(RefreshPluginFailures);
        }
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _sessionProfileStore = sessionProfileStore;
        _ttyProviderResolver = ttyProviderResolver;
        _firstRunWizard = firstRunWizard;
        _help = help;
        if (tryOpenExternalLink is not null)
        {
            _tryOpenExternalLink = tryOpenExternalLink;
        }

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
        _diagnosticsBackgroundService = diagnosticsBackgroundService;
        // The orchestrator loads its own setting on startup (before the UI), so its live value seeds the toggle here.
        _delegationMcpToggle = delegationMcpToggle;
        _orchestratorMcpEnabled = delegationMcpToggle?.McpEnabled ?? true;
        _sessionResourceResolver = sessionResourceResolver;
        _agentCoordinator = agentCoordinator;
        _agentMessages = agentMessages;
        _agentClaims = agentClaims;
        _agentLineBudget = agentLineBudget;
        _claimCollisionMonitor = claimCollisionMonitor;

        // Built here rather than injected, because it is only useful with all four stores present and this is the one
        // place that holds them; a graph missing any of them gets the design-time shape, which says so in the window
        // instead of drawing empty lists (AC-397).
        AgentLineInspector = agentCoordinator is not null && agentClaims is not null && agentLineBudget is not null
                && agentNotifyTrail is not null
            ? new AgentLineInspectorViewModel(agentNotifyTrail, agentClaims, agentLineBudget, agentCoordinator)
            : new AgentLineInspectorViewModel();
        // Read fresh on every refresh rather than captured, because the desk follows the operator's selection.
        AgentLineInspector.Desk = _SelectedSessionDesk;
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

        if (sessionMcpMounts is not null)
        {
            sessionMcpMounts.Reported += _OnSessionMcpMounted;
        }
    }

    // AC-997: the selection stays the full set this route tried, not only the ones that answered — a server that fell
    // over stays present instead of reading as one the operator never checked; the issues ride along for the header's
    // own line and hover to say which one and why (AC-927).
    private void _OnSessionMcpMounted(string paneId, IReadOnlyList<string> connectedServerNames, IReadOnlyList<McpServerConnectionIssue> issues) =>
        _OnUiThread(() =>
        {
            if (FindSession(paneId) is { } session)
            {
                var selection = new HashSet<string>(connectedServerNames, StringComparer.OrdinalIgnoreCase);
                foreach (var issue in issues)
                {
                    selection.Add(issue.Name);
                }

                session.McpServerSelection = selection;
                session.McpServerConnectionIssues = issues;
            }
        });

    // Route a consent prompt to the pane it belongs to. On the UI thread: it sets an observable property and can
    // raise a toast. A prompt whose pane is gone is denied rather than left hanging — there is nowhere to show it.
    private void _OnConsentPromptOpened(object? sender, ConsentPrompt prompt)
    {
        // AC-711: captured now, since the assistant's live instance can be replaced (restart, AC-596 hand-over)
        // before the routing below runs. AssistantIdentity.PaneId is reused across that
        // replacement, so a pane-id-only lookup there can't tell the original instance from its successor.
        var isForAssistant = prompt.Request.Source.PaneId == Cockpit.Core.Assistant.AssistantIdentity.PaneId;
        var assistantWhenOpened = isForAssistant ? _assistantSession : null;

        Dispatcher.UIThread.Post(() =>
        {
            // Either way, if there is nowhere to show it, deny — never hang.
            var pane = prompt.Request.Source.PaneId is { } paneId
                ? _ConsentPanes().FirstOrDefault(session => session.PaneId == paneId)
                : SelectedSession;

            // Replaced while queued: `pane` (if any) is an unrelated successor reusing the same id, not the
            // session that asked, and nothing will ever answer it — deny rather than orphan AC-47's scrim.
            if (isForAssistant && !ReferenceEquals(pane, assistantWhenOpened))
            {
                pane = null;
            }

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

            // If the pane needing consent is not the one in view, point the operator at it. The assistant gets a
            // toast with no Review action: selecting it would put a session that belongs to no workspace into the
            // grid, and its consent is answered in the chat window, which is the one place it can be.
            if (ReferenceEquals(pane, _assistantSession))
            {
                ToastHost.Add($"Consent needed · {pane.Title}", ToastSeverity.Warning, actionLabel: null, onAction: null);
            }
            else if (!ReferenceEquals(pane, SelectedSession))
            {
                ToastHost.Add($"Consent needed · {pane.Title}", ToastSeverity.Warning, "Review", () => SelectedSession = pane);
            }
        });
    }

    private void _OnConsentPromptClosed(object? sender, Guid promptId) =>
        Dispatcher.UIThread.Post(() =>
        {
            // The same seam the open side routes through, embedded panes (AC-152) and the assistant included: a pane
            // an open can reach and a close cannot keeps PendingConsent forever, and the one-banner-per-pane rule
            // above then denies every later request on it without ever showing one.
            if (_ConsentPanes().FirstOrDefault(session => session.PendingConsent?.Id == promptId) is { } pane)
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
                new ConsentSource(pane.PaneId, null, ConsentSourceCatalog.Debug),
                "debug.command",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                "Workflow wants to call a URL",
                "GET https://api.github.com/repos/raymondkrahwinkel/AI-Cockpit/issues",
                new ConsentSource(pane.PaneId, null, ConsentSourceCatalog.Debug),
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
        NotifyOnCiFailure = settings.NotifyOnCiFailure;
    }

    // Persists the notification settings edited in the Options flyout to `cockpit.json`.
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
            NotifyOnCiFailure = NotifyOnCiFailure,
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

    // Persists the keyboard shortcuts edited in the Options → Shortcuts tab to `cockpit.json`.
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

        // AC-608: one gesture, one owner. Subscribed here rather than in the row, because the rule is about the
        // rows as a set and a row cannot see its siblings. The rows are thrown away on every rebuild, so the
        // handler goes with them — nothing to unsubscribe.
        foreach (var row in ShortcutRows)
        {
            row.PropertyChanged += _OnShortcutRowChanged;
        }
    }

    // Takes a gesture away from whoever else held it (AC-608). Without this the operator binds a chord that is
    // already in use and nothing happens: the dispatch invokes the first match in catalog order, so one of the two
    // silently never fires and neither row shows that anything is wrong.
    private void _OnShortcutRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShortcutRowViewModel.Gesture) || sender is not ShortcutRowViewModel claimant)
        {
            return;
        }

        var claimantIndex = ShortcutRows.IndexOf(claimant);
        if (claimantIndex < 0)
        {
            return;
        }

        // Clearing a displaced row raises this again for that row; a blank gesture displaces nobody, so it stops
        // there rather than needing a re-entrancy flag.
        var gestures = ShortcutRows.Select(row => row.Gesture).ToList();
        foreach (var index in ShortcutGestureOwnership.DisplacedBy(gestures, claimantIndex))
        {
            ShortcutRows[index].Gesture = string.Empty;
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

    // Raised when a pane-focus shortcut (Ctrl+arrow) asks to move the selection to the pane in that direction.
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

    // Persists the transcript-display settings edited in the Options flyout to `cockpit.json`.
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
        ShowUsagePillRateWindows = settings.VisibleFields.Contains(UsagePillField.RateWindows);
    }

    // Persists the usage-pill field selection edited in the Options dialog to `cockpit.json`.
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
        WakeAgentsByDefault = settings.WakeAgentsByDefault;
        MemoryBudgetPercent = settings.MemoryBudgetPercent;
        // Pushed explicitly as well as through the property's own change handler: the saved value can equal the
        // property's initial one, and then nothing changed and the handler never ran — leaving the coordinator on
        // its own default rather than on the operator's, which happen to agree today and need not tomorrow.
        _agentCoordinator?.SetDefaultWakeConsent(settings.WakeAgentsByDefault);
    }

    // Persists the session-behaviour settings edited in the Options flyout to `cockpit.json`.
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
            WakeAgentsByDefault = WakeAgentsByDefault,
            MemoryBudgetPercent = MemoryBudgetPercent,
        });
        SessionBehaviorSettingsStatus = "Saved";
    }

    // What the cockpit and its sessions are using, for the status bar (#78) — e.g. "CPU 12% · RAM 1.9 GB".
    [ObservableProperty]
    private string _resourceSummary = string.Empty;

    // The CPU half of the status-bar figure, up to and including "RAM " — split from the memory so the memory alone can
    // change colour.
    [ObservableProperty]
    private string _resourceCpu = string.Empty;

    [ObservableProperty]
    private string _resourceMemory = string.Empty;

    // Which brush the memory figure reads in: quiet, amber as it climbs, red where the system starts killing things.
    [ObservableProperty]
    private string _resourceMemoryBrushKey = "CockpitTextSecondaryBrush";

    // The same, broken down per session — the panel's own text when there is nothing to break down.
    [ObservableProperty]
    private string _resourceDetail = string.Empty;

    // It opens from the figures in the status bar rather than appearing on hover — a tooltip is at the mercy of the
    // platform's hit-testing and placement, and on this one it turned out to be at the mercy of both (#78).
    public ObservableCollection<ResourceRowViewModel> ResourceRows { get; } = [];

    // The local model servers (#78) — Ollama, LM Studio — with what they are holding. A session that talks to one
    // over HTTP has no process of its own, so it can never appear above; the model it loaded is nonetheless the
    // heaviest thing on the machine, and "nothing to break down" was a poor answer to "what is using my memory".
    public ObservableCollection<ResourceRowViewModel> ModelServerRows { get; } = [];

    // Whether a local model server is running at all — no Ollama, no section.
    public bool HasModelServers => ModelServerRows.Count > 0;

    // Whether the resource panel is open — toggled from the status bar's figures.
    [ObservableProperty]
    private bool _isResourcePanelOpen;

    // True when there is nothing to break down: sessions that run over HTTP have no local process to weigh.
    public bool HasResourceRows => ResourceRows.Count > 0;

    // Opens the breakdown, or closes it — the status bar's figures are the button.
    [RelayCommand]
    private void ToggleResourcePanel() => IsResourcePanelOpen = !IsResourcePanelOpen;

    // Closes the breakdown. Esc, and the panel's own close button.
    [RelayCommand]
    private void CloseResourcePanel() => IsResourcePanelOpen = false;

    // Left of the meter: how many sessions are being weighed, so it is visible that the breakdown exists at all rather
    // than hidden behind a hover nobody tries.
    [ObservableProperty]
    private string _resourceSessions = string.Empty;

    // Whether a memory warning is standing. Kept here between samples, because the decision is "has it climbed since
    // I last said so", and that question needs a memory of its own.
    private bool _warnedAboutMemory;

    // One warned-flag per session pane (AC-692), not one flag for the whole cockpit — a session that has already
    // been named should not silence the toast for the next one that climbs.
    private readonly Dictionary<string, bool> _warnedAboutSessionMemory = new(StringComparer.Ordinal);

    // Tells each session how close it is to its own OS memory cap (AC-661), so one that is about to be cut off says
    // so on its own bar first, and past the cap offers the Kill there too (AC-700). Matched back by pane id, the
    // same key the sample was taken under — a title is not one, since two sessions may carry the same (AC-1096).
    private void _WarnAboutSessionCaps(ResourceUsage usage)
    {
        foreach (var measured in usage.Sessions)
        {
            _FindMeasured(measured)?.ReportMemoryAgainstCap(measured.MemoryBytes);
        }
    }

    // The one place the measurement is matched back to the pane it was taken on.
    private SessionPanelViewModel? _FindMeasured(SessionResourceUsage measured) =>
        Sessions.FirstOrDefault(session => string.Equals(session.PaneId, measured.PaneId, StringComparison.Ordinal));

    // AC-1096: puts each session's own processes on its sidebar row, where the status is. Status comes from the
    // agent's event stream and knows nothing about what it left running, so an idle session with a test host still
    // resident is indistinguishable from a finished one until this number sits beside it.
    private void _ReportSessionProcesses(ResourceUsage usage)
    {
        foreach (var measured in usage.Sessions)
        {
            if (_FindMeasured(measured) is not { } session)
            {
                continue;
            }

            session.ProcessCpuPercent = measured.CpuPercent;
            session.ProcessMemoryBytes = measured.MemoryBytes;
            session.ProcessCount = measured.ProcessCount;
            session.AbandonedProcessCount = measured.AbandonedProcessCount;
        }
    }

    // AC-1060: the cap above is about this session's own ceiling; this is about the machine's. A session well
    // inside its cap is killed anyway when the slice it sits in stays under pressure, and that is what oomd reads.
    private void _WarnAboutSessionPressure(ResourceUsage usage)
    {
        foreach (var measured in usage.Sessions)
        {
            if (_FindMeasured(measured)?.ReportMemoryPressure(measured.PressureAvg10) != true)
            {
                continue;
            }

            // No Kill button here, unlike the over-cap toast: killing this session is rarely the answer — it is
            // the machine that is short, and the operator is the one who knows which session matters least.
            ToastHost.Add(
                $"'{measured.Title}' has been stalling on memory for {SessionPressureAlarm.Sustained.TotalSeconds:0}s. "
                + "Sessions are ended whole by the system when this holds — closing one now is cheaper than losing one.",
                ToastSeverity.Warning,
                actionLabel: null,
                onAction: null);
        }
    }

    // Names the session in a cockpit-wide toast with a Kill button the moment it crosses its own cap — replaces
    // the automatic kill that used to happen instead (AC-692). Kept beside AC-700's bar, which outlives it.
    private void _WarnAboutSessionMemory(ResourceUsage usage)
    {
        var stillHere = new HashSet<string>(usage.Sessions.Select(session => session.PaneId), StringComparer.Ordinal);
        foreach (var paneId in _warnedAboutSessionMemory.Keys.Where(paneId => !stillHere.Contains(paneId)).ToList())
        {
            _warnedAboutSessionMemory.Remove(paneId);
        }

        foreach (var measured in usage.Sessions)
        {
            var session = _FindMeasured(measured);
            var cap = session?.MemoryCapBytes ?? 0;
            var warned = _warnedAboutSessionMemory.GetValueOrDefault(measured.PaneId);
            var decision = SessionMemoryPressure.Decide(measured.MemoryBytes, cap, warned);
            _warnedAboutSessionMemory[measured.PaneId] = decision.Warned;

            if (!decision.Warn || session is null)
            {
                continue;
            }

            ToastHost.Add(
                $"'{measured.Title}' is over its {_Megabytes(cap)} memory cap, holding {_Megabytes(measured.MemoryBytes)}. Cockpit will not close it on its own.",
                ToastSeverity.Warning,
                actionLabel: "Kill",
                onAction: () => _ = CloseSessionCommand.ExecuteAsync(session));
        }
    }

    // Says something when the cockpit and its sessions together pass the operator's shared budget (#78, AC-1086).
    // Nothing is closed or refused on the strength of it: AC-692 settled that the operator decides, and a budget
    // that only makes the crossing visible is already the thing the per-session caps could never say.
    private void _WarnAboutMemory(ResourceUsage usage)
    {
        var machine = MachineMemory.TotalBytes();
        var decision = MemoryPressure.Decide(usage.MemoryBytes, machine, MemoryBudgetPercent, _warnedAboutMemory);
        _warnedAboutMemory = decision.Warned;

        if (!decision.Warn)
        {
            return;
        }

        var heaviest = usage.Sessions.MaxBy(session => session.MemoryBytes);

        var advice = heaviest is not null
            ? $" '{heaviest.Title}' is the largest at {_Megabytes(heaviest.MemoryBytes)} — closing or restarting it frees that."
            : string.Empty;

        // AC-1096: the cheapest thing the operator can act on, because nothing is lost by it — these belong to no
        // living parent, so no work in progress ends with them. Only open sessions are summed, which is all the
        // figure above counts too.
        var left = usage.Sessions.Sum(session => session.AbandonedProcessCount);
        var abandoned = left > 0
            ? $" {left} process(es) of open sessions have been left behind and nothing will collect them."
            : string.Empty;

        // Raised on the host this view model owns: ToastService is built *from* it, and injecting the service back in
        // is a circle the container walks forever.
        ToastHost.Add(
            $"The cockpit and its sessions are holding {_Megabytes(usage.MemoryBytes)} of {_Megabytes(machine)} — over the {MemoryBudgetPercent}% shared budget. "
            + "That figure is this app plus every process the sessions now open have spawned, including ones they have lost the parent of. "
            + $"Nothing is closed automatically; when memory runs out the system ends a session for you instead.{advice}{abandoned}",
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

    // Takes one sample and updates the status bar (#78). Driven by a timer in the view, like the idle sweep —
    // the view model stays free of timers, and a test can tick it whenever it likes.
    internal void SampleResources()
    {
        if (_resourceMonitor is null)
        {
            return;
        }

        // Synchronous path for the tests; the live timer uses SampleResourcesAsync to keep the WMI read off the UI
        // thread.
        _ApplyResourceUsage(_resourceMonitor.Sample(_SessionProcessIds()));
    }

    // The WMI Win32_Process read (WmiProcessTableReader) is 70-200ms and, on the DispatcherTimer, blocked the UI
    // thread every 2s — a periodic stutter. Read on the thread pool; apply on the UI thread the await resumes onto.
    internal async Task SampleResourcesAsync()
    {
        if (_resourceMonitor is null || _samplingResources)
        {
            return;
        }

        _samplingResources = true;
        try
        {
            var processes = _SessionProcessIds();
            var usage = await Task.Run(() => _resourceMonitor.Sample(processes));
            _ApplyResourceUsage(usage);
        }
        catch (Exception exception)
        {
            // Belt-and-braces (the reader already swallows WMI errors): a failed sample must not stop the timer.
            _logger?.LogWarning(exception, "A resource sample failed; the next tick will try again.");
        }
        finally
        {
            _samplingResources = false;
        }
    }

    // A session with no process (an HTTP-backed provider) has nothing local to weigh; it is left out rather than
    // shown as 0%, which would read as "idle" instead of "not measurable here".
    private List<SessionProcessRef> _SessionProcessIds()
    {
        var measured = new List<SessionProcessRef>(Sessions.Count);
        foreach (var session in Sessions)
        {
            if (session.ProcessId is { } processId)
            {
                measured.Add(new SessionProcessRef(session.PaneId, session.Title, processId));
            }
        }

        return measured;
    }

    private void _ApplyResourceUsage(ResourceUsage usage)
    {
        _WarnAboutMemory(usage);
        _WarnAboutSessionCaps(usage);
        _WarnAboutSessionMemory(usage);
        _WarnAboutSessionPressure(usage);
        _ReportSessionProcesses(usage);

        ResourceCpu = $"CPU {usage.CpuPercent:0}%  ·  RAM ";
        ResourceMemory = _Megabytes(usage.MemoryBytes);

        // Amber before the toast, red at the point where macOS starts thinking about killing the app: a number that
        // changes colour while you work is something you can act on without being interrupted.
        ResourceMemoryBrushKey = MemoryPressure.Level(usage.MemoryBytes, MachineMemory.TotalBytes(), MemoryBudgetPercent) switch
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
                // AC-734: matched by process id, not by the raw "claude" name, so a second same-named child stays
                // on its generic label rather than being guessed at.
                child.ProcessId is { } pid && pid == AssistantPane?.ProcessId ? "Assistant" : child.Name,
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

    // Whether this cockpit can back itself up (#70) — false only in the design-time view model, which has no
    // services at all. The buttons bind to it, so a build that forgot to register the service shows them disabled
    // rather than showing two controls that swallow a click and do nothing.
    public bool CanBackUp => _backupService is not null;

    // Same reasoning as CanBackUp, for the assistant-memory-only export/restore (AC-657).
    public bool CanBackUpAssistantMemory => _assistantMemory is not null;

    // Reads the update preferences and, if they say so, looks once for a newer build (#71).
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

            // Per control rather than all-or-nothing: they touched one setting, not the section, and treating the whole
            // section as spoken for is how touching the startup box came to discard a channel chosen on an earlier run.
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

    // One background re-check for a newer build (AC-188), on the hourly cadence set by `StartPeriodicUpdateChecks`.
    // Gated by the same `CheckForUpdatesOnStartup` setting as the startup look, and toasts a given release
    // only once — a build already on offer, or one the operator dismissed, stays quiet. Silent on a failed check.
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
            // A background poll that cannot reach GitHub says nothing — an error toast for a look nobody asked for is
            // noise.
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

    // Starts an idempotent UI-thread update timer so long-running cockpits see new releases (AC-188), stopped by
    // DisposeAsync. Also watches for due resumes and supplies the live-session lookup only Cockpit knows
    // (AC-234); both are no-ops when their optional services are absent.
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

    // Looks now, because the operator asked (#71).
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

    // Opens the release page. The cockpit does not install itself — see IUpdateService for why.
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

    // Downloads self-updates without disturbing the current offer on failure (AC-388). Applying never happens
    // automatically: confirmation names the running-session count, and declining leaves the build ready for a
    // later click or InstallUpdateOnNextStartAsync.
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

    // Downloads the build on offer and asks for it to be applied the next time the cockpit starts, leaving this
    // session alone (criterion 3, criterion 7's conservative alternative to restarting now). A request that could not
    // be written says so (AC-738): the failure this replaces was a promise the operator had no way to check.
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

        UpdateStatus = updates.RequestUpdateOnNextStart()
            ? $"Downloaded {UpdateName}. It will be installed the next time the cockpit starts."
            : $"Downloaded {UpdateName}, but the request to install it on the next start could not be saved. Use \"Update now\" instead.";
    }

    // The download half shared by `UpdateNowAsync` and `InstallUpdateOnNextStartAsync`.
    // Returns whether it succeeded; a failure already left `UpdateStatus` saying why and touched
    // nothing else (criterion 4) — the caller has nothing left to do but stop.
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

    // Names how many sessions are running before "Update now" restarts (criterion 7) — never a generic "are you
    // sure?". `SessionPanelViewModel.RequiresCloseConfirmation` is the same reading the close-confirm
    // prompt already uses for "is this session doing something a restart would cut off".
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

    // Hides the update banner (AC-73) for the build now on offer. Per-build, not forever: the operator is saying
    // "not this one", so a later check that finds a newer build shows the banner again — see `_Announce`.
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

    // The stream the checks ask on: what the channel control says, however it came to say it.
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

    // Touching the channel is the choice (AC-387). From here on it is the operator's and it wins over what the build
    // would have implied — including when they set it back to the value the build gave them.
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

    // Defer writing until stored update settings are read, or an uninitialized channel would erase the operator's
    // earlier choice. InitialiseUpdatesAsync saves once both halves are known.
    private void _SaveUpdateSettings()
    {
        // AC-999: while the Options dialog is open this is one of the settings held back until Apply, which
        // flushes it by calling here again once the flag is down.
        if (_optionsStaged || _updateSettingsStore is not { } store)
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

    // Writes the whole cockpit to `archivePath` (#70). The view picks the file; this decides what
    // goes in it, and says afterwards what was left out — a backup without keys is only useful if you know which
    // ones you will have to enter again.
    public async Task CreateBackupAsync(string archivePath)
    {
        if (_backupService is not { } backups)
        {
            BackupStatus = NoBackupService;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;

        try
        {
            IsBackupRunning = true;

            // Unlike a restore, a backup can be stopped right up to the end: it builds its zip under staging and
            // only moves it into place when it is whole, so nothing outside staging exists to be left half-done.
            CanStopBackup = true;
            BackupStatus = "Backing up…";

            var chosen = BackupPlugins.Where(plugin => plugin.Selected).Select(plugin => plugin.Id).ToList();

            var manifest = await backups.WriteAsync(
                archivePath,
                new BackupOptions(BackupIncludesCredentials, BackupIncludesProfiles, chosen),
                cancellation.Token);

            var stripped = manifest.RemovedSecrets.Count == 0
                ? string.Empty
                : $" {manifest.RemovedSecrets.Count} were left out and must be entered again after a restore.";

            BackupStatus = $"Backed up to {Path.GetFileName(archivePath)}.{stripped}";
        }
        catch (OperationCanceledException)
        {
            BackupStatus = "The backup was stopped. No archive was written.";
        }
        catch (Exception exception)
        {
            BackupStatus = $"The backup was not made: {exception.Message}";
        }
        finally
        {
            _backupCancellation = null;
            IsBackupRunning = false;
            CanStopBackup = false;
        }
    }

    // Said out loud rather than returned in silence (AC-1281): without a backup service the buttons do nothing,
    // and "nothing happened" is the one outcome an operator cannot tell apart from a cockpit that has hung.
    private const string NoBackupService = "Backup is not available in this build, so nothing was done.";

    // Preview archive settings/plugins before asking what to restore (#70). Move replaced data aside rather than
    // deleting it, then restart to load the restored state; a null choice cancels.
    public async Task RestoreBackupAsync(string archivePath, Func<BackupManifest, Task<RestoreOptions?>> choose)
    {
        if (_backupService is not { } backups)
        {
            BackupStatus = NoBackupService;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;

        try
        {
            var manifest = await backups.ReadManifestAsync(archivePath, cancellation.Token);

            if (await choose(manifest) is not { } options)
            {
                return;
            }

            IsBackupRunning = true;
            CanStopBackup = true;
            BackupStatus = "Restoring…";
            _lastRestoreStage = null;

            var progress = new RestoreProgressReporter(this);
            var report = await backups.RestoreAsync(archivePath, options, progress, cancellation.Token);

            // How far it got is not asked for back: the stage arrived over `progress` on the way, and a second
            // channel for the same fact is one that can disagree with the first.
            if (report.Stopped)
            {
                BackupStatus = _lastRestoreStage == RestoreStage.Unpacking
                    ? "The restore was stopped while unpacking. Nothing here was changed."
                    : "The restore was stopped before the settings were put back, so they are unchanged. "
                      + "Anything already fetched was left in place." + _WorthKnowing(report);

                return;
            }

            BackupStatus = report.Notes.Count == 0
                ? "Restored. Restarting the cockpit to read it."
                : $"Restored.{_WorthKnowing(report)} Restarting the cockpit to read the rest.";

            _appRestart?.Restart();
        }
        catch (OperationCanceledException)
        {
            // A stop the restore itself reports comes back as a report; this is only the one that beat it to the
            // start — the token was already cancelled when the manifest was read, or before the work was queued.
            BackupStatus = "The restore was stopped before it began. Nothing here was changed.";
        }
        catch (Exception exception)
        {
            BackupStatus = $"Nothing was restored: {exception.Message}";
        }
        finally
        {
            _backupCancellation = null;
            IsBackupRunning = false;
            CanStopBackup = false;
        }
    }

    // Whether a backup or restore is running at all, and — separately — whether it can still be stopped. Two flags
    // rather than one so the button stays on screen and goes dead at the moment stopping stops being safe
    // (AC-1278); a button that disappears tells the operator nothing about why.
    [ObservableProperty]
    private bool _isBackupRunning;

    [ObservableProperty]
    private bool _canStopBackup;

    private CancellationTokenSource? _backupCancellation;

    // The last stage the restore reported. Kept because a stopped restore comes back saying only that it stopped —
    // where it stopped is what decides whether "nothing was changed" is true, and it already arrived over progress.
    private RestoreStage? _lastRestoreStage;

    public void StopBackup() => _backupCancellation?.Cancel();

    // "Worth knowing" rather than "still not installed" (AC-1279): the list carries plugins that came back on a
    // different version too, and calling those missing would be a lie the operator could act on.
    private static string _WorthKnowing(RestoreReport report) =>
        report.Notes.Count == 0
            ? string.Empty
            : " Worth knowing about these plugins: "
              + string.Join(", ", report.Notes.Select(plugin => $"{plugin.Id} ({plugin.Note})")) + ".";

    // Marshals onto the UI thread itself: BackupService reports from the thread pool it offloaded the unpacking to,
    // and Progress<T> would only do the same if this were always constructed on the UI thread.
    private sealed class RestoreProgressReporter(CockpitViewModel cockpit) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => _OnUiThread(() =>
        {
            cockpit._lastRestoreStage = value.Stage;

            // A stop is honoured between plugins, never in the middle of one, so the line says "finishing" rather
            // than anything that would read as immediate — the fetch it interrupts can still take a moment.
            var stopping = cockpit._backupCancellation?.IsCancellationRequested == true;

            cockpit.BackupStatus = value.Stage switch
            {
                RestoreStage.Unpacking => "Unpacking the archive…",
                RestoreStage.Writing => "Putting the settings back…",
                RestoreStage.FetchingPlugins when stopping =>
                    $"Finishing the plugin being fetched, then stopping… {value.Done} of {value.Total}.",
                RestoreStage.FetchingPlugins when value.Total > 0 => $"Fetching plugins… {value.Done} of {value.Total}.",
                RestoreStage.FetchingPlugins => "Fetching plugins…",
                _ => cockpit.BackupStatus,
            };

            // Writing alone withdraws the offer: from there cockpit.json is being rewritten, and a half-written one
            // is what all of this exists to prevent. Unpacking and the fetch both run before it and stay stoppable.
            if (value.Stage == RestoreStage.Writing)
            {
                cockpit.CanStopBackup = false;
            }
        });
    }

    // The assistant's own memory, on its own (AC-657) — loose from the rest of the cockpit backup above: no
    // settings, no plugins, no secrets dialog, just assistant-memory.md and assistant-state.md. The same
    // IAssistantMemory the assistant's own export_assistant_memory/import_assistant_memory MCP tools write through.
    public async Task ExportAssistantMemoryAsync(string archivePath)
    {
        if (_assistantMemory is not { } memory)
        {
            return;
        }

        try
        {
            AssistantMemoryBackupStatus = "Exporting…";
            var written = await memory.ExportAsync(archivePath);
            AssistantMemoryBackupStatus = $"Exported {string.Join(" and ", written)} to {Path.GetFileName(archivePath)}.";
        }
        catch (Exception exception)
        {
            AssistantMemoryBackupStatus = $"Nothing was exported: {exception.Message}";
        }
    }

    // No selection dialog, unlike RestoreBackupAsync: there is nothing to choose, only these two files, and what
    // gets replaced is copied aside rather than deleted — see AssistantMemoryFile.ImportAsync. Does not restart the
    // cockpit; the assistant reads the new memory the next time its own session restarts.
    public async Task ImportAssistantMemoryAsync(string archivePath)
    {
        if (_assistantMemory is not { } memory)
        {
            return;
        }

        try
        {
            AssistantMemoryBackupStatus = "Restoring…";
            var restored = await memory.ImportAsync(archivePath);
            AssistantMemoryBackupStatus = $"Restored {string.Join(" and ", restored)}.";
        }
        catch (Exception exception)
        {
            AssistantMemoryBackupStatus = $"Nothing was restored: {exception.Message}";
        }
    }

    // Fills the backup tab's plugin list from what is installed (#70). Called when the Options dialog opens: a plugin
    // installed since the app started should not be missing from its own backup.
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

    partial void OnLogDiagnosticSnapshotsChanged(bool value) => _diagnosticsBackgroundService?.SetSnapshotsEnabled(value);

    // Flips the orchestrator MCP on or off (AC-40) and persists it; it takes effect on the next session's servers.
    // Held back while the Options dialog is staging (AC-999) — `ApplyOptionsAsync` flips it there instead.
    partial void OnOrchestratorMcpEnabledChanged(bool value)
    {
        if (!_optionsStaged)
        {
            _ = _delegationMcpToggle?.SetMcpEnabledAsync(value);
        }
    }

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
            _pluginMenuPreferences[folderId] = new PluginMenuPreference(registration.MenuOrder, registration.HiddenInMenu, registration.PinnedToSidebar);
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
        LogDiagnosticSnapshots = settings.LogDiagnosticSnapshots;
    }

    // Persists the debug settings edited in the Options dialog to `cockpit.json`.
    [RelayCommand]
    private async Task SaveDebugSettingsAsync()
    {
        if (_debugSettingsStore is null)
        {
            return;
        }

        await _debugSettingsStore.SaveAsync(new DebugSettings
        {
            ShowDebugControls = ShowDebugControls,
            LogDiagnosticSnapshots = LogDiagnosticSnapshots,
        });
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
        GlobalFocusRailLayout = settings.FocusRailLayout;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        SidebarWidth = settings.SidebarWidth;
        SidebarCollapsed = settings.SidebarCollapsed;
        GlobalFocusRailWeight = settings.FocusRailWeight;
        DockRailWidth = settings.DockRailWidth;
        OpenDockPanelId = settings.OpenDockPanelId;
        AssistantDocked = settings.AssistantDocked;
    }

    // Every layout save writes the whole record, because the store holds one section rather than per-field keys —
    // so a save that built its own literal could silently drop a field it did not know about. Written once here
    // for that reason: this is the list a new layout setting has to be added to, and the only one.
    private LayoutSettings _CurrentLayoutSettings() => new()
    {
        SingleSessionLayout = GlobalSingleSessionLayout,
        StackSessionsVertically = GlobalStackSessionsVertically,
        FocusRailLayout = GlobalFocusRailLayout,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        SidebarWidth = SidebarWidth,
        SidebarCollapsed = SidebarCollapsed,
        FocusRailWeight = GlobalFocusRailWeight,
        DockRailWidth = DockRailWidth,
        OpenDockPanelId = OpenDockPanelId,
        AssistantDocked = AssistantDocked,
    };

    // AC-953: docking or undocking the assistant moves both settings at once — which host it stands in, and
    // which rail panel is open to show it — so they go out in one write rather than two that would each
    // read-modify-write the same section. Called by `AssistantIndicatorCoordinator`, which owns the swap itself.
    public async Task SetAssistantDockedAsync(bool docked, string? openDockPanelId)
    {
        AssistantDocked = docked;
        OpenDockPanelId = openDockPanelId;

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
    }

    // Persists the layout settings edited in the Options flyout to `cockpit.json`.
    [RelayCommand]
    private async Task SaveLayoutSettingsAsync()
    {
        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
        LayoutSettingsStatus = "Saved";
    }

    // Persist direct sidebar resizing immediately after the drag (#49), unlike staged Options settings. Clamp
    // before assignment and save so an out-of-range value cannot persist.
    public async Task SetSidebarWidthAsync(double width)
    {
        SidebarWidth = Math.Clamp(width, LayoutSettings.MinSidebarWidth, LayoutSettings.MaxSidebarWidth);

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
    }

    // Collapses or expands the left sidebar and persists it immediately — a direct-manipulation setting like
    // the width drag, so it survives a restart without waiting for the Options dialog's Save.
    [RelayCommand]
    private async Task ToggleSidebar()
    {
        SidebarCollapsed = !SidebarCollapsed;

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
    }

    // Persists the dock rail's width alone (AC-951), the sidebar's `SetSidebarWidthAsync` mirrored: called from
    // the view when its `GridSplitter` drag ends, clamped before both the assignment and the save.
    public async Task SetDockRailWidthAsync(double width)
    {
        DockRailWidth = Math.Clamp(width, LayoutSettings.MinDockRailWidth, LayoutSettings.MaxDockRailWidth);

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
    }

    // Opens the tapped rail panel, or closes the rail if that panel is already the open one — the toggle the
    // acceptance criteria ask for ("klik klapt uit, tweede klik weer in"). Switching straight from one open
    // panel to another (without a close in between) is the same one click, since only one can be open at a time.
    [RelayCommand]
    private async Task ToggleDockPanel(string panelId)
    {
        OpenDockPanelId = OpenDockPanelId == panelId ? null : panelId;

        if (_layoutSettingsStore is null)
        {
            return;
        }

        await _layoutSettingsStore.SaveAsync(_CurrentLayoutSettings());
    }

    private async Task LoadWorktreeSettingsAsync()
    {
        if (_worktreeSettingsStore is null)
        {
            return;
        }

        WorktreeRoot = (await _worktreeSettingsStore.LoadAsync()).Root ?? string.Empty;
    }

    // Persists the worktree-root override (AC-85); a blank field clears the override, keeping the default.
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

    // Persists the clones-root override (AC-90); a blank field clears the override, keeping the default.
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

    // (Re)builds the Options default-shell picker (#AC-25) from the shells detected now, and selects the one the
    // saved `configured` value names (its `ShellDescriptor.Id`, matched
    // case-insensitively) — falling back to "OS default" when it is blank or no longer resolves on this machine.
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

    // Persists the TTY terminal-appearance settings (#40) edited in the Options dialog to `cockpit.json`, clamping the
    // font size to the supported range.
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
        VoicePushToTalkKeyName = settings.PushToTalkKeyName;
        VoiceGlobalPushToTalk = settings.GlobalPushToTalk;
        // First load is app startup — capture what the hotkey actually armed with, so a later save can tell a real
        // change from a toggle-and-back. Reopening the Options dialog reloads but must not move the baseline.
        _voiceGlobalPushToTalkRunning ??= settings.GlobalPushToTalk;
        VoiceAutoSubmit = settings.AutoSubmitAfterVoice;
        VoiceOpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs;
        VoiceStopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking;
        VoiceStopReadAloudLevelThreshold = (decimal)settings.StopReadAloudLevelThreshold;
        SelectedTtsVoice = TtsVoices.FirstOrDefault(voice => voice.Sid == settings.TtsVoiceSid) ?? TtsVoiceCatalog.Default;
        VoiceTtsSpeed = (decimal)settings.TtsSpeed;
        SelectedReadAloudLanguage = ReadAloudLanguages.FirstOrDefault(language => language.Code == settings.ReadAloudLanguage) ?? ReadAloudLanguages[0];
        SelectedSttLanguage = SttLanguages.FirstOrDefault(language => language.Code == settings.SttLanguage) ?? SttLanguages[0];

        // Show this machine's last calibration if it has ever been run here (AC-68 slice 3).
        if (_transcriptionCalibrationStore is not null && await _transcriptionCalibrationStore.LoadAsync() is { } calibration)
        {
            _ApplyCalibration(calibration);
        }
    }

    // Refresh devices when Options opens so newly attached hardware appears without starting the audio backend on
    // every launch. Keep System default first, reselect the saved device, and no-op in provider-less previews.
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

    // Persist voice Options to cockpit.json. Open sessions re-read on their next push-to-talk hold; the enabled
    // gate remains session-creation state, matching the profile picker's new-session semantics.
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
            PushToTalkKeyName = string.IsNullOrWhiteSpace(VoicePushToTalkKeyName) ? "F9" : VoicePushToTalkKeyName.Trim(),
            GlobalPushToTalk = VoiceGlobalPushToTalk,
            AutoSubmitAfterVoice = VoiceAutoSubmit,
            OpenMicEnabled = current.OpenMicEnabled,
            OpenMicSilenceTimeoutMs = VoiceOpenMicSilenceTimeoutMs > 0 ? VoiceOpenMicSilenceTimeoutMs : 800,
            StopReadAloudWhenSpeaking = VoiceStopReadAloudWhenSpeaking,
            StopReadAloudLevelThreshold = (double)VoiceStopReadAloudLevelThreshold,
            TtsVoiceSid = SelectedTtsVoice.Sid,
            TtsSpeed = (double)VoiceTtsSpeed,
            ReadAloudLanguage = SelectedReadAloudLanguage.Code,
            SttLanguage = SelectedSttLanguage.Code,
            InputDeviceName = SelectedInputDevice.DeviceName ?? "",
            OutputDeviceName = SelectedOutputDevice.DeviceName ?? "",
        });

        // Push the read-aloud settings to already-open sessions so toggling naturalization or the voice takes effect
        // immediately, rather than only on the next session (the enabled/PTT flags keep the load-at-start behaviour,
        // which the hold path re-reads).
        foreach (var session in _WithAssistant(Sessions))
        {
            session.TtsVoiceSid = SelectedTtsVoice.Sid;
            session.ReadAloudLanguage = SelectedReadAloudLanguage.Code;
        }

        VoiceSettingsStatus = "Saved";

        // On Linux the global hotkey is a desktop-portal binding the compositor only takes at startup, so a change
        // to it there needs a restart — unlike Windows, where the re-arm below applies it live.
        VoiceGlobalPushToTalkNeedsRestart =
            ShouldOfferGlobalPushToTalkRestart(IsLinuxPlatform, _voiceGlobalPushToTalkRunning, VoiceGlobalPushToTalk);

        // Raised rather than called: VoicePushToTalkCoordinator takes this view model, so injecting it back here is a
        // circle the container walks forever — the same reason the toasts go through ToastHost.
        VoiceSettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    // Raised once the voice settings are saved, so whatever was configured from them can re-apply.
    public event EventHandler? VoiceSettingsSaved;

    // Whether a "Restart now" affordance can do anything — false in the design-time constructor, where there is no real
    // app to restart.
    public bool CanRestartApp => _appRestart is not null;

    // Restarts the app so a saved change that only applies at startup (the Linux global hotkey) takes effect, without
    // the operator closing and relaunching by hand.
    [RelayCommand(CanExecute = nameof(CanRestartApp))]
    private void RestartApp() => _appRestart?.Restart();

    // AC-691: raised rather than called for the same reason VoiceSettingsSaved is — see its remarks.
    public event EventHandler? HotkeyPortalRetryRequested;

    [RelayCommand]
    private void RetryHotkeyPortalPermission() => HotkeyPortalRetryRequested?.Invoke(this, EventArgs.Empty);

    // Whether saving global push-to-talk should offer a restart: only on Linux (elsewhere the change applies
    // live), and only when the saved value differs from what this process armed with at startup — so toggling it
    // and back offers nothing. Pulled out so the platform-gated decision is testable off Linux.
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

    // Opens the New-session dialog — SDK vs TTY is now chosen inside it (#32) — and, once confirmed, mints the matching
    // session: SDK (headless stream-json rendered as the chat UI) or TTY (the real interactive `claude` TUI in a
    // terminal panel, the #9 experiment), started immediately with the chosen profile and start options.
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

    // Runtime-only, exactly like an AI session (AC-25).
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

    // "New terminal (administrator)" (AC-967): the same shell as `NewTerminal`, but started elevated through
    // `ShellExecuteEx`+`runas`, which means its own OS console window instead of a pane — an elevated process
    // cannot be adopted into our ConPTY. Windows-only; nothing here runs on another platform.
    [RelayCommand]
    private void NewElevatedTerminal()
    {
        if (!IsWindowsPlatform)
        {
            return;
        }

        var shell = _ResolveDefaultShell();
        if (shell is null)
        {
            return;
        }

        var error = ElevatedTerminalLauncher.Launch(shell);
        if (error is not null)
        {
            ToastHost.Add(error, ToastSeverity.Warning, actionLabel: null, onAction: null);
        }
    }

    // The shell a new terminal opens (#AC-25): the operator's configured default when it is set and still resolves on
    // this machine (matched by `ShellDescriptor.Id` or absolute path, so a configured "pwsh" survives a machine where
    // its path differs), otherwise the OS default — the first shell `ShellCatalog` detects.
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

    // Opens a session on `profile` for a plugin (#69) — a workflow step, a shortcut — and hands it `prompt` as its
    // first input (AC-312).
    public async Task<string> StartSessionForPluginAsync(SessionProfile profile, string? prompt, string? workingDirectory, string? sessionName = null)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? $"{profile.Label} — {DateTime.Now:HH:mm}" : sessionName.Trim();

        // An SDK session, always: a plugin's prompt is text handed to a session, and a TTY is a terminal a human
        // drives.
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

    // Opens the New-session dialog on a plugin's behalf (#AC-96), optionally pre-filled from `prefill`, starts the
    // session the operator confirms, and returns its `SessionPanelViewModel.PaneId` — or null when the operator cancels
    // or nothing could be started.
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

    // The project a plugin's prefill named by its link (AC-419) — "the one tracked in YouTrack's AC" — handed to the
    // dialog through the project parameter the operator's own project pick already uses (AC-164), so a preselected
    // project brings its folder, profile, worktree default and MCP overlay exactly as picking it by hand would.
    private async Task<Project?> _ProjectLinkedAsAsync(ProjectLink? link)
    {
        if (link is null)
        {
            return null;
        }

        // Guarded like _ProjectIdForDirectoryAsync's read for the same reason: an unreadable list costs a preselection,
        // while an exception escaping here would reach the host's catch and cancel the dialog outright — no session at
        // all because a convenience could not be worked out.
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

    // Starts a session on `project` with the project's own defaults and no dialog (AC-164) — the
    // sidebar's ▶ and the launcher's Start. What it opens with is `ProjectQuickStart`'s to answer; this
    // only launches it, through the same path the dialog's result takes.
    [RelayCommand]
    private async Task StartProjectSessionAsync(Project? project)
    {
        if (project is null)
        {
            return;
        }

        if (_projectQuickStart is not null && await _projectQuickStart.ComposeAsync(project) is { } result)
        {
            // Only the name changes; that it is composed came with the result, and stays with it (#AC-324) — and being
            // composed is also what gets it numbered against the sessions already open, in
            // _LaunchSessionFromResultAsync.
            await _LaunchSessionFromResultAsync(result with { SessionName = project.Name });

            return;
        }

        // The project names no profile that still exists, so there is nothing to start it on. Ask rather than fail
        // quietly: the dialog opens on the project, leaving the operator only the choice the project cannot make.
        await NewSessionForProjectAsync(project);
    }

    // Opens the New-session dialog on `project` (AC-164) — the "New session…" next to the quick
    // start, for when the operator wants to change something the project would otherwise decide.
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

    // `title` if no session carries it, else "`title` 2", "… 3" — the first free one.
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

    // Opens `project`'s folder in the operating system's own file manager — the same shell hand-off the worktrees
    // dialog uses.
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

    // Opens the project editor for `project` from the sidebar, persisting through the same manager the Options tab
    // uses.
    [RelayCommand]
    private Task EditProjectAsync(Project? project) =>
        project is null ? Task.CompletedTask : Projects.EditAsync(project);

    // The launcher's own Share…/Stop sharing… action (AC-620) — one button, two directions, same either/or
    // Projects.ToggleSharingAsync already answers for Manage projects' selection-based command.
    [RelayCommand]
    private Task ShareProjectAsync(Project? project) =>
        project is null ? Task.CompletedTask : Projects.ToggleSharingAsync(project);

    // AC-894: the ⋯ menu's own "Sync now" — one immediate `DepotSyncWatcher` check for this project, outside its
    // 15-minute timer. `Projects.SyncNow` is null under the previewer and until `App.axaml.cs` wires the watcher.
    [RelayCommand]
    private Task SyncProjectNowAsync(Project? project) =>
        project is null || Projects.SyncNow is null ? Task.CompletedTask : Projects.SyncNow(project);

    // Mints and starts the matching session (SDK chat or TTY terminal) from a confirmed result, recording the result on
    // the panel so the context-menu Duplicate can replay it (AC-96, AC-545, AC-719).
    private async Task<string?> _LaunchSessionFromResultAsync(
        NewSessionResult result, string? targetWorkspaceId = null, bool interactive = true)
    {
        if (_sessionFactory is null || _ttySessionFactory is null)
        {
            return null;
        }

        // A second session on the same project is named "Cockpit 2", not a second "Cockpit": two identical rows in the
        // sidebar is exactly the confusion numbering exists to prevent. Only a composed name is numbered — a name the
        // operator typed is theirs and is started exactly as typed (#AC-324).
        if (!result.NameIsChosen && result.SessionName is { Length: > 0 } composed)
        {
            result = result with { SessionName = _UniqueSessionTitle(composed) };
        }

        SessionPanelViewModel session = result.Kind == SessionKind.Sdk ? _sessionFactory() : _ttySessionFactory();
        session.LaunchResult = result;
        AddSession(session, result.SessionName, result.Profile.Label, result.NameIsChosen, targetWorkspaceId);

        // AC-410: written now, before the session actually starts — see _PersistNewSessionPane for why this order
        // is the crash-safe one.
        _PersistNewSessionPane(session, result);

        return await _StartSessionAsync(session, result, interactive);
    }

    // Split out so a restore (which only ever attaches, never starts) does not carry this half, and reused as-is by the
    // fresh-launch path above (AC-410).
    private async Task<string?> _StartSessionAsync(SessionPanelViewModel session, NewSessionResult result, bool interactive = true)
    {
        string paneId;
        string? startedWorkingDirectory;
        string? startedPermissionMode;
        if (session is SessionViewModel sdkSession)
        {
            string? workingDirectory;
            try
            {
                workingDirectory = await _ResolveIsolatedWorkingDirectoryAsync(sdkSession, result, interactive);
            }
            catch (Exception exception)
            {
                // Isolation failed — declined, or a non-interactive caller was refused outright. Either way undo
                // the half-added session (CloseSessionAsync also removes its pane record) rather than starting it
                // in the operator's real working tree.
                await CloseSessionAsync(sdkSession);
                if (exception is OperationCanceledException)
                {
                    return null;
                }

                // Not a decline — a non-interactive refusal with a reason worth keeping. Rethrow so the caller (the
                // assistant gateway) reports it rather than the caller seeing a session that silently is not there.
                if (exception is WorktreeAdmissionException && interactive)
                {
                    ToastHost.Add(exception.Message, ToastSeverity.Warning, null, null);
                    return null;
                }

                throw;
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
                workingDirectory = await _ResolveIsolatedWorkingDirectoryAsync(ttySession, result, interactive);
            }
            catch (Exception exception)
            {
                // Same reasoning as the SDK branch above: cleanup happens whichever of the two the failure is, only
                // a decline swallows it silently.
                await CloseSessionAsync(ttySession);
                if (exception is OperationCanceledException)
                {
                    return null;
                }

                if (exception is WorktreeAdmissionException && interactive)
                {
                    ToastHost.Add(exception.Message, ToastSeverity.Warning, null, null);
                    return null;
                }

                throw;
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

        // Written once here rather than at two separate "session started"/"worktree coupled" moments: by this point
        // isolation has already resolved (session.WorktreeBranch is set when it applied), so a second write immediately
        // after this one would say nothing new (AC-409).
        _ = _sessionStateRecorder?.RecordSessionStartedAsync(
            paneId,
            result.Profile,
            startedWorkingDirectory,
            worktreePath: session.WorktreeBranch is not null ? startedWorkingDirectory : null,
            worktreeBranch: session.WorktreeBranch,
            startedPermissionMode);

        // Kept below the state write on purpose: the record above says which worktree this session *owns*, which
        // this does not change — only what the header shows about where it runs.
        await _AdoptWorktreeBadgeAsync(session, startedWorkingDirectory);

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

    // When asked and the folder is a git repository, a worktree is created for this session on its own branch — keyed
    // on the session's pane, so the same session identity is used whichever kind it is — and the session runs there
    // instead of in the folder as given; the branch shows as a header chip (AC-85, AC-938).
    private async Task<string?> _ResolveIsolatedWorkingDirectoryAsync(
        SessionPanelViewModel session, NewSessionResult result, bool interactive = true)
    {
        if (!result.IsolateInWorktree && !string.IsNullOrWhiteSpace(result.WorkingDirectory))
        {
            if (await _MatchingWorktreeAsync(result.WorkingDirectory) is not { } managed)
            {
                return result.WorkingDirectory;
            }

            if (_worktreeManager is null || await _worktreeManager.ReattachAsync(managed.Path, session.PaneId) is not { } reattached)
            {
                throw new WorktreeAdmissionException(managed.Path, managed.SessionId);
            }

            session.WorktreeBranch = reattached.Branch;
            return reattached.Path;
        }

        if (!result.IsolateInWorktree)
        {
            return result.WorkingDirectory;
        }

        try
        {
            if (_worktreeManager is null)
            {
                throw new InvalidOperationException("worktree isolation is not available here (no worktree manager).");
            }

            if (string.IsNullOrWhiteSpace(result.WorkingDirectory))
            {
                throw new InvalidOperationException("no working directory is set, so no isolated worktree can be created.");
            }

            // Reattach: the folder is already a worktree the cockpit created — re-own it for this session and run
            // there, rather than nesting a new worktree inside it.
            var existing = await _MatchingWorktreeAsync(result.WorkingDirectory);
            if (existing is not null)
            {
                if (await _worktreeManager.ReattachAsync(existing.Path, session.PaneId) is { } reattached)
                {
                    session.WorktreeBranch = reattached.Branch;
                    return reattached.Path;
                }

                throw new WorktreeAdmissionException(existing.Path, existing.SessionId);

            }

            if (await _worktreeManager.DetectRepositoryAsync(result.WorkingDirectory) is null)
            {
                throw new InvalidOperationException("the working directory is not a git repository, so no isolated worktree can be created.");
            }

            var worktree = await _worktreeManager.CreateForSessionAsync(session.PaneId, result.Profile.Label, result.WorkingDirectory);
            session.WorktreeBranch = worktree.Branch;
            return worktree.Path;
        }
        catch (WorktreeAdmissionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The worktree could not be created; running unisolated is the exact contamination isolation exists to
            // prevent, so this never falls back to it silently. A non-interactive caller (AC-719: an assistant
            // spawn) never gets the dialog below — a modal it cannot see or answer — so it is refused with the reason.
            if (!interactive)
            {
                throw new InvalidOperationException(
                    $"worktree isolation failed for '{result.WorkingDirectory}': {exception.Message}");
            }

            // Ask, and only run unisolated on an explicit yes. A no throws OperationCanceledException, which the
            // launch path turns into a cancelled start rather than contaminating the working tree.
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

    // Uses the same OS-aware path comparison the worktree engine does, so a case-only difference matches on
    // Windows/macOS and is distinct on Linux (AC-320).
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

    // AC-633: a `worktree_create`-made worktree is registered to the pane that asked for it, not to the session
    // started in it, so neither the paths above nor a per-pane lookup finds it — the folder is what is true on
    // every route. Display only: who owns the worktree and who tears it down is unchanged.
    internal async Task _AdoptWorktreeBadgeAsync(SessionPanelViewModel session, string? startedWorkingDirectory)
    {
        if (session.WorktreeBranch is not null || startedWorkingDirectory is not { Length: > 0 })
        {
            return;
        }

        try
        {
            session.WorktreeBranch = (await _MatchingWorktreeAsync(startedWorkingDirectory))?.Branch;
        }
        catch (Exception)
        {
            // A badge is never worth a started session: an unreadable registry or a path git rejects leaves it off.
        }
    }

    // Context-menu Rename: begin the sidebar row's inline rename.
    [RelayCommand]
    private void RenameSession(SessionPanelViewModel session) => session.BeginRename();

    // Context-menu Set status (AC-32): edit this session's status line by hand through the dialog, seeded with its
    // current value. Writes the result back to the same `SessionPanelViewModel.Statusline` the MCP
    // `set_status` tool sets, so manual and agent updates stay one source of truth; a cancel leaves it as it was.
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

    // Context-menu "Resume later…" (AC-231): schedules one prompt for this session at a moment of the operator's
    // choosing, the route that does not start from a warning. Silently unavailable where nothing can be scheduled.
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


    // Context-menu Clear status (AC-32): wipe this session's status line, the same as the MCP setting it to empty.
    [RelayCommand]
    private void ClearSessionStatus(SessionPanelViewModel session) => session.Statusline = string.Empty;

    // The reorder lands in `_sidebarOrder`, never in `Sessions`: the session grid binds to `Sessions` and keeps its own
    // positional cell layout, so touching that collection would rebuild panes and drag the grid tiles along with the
    // strip — the very coupling this separation removes (AC-115).
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

    // AC-674: WorkspaceId is stamped before the pane write, since the write's Settings change synchronously
    // triggers RefreshPaneVisibility — stamping after would leave the grid a step behind.
    [RelayCommand]
    private async Task MoveSessionToWorkspaceAsync((SessionPanelViewModel Session, string TargetWorkspaceId) move)
    {
        var (session, targetWorkspaceId) = move;
        if (session.WorkspaceId == targetWorkspaceId)
        {
            return;
        }

        var sourceWorkspaceId = session.WorkspaceId;
        session.WorkspaceId = targetWorkspaceId;

        if (!await Workspaces.MoveSessionPaneToWorkspaceAsync(sourceWorkspaceId, session.PaneId, targetWorkspaceId))
        {
            // The pane write refused (target vanished, wrong kind): put the live side back rather than leave a
            // session whose desk and pane record disagree.
            session.WorkspaceId = sourceWorkspaceId;
        }
    }

    // Context-menu Move up: shift the session one place earlier in the sidebar order.
    [RelayCommand]
    private void MoveSessionUp(SessionPanelViewModel session)
    {
        var index = VisibleSessions.ToList().IndexOf(session);
        if (index > 0)
        {
            MoveSessionToVisibleIndex(session, index - 1);
        }
    }

    // Context-menu Move down: shift the session one place later in the sidebar order.
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

    // Context-menu Duplicate: start a new session with the same profile/model/mode as this one (≈ Fork).
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

    // Context-menu Clear context (AC-564): drops what the agent remembers and carries the pane itself over unchanged.
    [RelayCommand]
    private async Task ClearSessionContextAsync(SessionPanelViewModel session)
    {
        if (session is not SessionViewModel sdkSession || sdkSession.LaunchResult is not { } result)
        {
            return;
        }

        // Not undoable and a misclick mid-task costs half a session, so it is confirmed first (decision 2) — and
        // the confirmation says the part the operator would otherwise only find out afterwards: from here on this
        // pane is a different conversation, and resuming by the old id reaches the old one.
        if (!await ConfirmAsync(
            "Clear context",
            $"'{session.Title}' forgets this conversation and starts a new one. This cannot be undone.\n\n"
            + "The transcript stays, with a line marking where the agent's memory stops. Nothing is deleted: the "
            + "conversation so far keeps its own id and stays resumable — but from here this session is a new "
            + "conversation with a new id.",
            confirmLabel: "Clear context"))
        {
            return;
        }

        await sdkSession.ClearContextAsync(result.Profile);
    }

    // Used to open the standalone ManageProfilesDialog window; that window still exists for the New-session dialog's
    // own "manage profiles" link, but this — the menu item and ShortcutAction.ManageProfiles alike — now deep-links
    // into Options instead, so there is one place for the setting rather than two ways to reach it (AC-1001).
    [RelayCommand]
    private Task ManageProfilesAsync() => _ShowOptionsAsync("profiles");

    // The command lives here rather than on `AssistantOptionsViewModel` because the dialog it opens needs
    // `IAssistantSessionHost` for its restart button, and that host is constructed from this view model — so injecting
    // it into the dialog service (which this view model already depends on) would be a cycle.
    [RelayCommand]
    private async Task EditAssistantProfileAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAssistantProfileDialogAsync(AssistantHost);

        // The dialog can rename the record or move it to another provider, and this page shows both. Re-read rather
        // than have the dialog push a value back: one loader, and it is the one that already runs on open.
        await AssistantOptions.RefreshAsync();
    }

    // The living assistant, handed in by the app at startup for the same reason `ScheduledResumes` is:
    // it is built from this view model, so it cannot also be a constructor argument of it. Null in the test and
    // design-time graphs, where the profile editor simply offers no restart.
    public IAssistantSessionHost? AssistantHost { get; set; }

    // Opens Options on the MCP Servers category (AC-1002) from the sidebar menu, independent of creating a
    // session — the same deep-link split ManageProfilesAsync above uses. The standalone McpServersDialog window
    // this replaced is gone entirely now (AC-1006): nothing else built it any more.
    [RelayCommand]
    private Task OpenMcpServersAsync() => _ShowOptionsAsync("mcp-servers");

    // Opens the Verify-runners dialog (AC-86) from the sidebar to register the per-project command the visual verify
    // loop may run.
    [RelayCommand]
    private async Task OpenVerifyRunnersAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowVerifyRunnersDialogAsync();
    }

    // Opens the Options dialog (#13) from the sidebar, passing this view model as its DataContext.
    [RelayCommand]
    private Task OptionsAsync() => _ShowOptionsAsync();

    // Opens the projects manager (AC-161) — its own window, not a corner of Options.
    [RelayCommand]
    private async Task ManageProjectsAsync()
    {
        if (_dialogService is not null)
        {
            await _dialogService.ShowProjectsDialogAsync(Projects);
        }
    }

    // Brings the projects overview to the front, opening it when it is not there (AC-162) — the sidebar's way in,
    // so reaching it is not a matter of knowing that a workspace type exists and finding it in the "+" menu.
    [RelayCommand]
    private Task OpenProjectsWorkspaceAsync() => Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

    private async Task _ShowOptionsAsync(string? category = null)
    {
        if (_dialogService is null)
        {
            return;
        }

        _optionsOpenMeasurement = new OptionsOpenMeasurement();
        await _RefreshAudioDevicesAsync();
        _MarkOptionsOpen("audio");
        await Plugins.LoadAsync();
        _MarkOptionsOpen("plugins");

        // Refreshed before BeginOptionsEdit (inside ShowOptionsDialogAsync) takes its fingerprint, so a profile
        // added or edited from elsewhere since the app started is what Cancel reverts to, not stale startup state.
        if (Profiles is not null)
        {
            await Profiles.LoadAsync();
            _MarkOptionsOpen("profiles");
        }

        if (McpServers is not null)
        {
            await McpServers.LoadAsync();
            _MarkOptionsOpen("mcp");
        }

        await _dialogService.ShowOptionsDialogAsync(this, category);
    }

    internal void OptionsOpenPresented()
    {
        if (_optionsOpenMeasurement is not { } measurement)
        {
            return;
        }

        _MarkOptionsOpen("presented");
        if (measurement.Finish() is { } line)
        {
            _logger?.LogWarning("{Message}", line);
        }

        _optionsOpenMeasurement = null;
    }

    private void _MarkOptionsOpen(string phase) => _optionsOpenMeasurement?.Mark(phase);

    // Opens the plugin store dialog (#62) with the "Available updates" filter preselected (#65) — the action
    // button on a plugin-update toast, so the operator lands straight on the updates list instead of
    // the full Options→Plugins tab. Skips the audio-device refresh `OptionsAsync` does since it is irrelevant here.
    public async Task OpenPluginStoreUpdatesAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await Plugins.LoadAsync();
        await _dialogService.ShowPluginStoreDialogAsync(Plugins, PluginStoreFilter.UpdatesAvailable);
    }

    // Opens the plugin store from the sidebar (AC-76) — on the Updates filter when updates are waiting (the sidebar
    // badge is showing), so a click on the "N updates" indicator lands straight on them; otherwise the normal browse.
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

    // Opens the About dialog (#46) from the sidebar: app name, version, description and links.
    [RelayCommand]
    private async Task AboutAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAboutDialogAsync();
    }

    // Opens the guide in the operator's browser (AC-512) — the guide's content lives on the website, not in the
    // app. Honest about the one thing this cannot know: whether that browser can actually reach it. When it
    // cannot even start (no default browser, a locked-down machine), says so rather than opening nothing.
    [RelayCommand]
    private async Task OpenGuideAsync()
    {
        if (_tryOpenExternalLink(CockpitBrand.GuideUrl) || _dialogService is null)
        {
            return;
        }

        await _dialogService.ShowConfirmationDialogAsync(
            "Can't open your browser",
            $"{CockpitBrand.ProductName} could not open your browser to show the guide. It lives online at "
            + $"{CockpitBrand.GuideUrl} — visit it once you have a browser and a connection.",
            "OK");
    }

    // Shows the in-app glossary (AC-512): the five primitives, explained without a browser — the guide's own
    // depth stays on the website, this is what still answers something when that site is unreachable (AC-510).
    [RelayCommand]
    private async Task ShowGlossaryAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowGlossaryDialogAsync();
    }

    // Opens the knowledge base (AC-1033) — the app's own pages and every installed plugin's, in one window
    // that stays beside whatever you were doing. The first of the two doors to it; the other one is the
    // Documentation link on a plugin's own page in Options, where the question tends to actually come up.
    [RelayCommand]
    private void ShowDocumentation() => _help?.Open();

    // Reopens the first-run wizard (AC-512) from the Help menu — a no-op without one wired up.
    [RelayCommand]
    private Task RunSetupAgainAsync() => _firstRunWizard?.ShowAsync() ?? Task.CompletedTask;

    // Opens the delegated-tasks view (#67): the work other sessions handed to a profile. Those tasks run as
    // sessions with no tab of their own, so this is where they stay visible — and stoppable.
    [RelayCommand]
    private async Task ShowDelegatedTasksAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowDelegatedTasksDialogAsync();
    }

    // The desk the selected session is on, and the agent panes sharing it (AC-397) — the same answer
    // `WorkspaceAgentGateway` gives an agent asking who its neighbours are, worked out here through the same
    // `SessionWorkspacePlacement` rule because depending on that gateway from this view model is the cycle it is built
    private AgentLineDesk? _SelectedSessionDesk()
    {
        if (SelectedSession is not { ShowPluginHeaderItems: true } selected)
        {
            return null;
        }

        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(Workspaces.Settings);
        if (SessionWorkspacePlacement.Resolve(selected, firstSessionsWorkspaceId) is not { } workspaceId)
        {
            return null;
        }

        return new AgentLineDesk(
            workspaceId,
            AllSessions()
                .Where(candidate => candidate.ShowPluginHeaderItems
                    && SessionWorkspacePlacement.Resolve(candidate, firstSessionsWorkspaceId) == workspaceId)
                .Select(candidate => candidate.PaneId)
                .ToHashSet(StringComparer.Ordinal));
    }

    // Opens the agent-line inspector (AC-397). The operator is not in the message path, so without this the traffic
    // between agents on their own desk is invisible to them — including the wakes one agent asked for on another's
    // session, and the refusals nobody was told about.
    [RelayCommand]
    private async Task ShowAgentLineAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _dialogService.ShowAgentLineInspectorDialogAsync(AgentLineInspector);
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

    // Opens the command palette (#: command palette): a searchable list of every app action and plugin command with its
    // shortcut.
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

        // One entry per widget rather than a single "Add widget" that reopens the gallery: the palette is a search box,
        // so naming the widget in it is the whole point — you type "clock" and it is placed, which is one step where
        // the gallery is two.
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

    // Persists every options section in one go — the Options dialog's single footer Save (#13)
    // replaces the six per-section Save buttons the flyout used to have.
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
    }

    // What makes that safe to take back is that none of it reaches `cockpit.json` while the dialog is open:
    // `_optionsStaged` holds off the handful of settings that otherwise persist the moment they change, and Cancel then
    // re-seeds from a file nobody wrote to (AC-999).
    private bool _optionsStaged;

    // Taken when the dialog opens, compared against on every change to answer `HasPendingOptionChanges`.
    private string _optionsFingerprintAtOpen = string.Empty;

    // The update section is the one Cancel cannot recover from the store: whether a channel was chosen *at all*
    // is itself state (AC-387), and with nobody having chosen, the displayed channel comes from the running
    // build rather than from disk. So it is remembered outright instead of reloaded.
    private (bool StartupChoiceMade, bool ChannelChoiceMade, UpdateChannel? Chosen, bool CheckOnStartup, bool Nightly)
        _updateChoicesAtOpen;

    public void BeginOptionsEdit()
    {
        if (_optionsStaged)
        {
            return;
        }

        _optionsStaged = true;
        Security.SuspendPersistence = true;
        AssistantOptions.SuspendPersistence = true;
        _updateChoicesAtOpen =
            (_startupChoiceMade, _channelChoiceMade, _chosenChannel, CheckForUpdatesOnStartup, IncludeNightlyBuilds);
        _optionsFingerprintAtOpen = OptionsStaging.Fingerprint(this);
        HasPendingOptionChanges = false;
        PropertyChanged += _OnStagedPropertyChanged;
        Security.PropertyChanged += _OnStagedPropertyChanged;
        AssistantOptions.PropertyChanged += _OnStagedPropertyChanged;
        _RebuildPluginOptionsRows();
    }

    // One row per plugin that has ever registered a settings view (`PluginRowViewModel.HasSettings`, backed by the
    // persisted settings registry — true even for a plugin disabled this session, which never called `AddSettings` at
    // all) (AC-1005).
    private void _RebuildPluginOptionsRows()
    {
        // `ShowOptionsDialogAsync` calls `BeginOptionsEdit` unconditionally even when the dialog is merely activated
        // rather than recreated (a second gear clicked while Options is already open) — guarded so that never discards
        // the views the operator may already be mid-edit on.
        if (PluginOptionsRows.Count > 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Plugins.Plugins.Where(row => row.HasSettings))
        {
            seen.Add(row.FolderId);
            if (PluginSettings.TryGetValue(row.FolderId, out var registration))
            {
                PluginOptionsRows.Add(new PluginOptionsRowViewModel(row.FolderId, row.DisplayName, registration.CreateView, unavailableReason: null, registration.Category));
            }
            else
            {
                var reason = row.HasFailure ? $"{row.StatusText} — {row.FailureText}" : row.StatusText;
                PluginOptionsRows.Add(new PluginOptionsRowViewModel(row.FolderId, row.DisplayName, createView: null, reason));
            }
        }

        // A plugin that registered a settings view this session but the manager has not (also) discovered —
        // the design-time/test graphs build a CockpitViewModel with no plugin discovery wired at all, so without
        // this a plugin that just called AddSettings would silently get no row.
        foreach (var (pluginId, registration) in PluginSettings)
        {
            if (!seen.Add(pluginId))
            {
                continue;
            }

            PluginOptionsRows.Add(new PluginOptionsRowViewModel(pluginId, registration.PluginName, registration.CreateView, unavailableReason: null, registration.Category));
        }

    }

    // Any property on any of the three may be the one that moved, so the fingerprint decides rather than the
    // name — which also means a value put back by hand correctly stops reporting as pending.
    private void _OnStagedPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshPendingOptionChanges();

    // The two lists the dialog edits — the shortcut rows and the usage thresholds — raise their changes on the
    // rows, which nothing here is subscribed to. The footer's indicator therefore lags behind an edit to one of
    // those until something else moves, so the close guard asks again rather than trusting the flag it can see.
    public bool RefreshPendingOptionChanges()
    {
        HasPendingOptionChanges = _optionsStaged && OptionsStaging.Fingerprint(this) != _optionsFingerprintAtOpen;

        return HasPendingOptionChanges;
    }

    public bool ShouldConfirmOptionDiscard() => RefreshPendingOptionChanges();

    // Writes everything the dialog holds, in the order the five `_optionsStaged` writers would have run had
    // they never been staged. AC-1082: used to stop at the first section that refused, blocking every other
    // category too — now every section is attempted, what validates commits, refusers are reported together.
    [RelayCommand]
    private async Task ApplyOptionsAsync()
    {
        OptionsApplyBlocked = false;
        PluginSettingsError = null;
        OptionsApplyBlockedCategoryTag = null;

        var failures = new List<(string Label, string Reason, string CategoryTag)>();

        // `TryStage` only checks fields and hands back the write, so a refusal here leaves nothing staged for that
        // row — the rows that did pass still get committed below.
        var pluginStaging = new PluginSettingsStaging();
        foreach (var row in PluginOptionsRows)
        {
            if (row.RawView is not IPluginSettingsView settingsView)
            {
                continue;
            }

            if (!pluginStaging.TryStage(settingsView, () => ((IPluginContributionSink)this).NotifySettingsSaved(row.PluginId), out var error))
            {
                failures.Add((row.DisplayName, error, $"plugin:{row.PluginId}"));
            }
        }

        // `PersistAsync` validates internally and leaves the store untouched on a refusal, so calling it
        // unconditionally here — rather than gating it behind a separate `Validate()` pre-check — is what lets a
        // Profiles refusal stay contained to Profiles instead of blocking MCP Servers and the plugin rows too.
        if (Profiles is not null && !await Profiles.PersistAsync())
        {
            failures.Add(("Profiles", Profiles.StatusMessage, _ProfilesCategoryTag));
        }

        if (McpServers is not null && !await McpServers.PersistAsync())
        {
            failures.Add(("MCP Servers", McpServers.StatusMessage, "mcp-servers"));
        }

        if (failures.Count > 0)
        {
            PluginSettingsError = string.Join(" · ", failures.Select(failure => $"{failure.Label}: {failure.Reason}"));
            OptionsApplyBlockedCategoryTag = failures[0].CategoryTag;
            OptionsApplyBlocked = true;
        }

        // AC-1108: Commit() below re-commits every plugin's settings, not only the tab opened — measured 51+
        // separate cockpit.json writes here on top of SaveAllSettingsAsync's thirteen; batched to one round-trip.
        await using (CockpitConfigWriteBatch.Begin())
        {
            pluginStaging.Commit();

            // Left running when blocked: `_EndOptionsEdit` clears `PluginOptionsRows`, which is exactly the row the
            // operator still needs to fix and retry — ending the edit here would make a second Apply silently skip
            // it instead of validating it again.
            if (!OptionsApplyBlocked)
            {
                _EndOptionsEdit();
            }

            await SaveAllSettingsAsync();
        }

        _SaveUpdateSettings();

        if (_delegationMcpToggle is { } toggle)
        {
            await toggle.SetMcpEnabledAsync(OrchestratorMcpEnabled);
        }

        await Security.SaveStagedAsync();
        await AssistantOptions.SaveStagedAsync();

        // AC-233 used to write these after the dialog had closed, which is a path with no Cancel on it.
        if (UsageThresholdSettings is { } thresholds)
        {
            await thresholds.SaveAsync();
            UsageThresholds = await thresholds.ReloadAsync();
        }

        _RebaselineFingerprintAfterBlockedApply(failures);
    }

    // The only refuser whose staged values the fingerprint covers, so the only one that may leave it dirty below.
    private const string _ProfilesCategoryTag = "profiles";

    // A blocked Apply keeps the edit open, so the fingerprint still holds the value taken at open and the footer
    // goes on calling settings this click just wrote unsaved — with ✕ offering to discard what Cancel can now only
    // reload straight back (AC-1078). Refused profile rows are the exception: those really did stay unwritten.
    private void _RebaselineFingerprintAfterBlockedApply(List<(string Label, string Reason, string CategoryTag)> failures)
    {
        var profilesRefused = failures.Any(failure => failure.CategoryTag == _ProfilesCategoryTag);
        if (!OptionsApplyBlocked || profilesRefused)
        {
            return;
        }

        _optionsFingerprintAtOpen = OptionsStaging.Fingerprint(this);
        RefreshPendingOptionChanges();
    }

    // Puts the cockpit back exactly as the dialog found it. The flag stays up across the whole re-seed: these
    // load paths assign the very properties whose change handlers persist, and dropping it first would write the
    // old values back out — a Cancel that saves.
    [RelayCommand]
    private async Task CancelOptionsAsync()
    {
        await LoadNotificationSettingsAsync();
        await LoadTranscriptDisplaySettingsAsync();
        await LoadUsagePillSettingsAsync();
        await LoadSessionBehaviorSettingsAsync();
        await LoadScreenshotSettingsAsync();
        await LoadLayoutSettingsAsync();
        await LoadVoiceSettingsAsync();
        await LoadTerminalSettingsAsync();
        await LoadShortcutSettingsAsync();
        await LoadDebugSettingsAsync();
        await LoadRenderingSettingsAsync();
        await LoadWorktreeSettingsAsync();
        await LoadCloneSettingsAsync();
        _RevertUpdateSettings();
        OrchestratorMcpEnabled = _delegationMcpToggle?.McpEnabled ?? OrchestratorMcpEnabled;
        await Security.RefreshAsync();
        await AssistantOptions.RefreshAsync();

        UsageThresholdSettings?.Revert();

        // Re-fetched from the store rather than tracked in a buffer, same reasoning as every load call above it:
        // this puts back an edited field, an added row and a removed-but-not-yet-applied row alike, in one call.
        if (Profiles is not null)
        {
            await Profiles.LoadAsync();
        }

        if (McpServers is not null)
        {
            await McpServers.LoadAsync();
        }

        _EndOptionsEdit();
    }

    // Only what the dialog shows: `LayoutSettings` also carries the sidebar width and which dock panel is open, and
    // "restore defaults" on a settings screen is not an invitation to rearrange the window behind it.
    [RelayCommand]
    private void RestoreOptionDefaults()
    {
        var notifications = new NotificationSettings();
        LocalNotificationsEnabled = notifications.LocalEnabled;
        DiscordNotificationsEnabled = notifications.DiscordEnabled;
        WebhookUrl = notifications.WebhookUrl ?? string.Empty;
        IdleThresholdMinutes = (int)notifications.IdleThreshold.TotalMinutes;
        SessionIdleMinutes = (int)notifications.SessionIdleThreshold.TotalMinutes;
        NotifyOnSessionFinished = notifications.NotifyOnSessionFinished;
        NotifyOnSessionIdle = notifications.NotifyOnSessionIdle;
        NotifyWhenAllSessionsIdle = notifications.NotifyWhenAllSessionsIdle;
        NotifyOnCiFailure = notifications.NotifyOnCiFailure;

        ShowTimestamps = new TranscriptDisplaySettings().ShowTimestamps;

        var usagePill = new UsagePillSettings();
        ShowUsagePillContext = usagePill.VisibleFields.Contains(UsagePillField.Context);
        ShowUsagePillSessionUsage = usagePill.VisibleFields.Contains(UsagePillField.SessionUsage);
        ShowUsagePillRateWindows = usagePill.VisibleFields.Contains(UsagePillField.RateWindows);

        var behavior = new SessionBehaviorSettings();
        AutoCloseOnExit = behavior.AutoCloseOnExit;
        CombineQueuedMessages = behavior.CombineQueuedMessages;
        WakeAgentsByDefault = behavior.WakeAgentsByDefault;
        MemoryBudgetPercent = behavior.MemoryBudgetPercent;

        var screenshot = new ScreenshotSettings();
        ScreenshotGlobalHotkeyEnabled = screenshot.GlobalHotkeyEnabled;
        ScreenshotHotkeyKeyName = screenshot.HotkeyKeyName;
        ScreenshotPreviewEnabled = screenshot.PreviewEnabled;

        var layout = new LayoutSettings();
        GlobalSingleSessionLayout = layout.SingleSessionLayout;
        GlobalStackSessionsVertically = layout.StackSessionsVertically;
        GlobalFocusRailLayout = layout.FocusRailLayout;
        MinimizeToTrayOnClose = layout.MinimizeToTrayOnClose;

        var voice = new VoiceSettings();
        VoiceEnabled = voice.IsEnabled;
        VoiceModelName = voice.ModelName;
        _transcriptionModelAuto = voice.ModelAutoSelected;
        _SyncTranscriptionModelFromName();
        SelectedVoiceBackendPreference = VoiceBackendPreferences.FirstOrDefault(option => option.Value == voice.BackendPreference)
                                         ?? VoiceBackendPreferences[0];
        _UpdateTranscriptionAdvice();
        VoicePushToTalkKeyName = voice.PushToTalkKeyName;
        VoiceGlobalPushToTalk = voice.GlobalPushToTalk;
        VoiceAutoSubmit = voice.AutoSubmitAfterVoice;
        VoiceOpenMicSilenceTimeoutMs = voice.OpenMicSilenceTimeoutMs;
        VoiceStopReadAloudWhenSpeaking = voice.StopReadAloudWhenSpeaking;
        VoiceStopReadAloudLevelThreshold = (decimal)voice.StopReadAloudLevelThreshold;
        VoiceTtsSpeed = (decimal)voice.TtsSpeed;
        SelectedTtsVoice = TtsVoices.FirstOrDefault(item => item.Sid == voice.TtsVoiceSid) ?? TtsVoiceCatalog.Default;
        SelectedReadAloudLanguage = ReadAloudLanguages.FirstOrDefault(language => language.Code == voice.ReadAloudLanguage) ?? ReadAloudLanguages[0];
        SelectedSttLanguage = SttLanguages.FirstOrDefault(language => language.Code == voice.SttLanguage) ?? SttLanguages[0];
        SelectedInputDevice = InputDevices.Count > 0 ? InputDevices[0] : SelectedInputDevice;
        SelectedOutputDevice = OutputDevices.Count > 0 ? OutputDevices[0] : SelectedOutputDevice;

        var terminal = new TerminalSettings();
        TerminalFontFamily = terminal.FontFamily;
        TerminalFontSize = terminal.FontSize;
        SyncTerminalFontSelectionFromFamily();
        _BuildTerminalShellChoices(terminal.Shell);

        _shortcutSettings = ShortcutSettings.Default;
        _RebuildShortcutRows();

        var debug = new DebugSettings();
        ShowDebugControls = debug.ShowDebugControls;
        LogDiagnosticSnapshots = debug.LogDiagnosticSnapshots;
        OrchestratorMcpEnabled = true;

        RenderBackendSelection = RenderBackendLabel(new RenderingSettings().Backend);
        WorktreeRoot = new WorktreeSettings().Root ?? string.Empty;
        CloneRoot = new CloneSettings().Root ?? string.Empty;

        // `UpdateSettings.Channel` defaults to "nobody has chosen", which is not a channel but an absence — the
        // running build decides then (AC-387). So this puts the record of a choice back to none as well as the
        // switch it drove, or the next save would persist a stable/nightly nobody picked.
        var updates = new UpdateSettings();
        _loadingUpdateSettings = true;
        try
        {
            CheckForUpdatesOnStartup = updates.CheckOnStartup;
            IncludeNightlyBuilds = _updates is { } service
                && BuildChannel.FromVersion(service.Current.Version) == UpdateChannel.Nightly;
        }
        finally
        {
            _loadingUpdateSettings = false;
        }

        _startupChoiceMade = false;
        _channelChoiceMade = false;
        _chosenChannel = updates.Channel;

        Security.RestoreDefaults();
        AssistantOptions.RestoreDefaults();
        UsageThresholdSettings?.RestoreDefaults();
    }

    private void _EndOptionsEdit()
    {
        PropertyChanged -= _OnStagedPropertyChanged;
        Security.PropertyChanged -= _OnStagedPropertyChanged;
        AssistantOptions.PropertyChanged -= _OnStagedPropertyChanged;
        _optionsStaged = false;
        Security.SuspendPersistence = false;
        AssistantOptions.SuspendPersistence = false;
        HasPendingOptionChanges = false;

        // Drops the views this session created — a fresh `CreateView()` next open is Cancel's revert, and there
        // is nothing left here worth holding onto once the dialog is gone either way.
        PluginOptionsRows.Clear();
    }

    private void _RevertUpdateSettings()
    {
        var (startupChoiceMade, channelChoiceMade, chosen, checkOnStartup, nightly) = _updateChoicesAtOpen;
        _startupChoiceMade = startupChoiceMade;
        _channelChoiceMade = channelChoiceMade;
        _chosenChannel = chosen;

        _loadingUpdateSettings = true;
        try
        {
            CheckForUpdatesOnStartup = checkOnStartup;
            IncludeNightlyBuilds = nightly;
        }
        finally
        {
            _loadingUpdateSettings = false;
        }
    }

    // Named, this is deliberately *three* departures from that and not one, because a spawn onto a desk that is not on
    // screen must not move the operator (AC-545).
    private void AddSession(
        SessionPanelViewModel session, string? name, string profileLabel, bool nameIsChosen = false, string? targetWorkspaceId = null)
    {
        _sessionCounter++;
        // Started while only a dashboard exists, it would otherwise run on a desk that cannot show it — invisible
        // rather than absent, which is the worse of the two.
        session.WorkspaceId = targetWorkspaceId ?? Workspaces.EnsureSessionWorkspace();
        // A friendly name from the dialog wins; otherwise fall back to "<profile> - <N>" so the sidebar
        // shows which profile — and therefore which provider — each session runs under. Whether that name is one
        // somebody meant is not worked out here: NewSessionResult.NameIsChosen says so, and this applies it (#AC-324).
        session.Title = string.IsNullOrWhiteSpace(name) ? $"{profileLabel} - {_sessionCounter}" : name.Trim();
        session.HasGeneratedName = !nameIsChosen;
        _AttachSession(session);

        if (targetWorkspaceId is null)
        {
            SelectedSession = session;
        }
    }

    // Shared by a freshly started session (`AddSession`) and one brought back after a restart
    // (`_AttachRestoredSession`) — the two differ only in how `SessionPanelViewModel.WorkspaceId`, the title and
    // selection are decided, which is why those stay in the callers.
    private void _AttachSession(SessionPanelViewModel session)
    {
        _SeedSessionPreferences(session);

        session.CloseRequested += OnSessionCloseRequested;
        // AC-410: harmless for a freshly started session — RestoreOffer stays null, so nothing on the banner can
        // ever raise this — and is what lets a restored one's "Resume"/"Start fresh" reach the cockpit.
        session.RestoreDecided += OnSessionRestoreDecided;
        // AC-514: a suggested name or an inline rename after the pane already exists — without this the persisted
        // pane kept whatever title the session was created with, so it came back unchanged after a restart.
        session.NameChanged += OnSessionNameChanged;

        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;
        // AC-613: admission records presence; cockpit-agents records tool contact separately.
        _agentCoordinator?.Enroll(session.PaneId);

        Sessions.Add(session);
    }

    // Guarded on HasPersistedPane, which _PersistNewSessionPane/_AttachRestoredSession set synchronously before
    // Title/HasGeneratedName can ever change — so this only ever skips a session with no pane record at all (a plain
    // terminal), never races the pane's own creation write (AC-514).
    private void OnSessionNameChanged(object? sender, EventArgs e)
    {
        if (sender is not SessionPanelViewModel { HasPersistedPane: true } session)
        {
            return;
        }

        _ = Workspaces.RenamePaneAsync(session.WorkspaceId, session.PaneId, session.Title, !session.HasGeneratedName);
    }

    // `workspaceId` is set directly rather than through `Workspaces`' `EnsureSessionWorkspace`, which would switch the
    // operator to that desk; restoring a pane on a workspace must not activate it (AC-410).
    private void _AttachRestoredSession(SessionPanelViewModel session, string workspaceId, WorkspacePane pane)
    {
        session.WorkspaceId = workspaceId;
        session.Title = string.IsNullOrWhiteSpace(pane.Title) ? "Session" : pane.Title;
        session.HasGeneratedName = !pane.NameIsChosen;
        session.ProjectId = pane.ProjectId;
        session.HasPersistedPane = true;
        _AttachSession(session);
    }

    // The `WorkspacePane` record for a just-started AI session (AC-410) — the operator's *intention*: which profile and
    // kind it runs under, and the folder it was asked to run in, before isolation may have moved it into a worktree.
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

    // Persists `session`'s pane record right after `AddSession` — deliberately before `_StartSessionAsync` runs, not
    // after: a crash in between leaves at most one config write, so the worst case is a pane that never comes back, not
    // one that comes back describing a session that never actually started this way (AC-410).
    private void _PersistNewSessionPane(SessionPanelViewModel session, NewSessionResult result)
    {
        session.HasPersistedPane = true;
        _ = Workspaces.AddPaneAsync(session.WorkspaceId, _BuildSessionPane(session, result));
    }

    // AC-410: the restore plan composed for each pane brought back this run, kept by pane id — read by the banner
    // (SessionPanelViewModel.RestoreOffer, set from here) and again by _StartRestoredSessionAsync once the
    // operator picks a start, so the plan is composed exactly once per pane per run.
    private readonly Dictionary<string, SessionRestorePlan> _restorePlans = new(StringComparer.Ordinal);

    // AC-410: the working directory a restored pane actually starts in, resolved once here from the worktree registry
    // rather than left to the start path — the restore path runs with IsolateInWorktree: false (see
    // _BuildRestoreLaunchResult), so _ResolveIsolatedWorkingDirectoryAsync never gets a chance to look this up itself.
    private readonly Dictionary<string, string?> _restoreWorkingDirectories = new(StringComparer.Ordinal);

    // Waits on `IWorktreeReconcileGate` first: `Program.cs` starts the startup worktree reconcile fire-and-forget so it
    // never delays the window, and without this wait an operator who accepts a restore offer within about a second of
    // launch could race the reconcile into removing the very worktree the offer is about to reattach (AC-410).
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

        IReadOnlyList<SessionStateRecord>? loadedStates;
        try
        {
            // AC-513: TryLoadAsync, not LoadAsync — this result also feeds Seed() below, and LoadAsync's own
            // "unreadable looks like empty" collapse is exactly wrong for that call: an empty Seed() latches the
            // recorder onto a blank cache for the rest of the process (Seed only runs once; see its own doc), so a read
            loadedStates = await _sessionStateStore.TryLoadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // ISessionStateStore.TryLoadAsync's own contract says it never throws, but a restore that somehow
            // still fails here must not take the rest of startup down with it — nothing restores this run, same
            // as an empty state file. Logged so "no panes came back" leaves a trail rather than silence.
            _logger?.LogWarning(exception, "Could not load session state; no AI-session panes will be restored this run.");
            return;
        }

        // The pane-restore loop below has no fallback beyond "no saved state" either way, so a read failure and a
        // genuinely empty file are the same answer for it — same reasoning LoadAsync itself uses, just applied
        // here instead of inside the store.
        var states = loadedStates ?? [];

        if (loadedStates is not null)
        {
            // Skipped entirely on a read failure: Seed() latches _seedTask on whatever it is handed, so seeding it with
            // an empty list here — indistinguishable from "no saved state" — would permanently blind the write path's
            // own self-heal to a file that might still be readable moments later, the same loss criterion (AC-513).
            _sessionStateRecorder?.Seed(loadedStates);
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

            if (string.Equals(pane.Id, Cockpit.Core.Assistant.AssistantIdentity.PaneId, StringComparison.Ordinal))
            {
                // The check above cannot catch this case — the assistant is never in Sessions, so a workspace pane
                // claiming its id looks unused (AC-544).
                _logger?.LogWarning(
                    "A saved pane claims the assistant's reserved id '{PaneId}' and was not restored. Remove it from cockpit.json; the assistant owns that id.",
                    pane.Id);
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
                // One pane's restore failing (a planner it cannot compose against, a factory that throws) must not cost
                // every other pane its restore — the conservative outcome here is a pane that does not come back, not a
                // half-attached one or a startup that never finishes.
                _logger?.LogWarning(exception, "Could not restore the AI-session pane {PaneId}; it will not come back this run.", pane.Id);
            }
        }
    }

    // What a restored pane starts with once the operator accepts the offer (AC-410) — mirrors
    // `ProjectQuickStart.ComposeAsync`'s use of app-default mode/model/effort (the typed Claude vocabulary is
    // migration-only; there is no dialog here to have overridden them).
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
            IsolateInWorktree: false,
            ReadingLevel: isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId: pane.ProjectId)
        {
            NameIsComposed = !pane.NameIsChosen,
        };
    }

    // "Resume" resolves to `SessionResume.BySessionId` when the plan's saved state actually names a conversation id,
    // and to `SessionResume.New` otherwise (and always for "Start fresh") — the same fall-back
    // `_BuildRestoreLaunchResult` would otherwise silently need twice (AC-410).
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

    // Deliberately does not compose a fresh restore plan for a pane `RestoreSessionPanesAsync` never saw this run: a
    // pane closed on purpose already had its `WorkspacePane` record removed (`CloseSessionAsync`), so there is nothing
    // left to reopen it with, and reopening it anyway would second-guess the operator's own close (AC-290, AC-410).
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

    // A restore offer was resolved into a start (AC-410) — run the matching launch through the normal start path.
    private void OnSessionRestoreDecided(object? sender, SessionRestoreChoice choice)
    {
        if (sender is SessionPanelViewModel session)
        {
            _ = _StartRestoredSessionAsync(session, choice);
        }
    }

    // Seeds a freshly built session with the live global preferences it must start on — transcript display (T7),
    // usage-pill fields (AC-105), auto-close-on-exit (T10), diagnostic controls (#73), combine-queued-messages (AC-145,
    // SDK only), and, for a TTY, terminal appearance (#40) and stacked layout (#54).
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

    // Returns whether a live session matched; false is a no-op (the session may have closed), never an error (AC-13).
    public bool SetSessionStatusline(string paneId, string statusline)
    {
        if (FindSession(paneId) is not { } target)
        {
            return false;
        }

        target.Statusline = statusline ?? string.Empty;
        return true;
    }

    // The shape a ticket id takes everywhere this codebase already writes one by hand ("AC-13", "AC-544") — two or more
    // uppercase letters, a dash, digits — kept as the one pattern every brief-seeded statusline is read against
    // (AC-544) rather than reinvented per caller (AC-251).
    private static readonly Regex _TicketIdPattern = new(@"^[A-Z]{2,}-\d+\b", RegexOptions.Compiled);

    // Lets a host-side spawn path seed a fresh session's statusline deterministically, from what it already knows,
    // instead of leaving the line blank until (or unless) the agent inside calls `set_status` itself (AC-544, AC-13).
    private static string? _TicketFromBrief(string? text) =>
        !string.IsNullOrWhiteSpace(text) && _TicketIdPattern.Match(text.TrimStart()) is { Success: true } match
            ? match.Value
            : null;

    // A session by its pane id, including embedded ones the grid deliberately does not list — so an embedded run's
    // own `set_status`, a plugin acting on its embedded pane, and a consent routed to it all reach it (AC-152),
    // not only grid sessions. Read the collections on the UI thread, like its callers do.
    public SessionPanelViewModel? FindSession(string paneId) =>
        _AllSessions().FirstOrDefault(session => session.PaneId == paneId);

    // Every session the host holds, grid and embedded together (AC-391): an embedded agent (an Autopilot step, a
    // plugin-run) is a full session with its own MCP token even though the grid deliberately never lists it, so a
    // caller enumerating "every agent" — the workspace-presence roster, say — must not miss it the way iterating
    public IEnumerable<SessionPanelViewModel> AllSessions() => _AllSessions();

    // Every session the host holds — the grid's, plus the embedded ones the grid deliberately does not list. The seam
    // the pane-id lookup searches, so an embedded pane is never half-reached. The assistant is *not* in here; consent
    // routing therefore reads <see cref="_ConsentPanes"/> instead, which adds it.
    private IEnumerable<SessionPanelViewModel> _AllSessions() =>
        Sessions.Concat(_embeddedSessions.Values.SelectMany(owned => owned));

    // Every pane a consent banner can be shown on: <see cref="_AllSessions"/> plus the assistant. One seam for both
    // the open and the close side — an open that reaches a pane the close cannot reach leaves PendingConsent set for
    // the life of the process, and the next request on that pane is denied without a card ever being shown.
    private IEnumerable<SessionPanelViewModel> _ConsentPanes() => _WithAssistant(_AllSessions());

    // `sessions` with the live assistant on the end.
    private IEnumerable<SessionPanelViewModel> _WithAssistant(IEnumerable<SessionPanelViewModel> sessions) =>
        _assistantSession is { } assistant ? sessions.Append(assistant) : sessions;

    // The persisted `WorkspacePane` title goes in first and a live pane's title overwrites it: the persisted one
    // survives the pane closing or crashing — which is exactly when the panel most needs a name instead of "a pane" —
    // while a live pane carries the title the operator sees right now (AC-520).
    private IReadOnlyDictionary<string, string> _SessionNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pane in Workspaces.Settings.Workspaces.SelectMany(workspace => workspace.Panes))
        {
            if (!string.IsNullOrWhiteSpace(pane.Title))
            {
                names[pane.Id] = pane.Title;
            }
        }

        foreach (var session in _AllSessions())
        {
            if (!string.IsNullOrWhiteSpace(session.Title))
            {
                names[session.PaneId] = session.Title;
            }
        }

        return names;
    }

    // The pane ids currently showing an open restore offer (AC-410), for the managed-worktrees panel's Release action
    // (AC-520 fix 6) — what tells apart a row that is "live" only because of that offer from one whose session is
    // genuinely doing something.
    private IReadOnlySet<string> _RestoreOfferPaneIds() =>
        _AllSessions().Where(session => session.HasRestoreOffer).Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);

    // Renames a session — the title in its header and sidebar — by its `SessionPanelViewModel.PaneId`
    // (#AC-13). A blank name is ignored. Returns whether a live session matched. Must be called on the UI thread.
    public bool SetSessionName(string paneId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Sessions.FirstOrDefault(session => session.PaneId == paneId) is not { } target)
        {
            return false;
        }

        target.SetNameDirectly(name);
        return true;
    }

    // Names a session the way `SetSessionName` does, but stands down when its name is one somebody chose (#AC-310) —
    // how linking a ticket to a running session labels it without erasing the name the operator typed (AC-152, AC-312).
    public bool SuggestSessionName(string paneId, string name) =>
        FindSession(paneId)?.SuggestName(name) ?? false;

    // Edge-triggered attention routing: fires the presence-aware notifier once, on the transition
    // into `SessionStatus.NeedsAttention` — not on every status touch while it stays
    // there. The notifier itself decides present-toast vs away-webhook.
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SessionPanelViewModel session)
        {
            return;
        }

        // The assistant's session feeds this handler only for status plumbing (AC-543), never for the
        // OS-level attention/finished toasts a real session gets (AC-735).
        if (ReferenceEquals(session, _assistantSession))
        {
            return;
        }

        // The last background shell ending is the moment a session that was withheld below actually becomes
        // finished (AC-276). Its status does not change then — it is already Done — so without this the
        // notification would not merely be delayed but lost for good, on every session that ran one.
        if (e.PropertyName == nameof(SessionPanelViewModel.HasOutstandingBackgroundShells))
        {
            // AC-1273: the same edge, read a second way — the moment from which a session that ended its turn to wait
            // for that shell has nothing left to wait for. Unmeasured on the TTY route: every measurement behind this
            // came from an SDK session, and the gap is not theirs alone. A shell starting disarms it again.
            session.BackgroundShellsEndedUtc = session.HasOutstandingBackgroundShells
                ? null
                : DateTimeOffset.UtcNow;

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

        // Worth saying out loud only when you are not looking at that session — the notifier makes that call, since it
        // is the one that knows whether you are even at the PC (AC-276).
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

    // Driven by a periodic sweep rather than a timer per session: one tick decides for all of them.
    internal void SweepIdleSessions(DateTimeOffset now)
    {
        var threshold = SessionIdleMinutes > 0 ? TimeSpan.FromMinutes(SessionIdleMinutes) : TimeSpan.Zero;

        foreach (var session in Sessions)
        {
            _SweepStrandedBackgroundTask(session, now);

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

    // AC-1273: what the cockpit says to a session left standing after its background shell finished. It asks for the
    // work to be picked up rather than only reporting a fact, because nothing else is going to reach that session —
    // and it names the gap as the cockpit's own reading, so the session does not go looking for a mistake it made.
    internal const string StrandedBackgroundTaskNotice =
        "Your last background shell finished, and no completion notification followed it. That is a gap the cockpit "
        + "has measured (AC-1273), not something you did wrong — but nothing else is going to tell you. Read that "
        + "task's output yourself, and pick up whatever you said you would do once it was done.";

    // AC-1273: says so when a session's background shell finished and nothing came of it. It rides the idle sweep's
    // own tick rather than a timer of its own: what it looks for is a session that has done nothing for a while,
    // which is exactly what that sweep already walks.
    private void _SweepStrandedBackgroundTask(SessionPanelViewModel session, DateTimeOffset now)
    {
        if (session.BackgroundShellsEndedUtc is not { } endedAt)
        {
            return;
        }

        if (StrandedBackgroundTaskDecision.IsStranded(
                session.SessionStatus is SessionStatus.Done or SessionStatus.Idle,
                endedAt,
                session.LastActivityUtc,
                now,
                StrandedBackgroundTaskDecision.DefaultGrace))
        {
            // Disarmed before the send, not after: one shell ending buys one notice, whatever becomes of it.
            session.BackgroundShellsEndedUtc = null;
            _ = _SendStrandedBackgroundTaskNoticeAsync(session);
            return;
        }

        // It moved on by itself — the provider's notification landed after all, or the operator got there first.
        // Disarmed rather than left standing, so what re-arms this is a later shell of its own and not a stale moment.
        if (session.LastActivityUtc > endedAt)
        {
            session.BackgroundShellsEndedUtc = null;
        }
    }

    // The poke itself, guarded the way a peer's urgent wake is (`WorkspaceAgentGateway._TryWake`): never over a
    // question already standing in front of the operator, never at a pane that cannot take a prompt at all. Not
    // awaited, for that path's own reason — an SDK send does not complete until the whole turn it starts does.
    private async Task _SendStrandedBackgroundTaskNoticeAsync(SessionPanelViewModel session)
    {
        if (session.PendingConsent is not null || !session.CanTakeAPrompt)
        {
            return;
        }

        try
        {
            await session.SendPromptAsync(StrandedBackgroundTaskNotice);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Session {Pane}: the notice about its finished background shell could not be sent.",
                session.PaneId);
        }
    }

    // AC-439: recomputes which panes currently collide across a workspace boundary and stamps
    // `SessionPanelViewModel.HasClaimCollision` on every one of them — an operator-only chip, never anything an agent's
    // tool result carries.
    internal async Task RefreshClaimCollisionsAsync()
    {
        if (_claimCollisionMonitor is null)
        {
            return;
        }

        var monitor = _claimCollisionMonitor;
        IReadOnlySet<string> colliding;
        try
        {
            colliding = await Task.Run(monitor.PanesInCollision).ConfigureAwait(true);
        }
        catch (UiUnavailableException exception)
        {
            // AC-1201: the UI thread was starved past PaneWorkspaceDirectory's own marshal deadline (AC-1138/
            // AC-1196) — expected under load, not a bug here. This round's chip is stale; the next tick tries again.
            _logger?.LogWarning(exception, "A claim-collision check was skipped: the UI thread did not answer in time.");
            return;
        }
        catch (Exception exception)
        {
            // Anything else is not the known starvation case, so it is worth a louder signal than the one above.
            _logger?.LogError(exception, "A claim-collision check failed; the next tick will try again.");
            return;
        }

        foreach (var session in AllSessions())
        {
            session.HasClaimCollision = colliding.Contains(session.PaneId);
        }
    }

    // A session asked to close itself (T10: an "exit" turn finished) — run the normal close flow.
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

    // Moves the selection to the previous session in `Sessions`, wrapping from the first
    // to the last. No-op when there are no sessions; selects the only session when there is exactly
    // one. Bound to the configurable `ShortcutAction.PreviousSession` shortcut (Ctrl+Shift+Up by default).
    [RelayCommand]
    public void SelectPreviousSession() => _StepSelection(-1);

    // Moves the selection to the next session in `Sessions`, wrapping from the last to
    // the first. No-op when there are no sessions. Bound to the configurable
    // `ShortcutAction.NextSession` shortcut (Ctrl+Shift+Down by default).
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

#if DEBUG
    // Leak simulation (dev-only): open a synthetic session, fill its transcript, realise it, then close it through the
    // real close path — so we can reproduce and measure the after-close row retention on demand (fired by a trigger
    // file, see CockpitView) instead of driving a real agent.
    internal async Task RunLeakSimAsync(int rows = 300)
    {
        if (_sessionFactory is null)
        {
            return;
        }

        var tempDir = System.IO.Path.GetTempPath();
        var resultPath = System.IO.Path.Combine(tempDir, "cockpit-leaksim.result");
        var holdMarker = System.IO.Path.Combine(tempDir, "cockpit-leaksim.holding");

        var before = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        // Register the fake provider and start a REAL session against it, through the same New-session path a real
        // Claude session uses (StartConfiguredAsync) — so this exercises the real runtime, activity ticker and
        // Focus "steps run" folding, not a design-ctor stand-in.
        var registry = (Cockpit.Infrastructure.Sessions.IPluginProviderRegistry)
            Program.Services.GetService(typeof(Cockpit.Infrastructure.Sessions.IPluginProviderRegistry))!;
        Cockpit.App.Diagnostics.LeakSimProvider.EnsureRegistered(registry);

        var profile = new SessionProfile("Leak Sim", new PluginProviderConfig(Cockpit.App.Diagnostics.LeakSimProvider.ProviderId, "{}"));
        var vm = _sessionFactory();
        AddSession(vm, null, profile.Label);
        await vm.StartConfiguredAsync(profile, new PermissionModeOption("Default", "default"), new ModelOption("Sonnet", "sonnet"), new EffortOption("Medium", "medium", 8000), null, tempDir, null, null, ReadingLevel.Focus);

        var driver = Cockpit.App.Diagnostics.LeakSimProvider.Current;
        if (driver is null)
        {
            await CloseSessionAsync(vm);
            return;
        }

        // Stream scripted agent feedback incrementally, like a real agent: thinking, assistant markdown, then a run
        // of consecutive auto tool calls (which fold into a "N steps run" group at Focus), turn after turn.
        driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginSessionInitialized { SessionId = "leaksim-1", Tools = ["Bash", "Read", "Edit"], Cwd = tempDir });

        var turns = Math.Max(1, rows / 8);
        var block = 0;
        for (var turn = 0; turn < turns; turn++)
        {
            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginAssistantThinkingDelta { SessionId = "leaksim-1", BlockIndex = block++, Thinking = $"Planning turn {turn}: what to change and why." });

            foreach (var chunk in new[]
            {
                $"## Step {turn}\n\nLet me **check** `src/File{turn}.cs` ",
                $"and a [link](https://example.com/{turn}).\n\n```csharp\nvar v{turn} = Compute({turn});\n```\n\n",
                $"- first {turn}\n- second {turn}\n\n| a | b |\n|---|---|\n| {turn} | {turn * 2} |\n",
            })
            {
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginAssistantTextDelta { SessionId = "leaksim-1", BlockIndex = block, Text = chunk });
                await Task.Delay(12);
            }

            block++;

            for (var k = 0; k < 6; k++)
            {
                var id = $"t{turn}_{k}";
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolUseRequested { SessionId = "leaksim-1", ToolUseId = id, ToolName = "Bash", InputJson = $"{{\"command\":\"build {id}\"}}" });
                await Task.Delay(8);
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolResult { SessionId = "leaksim-1", ToolUseId = id, Content = $"output for {id}\nRestored\nCompiled\nDone.", IsError = false });
                await Task.Delay(8);
            }

            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginTurnCompleted { SessionId = "leaksim-1", Subtype = "success", Result = null, IsError = false });
            await Task.Delay(20);
        }

        driver.Complete();
        await Task.Delay(600);   // let the last events drain + realise
        var realised = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();
        var footprint = _MeasureFootprint(vm);

        // Scroll up/down to force VirtualizingStackPanel container recycling — the real per-row build/teardown
        // churn, not a one-shot realise.
        await _ScrollSimAsync(vm);
        var afterScroll = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        // In-process peer walk (transient peers — a weaker stand-in for a real UIA client).
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            _BuildPeerTree(Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(mainWindow), 0);
        }
        var afterInProcPeers = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        // Hold with rows alive so an EXTERNAL Win32 UIA client (uia-walk.ps1, fired by the harness on this marker)
        // builds and holds the real automation-peer tree — the ingredient the in-process walk cannot reproduce.
        try { System.IO.File.WriteAllText(holdMarker, "hold"); } catch (Exception) { }
        await Task.Delay(15000);
        try { System.IO.File.Delete(holdMarker); } catch (Exception) { }
        var afterExtUia = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        await CloseSessionAsync(vm);
        await Task.Delay(800);
        var after = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        try
        {
            System.IO.File.AppendAllText(resultPath,
                $"[leaksim turns={turns}] BEFORE: {before} | REALISED: {realised} | FOOTPRINT: {footprint} | AFTER-SCROLL: {afterScroll} | AFTER-INPROC-PEERS: {afterInProcPeers} | AFTER-EXT-UIA: {afterExtUia} | AFTER-CLOSE: {after}\n");
        }
        catch (Exception)
        {
            // A diagnostic result file is a nicety, never worth failing the sim over.
        }
    }

    // App-driven measurement: a real Cockpit with real session pipelines, controlled through the DEBUG trigger file.
    internal async Task RunAppReproAsync(int sessionCount, int seconds, string shape, bool retainResults)
    {
        var root = Path.GetTempPath();
        var readyPath = Path.Combine(root, "app-repro.ready.json");
        var donePath = Path.Combine(root, "app-repro.done.json");
        try { File.Delete(readyPath); } catch (Exception) { }
        try { File.Delete(donePath); } catch (Exception) { }
        if (_sessionFactory is null)
        {
            return;
        }

        var requestedSessionCount = sessionCount;
        var normalizedShape = string.Equals(shape, "sdk-read-fallback", StringComparison.OrdinalIgnoreCase)
            ? "sdk-read-fallback"
            : string.Equals(shape, "growing-tail", StringComparison.OrdinalIgnoreCase) ? "growing-tail" : "new-rows";
        var sdkReadFallback = normalizedShape == "sdk-read-fallback";
        if (sdkReadFallback)
        {
            sessionCount = 4;
        }

        sessionCount = Math.Clamp(sessionCount, 1, 12);
        seconds = Math.Clamp(seconds, 1, 600);
        var registry = (Cockpit.Infrastructure.Sessions.IPluginProviderRegistry)
            Program.Services.GetService(typeof(Cockpit.Infrastructure.Sessions.IPluginProviderRegistry))!;
        Cockpit.App.Diagnostics.LeakSimProvider.EnsureRegistered(registry);
        var drivers = new List<Cockpit.App.Diagnostics.LeakSimDriver>();
        var sessionVms = new List<SessionViewModel>();
        for (var i = 0; i < sessionCount; i++)
        {
            var vm = _sessionFactory();
            Sessions.Add(vm);
            sessionVms.Add(vm);
            var profile = new SessionProfile($"App repro {i + 1}", new PluginProviderConfig(Cockpit.App.Diagnostics.LeakSimProvider.ProviderId, "{}"));
            await vm.StartConfiguredAsync(profile, new PermissionModeOption("Default", "default"), new ModelOption("Sonnet", "sonnet"), new EffortOption("Medium", "medium", 8000), null, root, null, null, ReadingLevel.Focus);
            var driver = Cockpit.App.Diagnostics.LeakSimProvider.Current;
            if (driver is null)
            {
                await CloseSessionAsync(vm);
                continue;
            }

            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginSessionInitialized { SessionId = "app-repro", Tools = ["Read"], Cwd = root });
            drivers.Add(driver);
        }

        File.WriteAllText(readyPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            requested = requestedSessionCount,
            started = drivers.Count,
            shape = normalizedShape,
            stateRoot = CockpitBuild.StateRoot
        }));

        var block = 0;
        var reachableBytes = new List<long>();
        var retainedControl = retainResults ? new List<string>() : null;
        if (sdkReadFallback)
        {
            for (var call = 1; call <= 20; call++)
            {
                foreach (var (driver, session) in drivers.Select((driver, index) => (driver, index + 1)))
                {
                    driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolUseRequested
                    {
                        SessionId = "app-repro",
                        ToolUseId = $"read-{session}-{call}-request",
                        ToolName = "Read",
                        InputJson = "{\"file_path\":\"ac1088-5mb.bin\"}"
                    });
                    // The result deliberately has no matching request: this is the AC-1088 fallback path.
                    driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolResult
                    {
                        SessionId = "app-repro",
                        ToolUseId = $"read-{session}-{call}-orphan",
                        Content = new string('r', 5 * 1024 * 1024),
                        IsError = false
                    });
                }

                if (retainedControl is not null)
                {
                    retainedControl.Add(new string('c', 5 * 1024 * 1024));
                }

                await Task.Delay(50);
                reachableBytes.Add(GC.GetTotalMemory(forceFullCollection: true));
            }
        }
        else
        {
            var growingTail = normalizedShape == "growing-tail";
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                block++;
                foreach (var driver in drivers)
                {
                    driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginAssistantTextDelta
                    {
                        SessionId = "app-repro",
                        BlockIndex = growingTail ? 0 : block,
                        Text = growingTail
                            ? $"{new string('x', 20 + (block % 17) * 60)} "
                            : $"Repro line {block} {new string('x', 20 + (block % 17) * 60)}\n"
                    });
                }

                await Task.Delay(50);
            }
        }

        var orphanedResultRows = sdkReadFallback
            ? sessionVms.Sum(vm => vm.Transcript.Count(entry => entry.Kind == TranscriptEntryKind.ToolResult))
            : 0;

        foreach (var driver in drivers)
        {
            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginTurnCompleted { SessionId = "app-repro", Subtype = "success", Result = null, IsError = false });
            driver.Complete();
        }

        File.WriteAllText(donePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            blocks = sdkReadFallback ? 20 : block,
            shape = normalizedShape,
            callsPerSession = sdkReadFallback ? 20 : 0,
            resultBytes = sdkReadFallback ? 5 * 1024 * 1024 : 0,
            orphanedResultRows,
            reachableBytes,
            positiveControl = retainResults
        }));
    }

    // Leak simulation for the ASSISTANT CHAT window (dev-only).
    internal async Task RunAssistantChatLeakSimAsync(int rows = 300)
    {
        if (_sessionFactory is null)
        {
            return;
        }

        var tempDir = System.IO.Path.GetTempPath();
        var resultPath = System.IO.Path.Combine(tempDir, "cockpit-leaksim.result");

        var before = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        var registry = (Cockpit.Infrastructure.Sessions.IPluginProviderRegistry)
            Program.Services.GetService(typeof(Cockpit.Infrastructure.Sessions.IPluginProviderRegistry))!;
        Cockpit.App.Diagnostics.LeakSimProvider.EnsureRegistered(registry);

        var profile = new SessionProfile("Chat Leak Sim", new PluginProviderConfig(Cockpit.App.Diagnostics.LeakSimProvider.ProviderId, "{}"));
        var vm = _sessionFactory();
        // Not added to Sessions on purpose: the grid must not render this session too, or its (now bounded) rows
        // would be counted alongside the chat window's and blur what this sim measures.
        await vm.StartConfiguredAsync(profile, new PermissionModeOption("Default", "default"), new ModelOption("Sonnet", "sonnet"), new EffortOption("Medium", "medium", 8000), null, tempDir, null, null, ReadingLevel.Focus);

        var driver = Cockpit.App.Diagnostics.LeakSimProvider.Current;
        if (driver is null)
        {
            await CloseSessionAsync(vm);
            return;
        }

        var host = new _ChatLeakSimHost { Session = vm };
        var chatVm = new AssistantChatViewModel(host, new _ChatLeakSimSettingsStore(), new _ChatLeakSimVoiceQueue());
        var win = new Cockpit.App.Views.AssistantChatWindow
        {
            DataContext = chatVm,
            Topmost = false,
            ShowInTaskbar = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.Manual,
        };
        win.Show();
        await Task.Delay(200);

        driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginSessionInitialized { SessionId = "leaksim-1", Tools = ["Bash", "Read", "Edit"], Cwd = tempDir });

        var turns = Math.Max(1, rows / 8);
        var block = 0;
        for (var turn = 0; turn < turns; turn++)
        {
            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginAssistantThinkingDelta { SessionId = "leaksim-1", BlockIndex = block++, Thinking = $"Planning turn {turn}." });
            foreach (var chunk in new[]
            {
                $"## Step {turn}\n\nLet me **check** `src/File{turn}.cs` ",
                $"and a [link](https://example.com/{turn}).\n\n```csharp\nvar v{turn} = Compute({turn});\n```\n\n",
                $"- first {turn}\n- second {turn}\n\n| a | b |\n|---|---|\n| {turn} | {turn * 2} |\n",
            })
            {
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginAssistantTextDelta { SessionId = "leaksim-1", BlockIndex = block, Text = chunk });
                await Task.Delay(12);
            }
            block++;
            for (var k = 0; k < 6; k++)
            {
                var id = $"t{turn}_{k}";
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolUseRequested { SessionId = "leaksim-1", ToolUseId = id, ToolName = "Bash", InputJson = $"{{\"command\":\"build {id}\"}}" });
                await Task.Delay(8);
                driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginToolResult { SessionId = "leaksim-1", ToolUseId = id, Content = $"output for {id}\nRestored\nCompiled\nDone.", IsError = false });
                await Task.Delay(8);
            }
            driver.Emit(new Cockpit.Plugins.Abstractions.Sessions.PluginTurnCompleted { SessionId = "leaksim-1", Subtype = "success", Result = null, IsError = false });
            await Task.Delay(20);
        }

        driver.Complete();
        await Task.Delay(600);
        var realised = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();
        var heapMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);

        // Scroll the CHAT WINDOW's own transcript scroller up/down to force its VirtualizingStackPanel to recycle.
        var scroll = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win)
            .OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault(s => s.Name == "TranscriptScroll");
        for (var round = 0; round < 4 && scroll is not null; round++)
        {
            scroll.Offset = new Avalonia.Vector(0, 0); win.UpdateLayout(); await Task.Delay(40);
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height); win.UpdateLayout(); await Task.Delay(40);
        }
        var afterScroll = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        win.Close();
        await CloseSessionAsync(vm);
        await Task.Delay(800);
        var after = Cockpit.App.Diagnostics.LeakTracker.ReportAfterGc();

        try
        {
            System.IO.File.AppendAllText(resultPath,
                $"[chat-leaksim turns={turns}] BEFORE: {before} | REALISED: {realised} heap={heapMb}MB | AFTER-SCROLL: {afterScroll} | AFTER-CLOSE: {after}\n");
        }
        catch (Exception)
        {
            // A diagnostic result file is a nicety, never worth failing the sim over.
        }
    }

    private sealed class _ChatLeakSimHost : IAssistantSessionHost
    {
        public SessionViewModel? Session { get; init; }
        public Cockpit.Core.Assistant.AssistantActivity Activity => Cockpit.Core.Assistant.AssistantActivity.Ready;
        public string? UnavailableReason => null;
        public string? DefaultWorkingDirectory => System.IO.Path.GetTempPath();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task<SessionViewModel?> EnsureStartedAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Session);
        public Task<SessionViewModel?> RestartAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Session);
        public Task SendAsync(string text, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetSpeakReplies(bool speak) { }
        public Task ApplySettingsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ReportHoldListening(bool listening) { }
        public void ReportTranscribing(bool transcribing) { }
        public void ReportPreparing(string? status, double? fraction) { }
    }

    private sealed class _ChatLeakSimSettingsStore : Cockpit.Core.Abstractions.Assistant.IAssistantSettingsStore
    {
        public Task<Cockpit.Core.Assistant.AssistantSettings> LoadAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cockpit.Core.Assistant.AssistantSettings { IsEnabled = true });
        public Task SaveAsync(Cockpit.Core.Assistant.AssistantSettings settings, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class _ChatLeakSimVoiceQueue : Cockpit.Core.Abstractions.Voice.IVoicePlaybackQueue
    {
        public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session) { }
        public void Enqueue(IReadOnlyList<Cockpit.Core.Voice.SpeechSegment> segments, int speakerId, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session) { }
        public void NotifyPreparing(Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session) { }
        public event EventHandler<bool>? PlaybackActiveChanged { add { } remove { } }
        public event EventHandler? SpeakingStarted { add { } remove { } }
        public void StopAll() { }
        public int Generation => 0;
        public Cockpit.Core.Voice.VoicePlaybackSource ActiveSource => Cockpit.Core.Voice.VoicePlaybackSource.Session;
    }

    // Hard footprint snapshot for the sim: managed heap plus the LIVE control count in this session's transcript
    // visual tree (what virtualization actually keeps realised), so a per-render reduction (lazy rows) is visible.
    private static string _MeasureFootprint(SessionViewModel vm)
    {
        var heapMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
        var view = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }
            ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(mainWindow)
                .OfType<Cockpit.App.Views.SessionView>()
                .FirstOrDefault(v => ReferenceEquals(v.DataContext, vm))
            : null;

        int visuals = 0, buttons = 0, textBlocks = 0;
        if (view is not null)
        {
            foreach (var d in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view))
            {
                visuals++;
                if (d is Button)
                {
                    buttons++;
                }
                else if (d is TextBlock)
                {
                    textBlocks++;
                }
            }
        }

        return $"heap={heapMb}MB visuals={visuals} buttons={buttons} textblocks={textBlocks}";
    }

    // Scrolls the sim session's transcript up and down to churn the virtualizing panel's container recycling.
    private static async Task _ScrollSimAsync(SessionViewModel vm)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return;
        }

        var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(mainWindow)
            .OfType<Cockpit.App.Views.SessionView>()
            .FirstOrDefault(v => ReferenceEquals(v.DataContext, vm));
        var scroller = view is null
            ? null
            : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "TranscriptScroll");
        if (scroller is null)
        {
            return;
        }

        for (var s = 0; s < 8; s++)
        {
            scroller.Offset = new Avalonia.Vector(0, s % 2 == 0 ? scroller.Extent.Height : 0);
            await Task.Delay(150);
        }
    }

    // Recursively realises the automation-peer subtree, the way a UIA client's tree walk does.
    private static void _BuildPeerTree(Avalonia.Automation.Peers.AutomationPeer? peer, int depth)
    {
        if (peer is null || depth > 40)
        {
            return;
        }

        var children = peer.GetChildren();
        if (children is null)
        {
            return;
        }

        foreach (var child in children)
        {
            _BuildPeerTree(child, depth + 1);
        }
    }
#endif

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
        session.NameChanged -= OnSessionNameChanged;
        _lastStatus.Remove(session);

        // Deterministic teardown on a real close (a tab-switch never reaches here): empty the transcript while the pane
        // is still attached, so its realised rows dematerialise and their heavy control trees leave the tree now.
        if (session is SessionViewModel closing)
        {
            closing.VisibleTranscript.Clear();
        }

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
        // so a dispose that throws must not take the host-side teardown with it.
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The panel is already gone from the UI; what still matters is the teardown below.
        }

        // AC-834: the diagram (AC-810) and whiteboard (AC-823) registries are the same shape and were never released
        // here at all, so their "agent connected" bar outlived the agent — a coupling nobody could disconnect held the
        // surface against every other session (IsCoupledByAnother) for the life of the app (AC-34).
        _terminals?.SessionEnded(session.PaneId);
        _diagrams?.SessionEnded(session.PaneId);
        _whiteboards?.SessionEnded(session.PaneId);

        // AC-391: a closed pane must stop being remembered as a workspace-presence roster entry, or the roster only
        // ever grows for the life of the app and a reused pane id (unlikely, but not impossible) would inherit a stale
        // enrollment (AC-392, AC-393, AC-396).
        _agentCoordinator?.Forget(session.PaneId);
        _agentMessages?.Forget(session.PaneId);
        _agentClaims?.Forget(session.PaneId);
        _agentLineBudget?.Forget(session.PaneId);

        // Keyed on the pane the worktree was created for, not on session.WorktreeBranch: that field is only ever set
        // when the UI itself made the worktree at start or reattach — a worktree an agent created mid-session via the
        // worktree_create MCP tool never sets it, and used to outlive its own pane's close because of that gap (AC-85).
        if (_worktreeManager is not null)
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

    // Held so the things that must reach it despite it being in neither collection can — consent routing
    // (`_ConsentPanes`) and the live fan-out of the speech settings (`_WithAssistant`).
    private SessionViewModel? _assistantSession;

    // AC-632: the live assistant for the agent line's roster, which describes it rather than reaching it — kept
    // out of `FindSession`/`AllSessions` on purpose, which is also what keeps it unwakeable.
    internal SessionPanelViewModel? AssistantPane => _assistantSession;

    // The *only* way one is made: `Services.AssistantSessionHost` calls this and keeps the sole reference, which is
    // what makes the assistant's identity established by construction — no agent can declare that it is the assistant,
    // because declaring is not how one comes into being (AC-543, AC-410).
    internal SessionViewModel? CreateAssistantSession(string paneId)
    {
        if (_sessionFactory is null)
        {
            return null;
        }

        var session = _sessionFactory();
        session.AdoptPaneId(paneId);
        session.BelongsToNoWorkspace = true;
        session.Title = Cockpit.Core.Assistant.AssistantProfileSlot.DisplayName;
        _SeedSessionPreferences(session);

        // The screenshot button in the chat window, with the region picker and its marking tools behind it
        // (AC-630). Missing here is why the assistant was the one session that could not be shown anything.
        _WireScreenshots(session);

        // Status changes still feed the shared status plumbing, so the indicator can read "thinking" off the same
        // signal every other session reports through — but no close wiring: the assistant is not closed by the
        // operator, and its host is the only thing that ever ends it.
        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;
        _assistantSession = session;

        return session;
    }

    // Without this the dead `SessionViewModel` stayed subscribed to `OnSessionPropertyChanged` and sat in `_lastStatus`
    // for the life of the process, so the one path the ticket asks for by name — coming back after falling over —
    // leaked a whole session and everything it holds, every time it did what it was built to do.
    internal void ReleaseAssistantSession(SessionPanelViewModel session)
    {
        session.PropertyChanged -= OnSessionPropertyChanged;
        _lastStatus.Remove(session);

        // Cleared only when it is still the one being held: the host stands a replacement up before releasing the
        // dead instance, and clearing unconditionally would drop the live one's routing on the floor.
        if (ReferenceEquals(_assistantSession, session))
        {
            _assistantSession = null;
        }
    }

    // Whether `profile` has a terminal route of its own — Claude's, or one a plugin registered.
    // Asked by the spawn service before honouring a request for a TTY session, so "that profile cannot run as a
    // terminal" is a sentence the assistant can say rather than a launch that silently comes up as something else.
    internal bool ProfileHasTtyRoute(SessionProfile profile) =>
        SessionKindDefaults.HasTtyRoute(profile, _ttyProviderResolver);

    // *What is deliberately different.* Only the desk: `workspaceId` is stamped rather than worked out, the workspace
    // is not activated and `SelectedSession` does not move (AC-545, AC-410).
    internal async Task<(string PaneId, string Name, bool? PromptDelivered)?> StartSessionOnWorkspaceAsync(
        string workspaceId,
        SessionProfile profile,
        string? prompt,
        string? workingDirectory,
        string? sessionName,
        // The operator's own words override the profile's route, exactly as the New-session dialog's Kind toggle
        // does — "the same profile, but as an SDK session" is an ordinary request and this is where it lands.
        SessionKind? requestedKind = null,
        // The provider options this one session starts with, already merged over the profile's own and already
        // validated (AC-648) — null for "whatever the profile says", which is what every spawn but an overriding one
        // hands in.
        IReadOnlyDictionary<string, string>? launchOptions = null,
        // Tri-state (AC-719): null inherits the resolved project's own default, true may isolate on top of it.
        // `false` never reaches here in practice — the assistant gateway refuses it before a launch is composed —
        // but is honoured the same way false always is if it ever does.
        bool? isolateInWorktree = null,
        // This is the sole thing that changes about how a project is found: given, it is looked up directly and the
        // folder map-match below never runs; left out, the folder decides exactly as it always has (AC-773).
        string? explicitProjectId = null)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? $"{profile.Label} — {DateTime.Now:HH:mm}" : sessionName.Trim();

        // Resolved from the folder as given, or the profile's own default when nobody named one (AC-320) — the same
        // rule the embedded and plugin start paths are placed by, and never the isolated worktree a start later
        // derives from it.
        var profileOnlyDefaults = SessionStartDefaults.Resolve(project: null, profile);
        var lookupDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? profileOnlyDefaults.WorkingDirectory : workingDirectory;
        var projectId = explicitProjectId is { Length: > 0 } ? explicitProjectId : await _ProjectIdForDirectoryAsync(lookupDirectory);
        var project = await FindProjectByIdAsync(projectId);

        // Composed through the same door the launcher's Start button and the sidebar's ▶ use (AC-719): a resolved
        // project's isolation, behaviour prompt, instruction/memory/reference rows and MCP selection all come along
        // in one pass. No project falls back below to the profile's own half, unchanged from before this ticket.
        var composed = project is not null && _projectQuickStart is not null
            ? await _projectQuickStart.ComposeAsync(project, profile)
            : null;

        // This started as a hardcoded SDK launch, and a profile set to TTY came up as an SDK session with the profile's
        // own start options applied to the wrong vocabulary: it looked like it had worked, which is the only reason it
        // took a live test to notice.
        var kind = requestedKind ?? composed?.Kind ?? SessionKindDefaults.ResolveDefaultKind(profile, _ttyProviderResolver);
        var isSdk = kind == SessionKind.Sdk;
        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? composed?.WorkingDirectory ?? profileOnlyDefaults.WorkingDirectory
            : workingDirectory;

        var result = (composed ?? new NewSessionResult(
                kind,
                profile,
                // The typed Claude vocabulary is migration-only, and the dialog seeds it with the app defaults
                // whatever the profile says; a spawn has no operator at the dialog to override them either.
                SessionOptionCatalog.DefaultPermissionMode,
                SessionOptionCatalog.DefaultModel,
                SessionOptionCatalog.DefaultEffort,
                name,
                SystemPrompt: profileOnlyDefaults.SystemPrompt))
            with
        {
            Kind = kind,
            SessionName = name,
            NameIsComposed = string.IsNullOrWhiteSpace(sessionName),
            WorkingDirectory = directory,
            // The provider's own declared start defaults, saved on the profile — or those with this spawn's
            // overrides already folded in. Only ever for the kind that is actually starting: the two vocabularies
            // never both apply. A project carries no provider options of its own, so composed's are the profile's.
            PluginTtyOptions = isSdk ? null : launchOptions ?? profile.Defaults?.OptionDefaults,
            SdkLaunchOptions = isSdk ? launchOptions ?? profile.Defaults?.OptionDefaults : null,
            ReadingLevel = isSdk ? SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel).Value : null,
            ProjectId = projectId,
            // The tri-state override applied last, over whatever the project resolved (or false, with no project).
            IsolateInWorktree = isolateInWorktree ?? composed?.IsolateInWorktree ?? false,
        };

        // Non-interactive (AC-719): a failed isolation refuses with a reason instead of raising a modal on the main
        // window — the chat window's Allow row is answered by an operator who may be looking at another desk
        // entirely, and a dialog they never see would stall this turn on a question it cannot report (criterion 7).
        if (await _LaunchSessionFromResultAsync(result, workspaceId, interactive: false) is not { } paneId)
        {
            return null;
        }

        bool? promptDelivered = null;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            // By pane id rather than "the one just added": a session the operator opened at the same moment must not
            // catch a brief meant for this one (AC-760).
            promptDelivered = FindSession(paneId)?.SubmitPromptWhenReady(prompt);
        }

        return (paneId, name, promptDelivered);
    }

    // The gateway settles what may be closed (an agent session, never the assistant's own); this carries it out
    // (AC-545).
    internal Task StopSessionForAssistantAsync(SessionPanelViewModel session) => CloseSessionAsync(session);

    // Instead the host holds them here, keyed by the plugin workspace that owns them, and tears them down when that
    // workspace (or the app) closes (AC-122).
    private readonly Dictionary<string, List<SessionPanelViewModel>> _embeddedSessions = new(StringComparer.Ordinal);

    // Completed by _TeardownEmbeddedSessionAsync whatever ends the session — a workspace close, an explicit close, a
    // self-close, or the isolation refusal below — so an embedder (Autopilot) awaiting the session can tell it died
    // rather than hang.
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

        // Seed the statusline from it now rather than leave the line blank until the agent inside remembers to call
        // set_status itself: a model that never calls it, or dies before its first turn, otherwise never shows one at
        // all (AC-544, AC-13).
        if ((_TicketFromBrief(request.InitialUserMessage) ?? _TicketFromBrief(request.RunLabel)) is { } ticket)
        {
            session.Statusline = ticket;
        }

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
        _agentCoordinator?.Enroll(session.PaneId);

        // The end-signal for this session's Completion; completed on teardown whatever ends it (carrying the reason
        // when the host ended it itself — the isolation refusal in the start below), so an embedder awaiting the
        // session is never left hanging and can show why it ended.
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
                // Never fall back to the operator's real checkout — that is the contamination isolation exists to
                // prevent.
                var reason = $"Could not isolate this run: {isolationFailure.Message}";
                session.Statusline = reason;
                await _CloseEmbeddedSessionAsync(session, reason);
                return;
            }

            // Which project this run works on (AC-320), before the start rather than after: the launch asks every
            // plugin what it gives a starting session, and that answer may depend on the project, so a project
            // established afterwards would arrive too late to be used.
            await _ApplyEmbeddedProjectAsync(session, request);

            // A self-driving run (AC-152) asks for a more autonomous mode, and when it opts into the "worktree is the
            // boundary" stance (PreApproveAllTools, AC-215) its SDK tool permissions — including shell and edits — are
            // auto-allowed here rather than prompted; the host's ConsentBroker still gates the host's own (AC-174).
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
                // Pre-authorize control tools so unattended autopilot cannot stall; boundary mode allows all tools
                // within its worktree (AC-215, Raymond 2026-07-23).
                preApprovedTools: request.PreApprovedTools,
                preApproveAllTools: request.PreApproveAllTools);

            // Closed while the driver was launching: the teardown that ran then disposed a session whose runtime did
            // not exist yet, so tear it down now that it does — or its pty and child process outlive the workspace.
            if (!_IsEmbeddedSessionLive(session))
            {
                await _TeardownEmbeddedSessionAsync(session);
                return;
            }

            // AC-1239: the launch said why it failed, so end the run on that — before the confinement check below
            // answers for it and an embedder is told about a worktree when the provider was simply not there.
            if (session.StartFailure is { Length: > 0 } startFailure)
            {
                var failed = $"The session did not start: {startFailure}";
                session.Statusline = failed;
                await _CloseEmbeddedSessionAsync(session, failed);
                return;
            }

            // Confinement was asked for, so run the agent only when the session actually started AND its provider keeps
            // its file tools inside the directory it runs in.
            if (_EmbeddedConfinementRefusal(request, profile.Label, session.IsSessionReady, session.Capabilities.ConfinesFileAccessToWorkingDirectory) is { } refusal)
            {
                session.Statusline = refusal;
                await _CloseEmbeddedSessionAsync(session, refusal);
                return;
            }

            // This is how an autonomous embedded run — an Autopilot step agent — is set going without a human: its task
            // brief is the first turn (AC-174).
            if (request.InitialUserMessage is { Length: > 0 } opening && session.IsSessionReady)
            {
                session.InjectAndSubmit(opening);
            }
        }
        catch (Exception)
        {
            // A failed embedded start must not take the app down — and it must not leave the session's Completion
            // unresolved either, or an embedder awaiting it (an Autopilot step) hangs forever.
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

    // Why an embedded run that asked to be confined must not start — null when it may proceed (AC-174, AC-191) (AC-85).
    internal static string? _EmbeddedConfinementRefusal(EmbeddedSessionRequest request, string profileLabel, bool isSessionReady, bool confinesFileAccess)
    {
        if (!request.IsolateInWorktree && !request.ConfineFileToolsToWorkingDirectory)
        {
            return null;
        }

        // Named for what the run asked for, so the operator reads the refusal against the thing they set up: an
        // isolated run is about its worktree and their real checkout, a confined one about the folder it was pointed
        // at.
        var (attempt, boundary, exposure) = request.IsolateInWorktree
            ? ("isolate", "the worktree", "allowed to edit your real checkout")
            : ("confine", "its working directory", "allowed to reach files outside the folder it was given");

        // Confinement without a folder to confine to is not confinement.
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
    // session — each applies it its own way.
    internal static IReadOnlyDictionary<string, string>? _EmbeddedLaunchOptions(SessionProfile profile, EmbeddedSessionRequest request)
    {
        var defaults = profile.Defaults?.OptionDefaults;
        var addPrompt = !string.IsNullOrWhiteSpace(request.AppendSystemPrompt);
        // The flag rides the options map so it reaches every provider without a signature change (AC-174).
        var addConfine = request.IsolateInWorktree || request.ConfineFileToolsToWorkingDirectory;
        // An embedded run that starts with its composer off drives itself (an Autopilot step): no operator is watching,
        // so its tool narrowing must bind rather than merely add (AC-378). An embedded session that keeps its input —
        // one the operator converses with — is attended and stays additive, like any pane they opened themselves.
        var addUnattended = request.StartWithInputDisabled;
        // The embedded run's explicit permission mode (an Autopilot step's autonomy mode, AC-152) is a deliberate
        // per-run choice and must win over the profile's own stored permission-mode default.
        var dropProfilePermissionMode = !string.IsNullOrWhiteSpace(request.PermissionMode) && defaults is { Count: > 0 };
        if (!addPrompt && !addConfine && !dropProfilePermissionMode && !addUnattended)
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

        if (addUnattended)
        {
            options[Cockpit.Plugins.Abstractions.Sessions.WellKnownPluginSessionOptions.Unattended] = "true";
        }

        return options;
    }

    // Gives an embedded session the project it is working on (AC-320).
    internal async Task _ApplyEmbeddedProjectAsync(SessionPanelViewModel session, EmbeddedSessionRequest request)
    {
        if (request.WorkingDirectory is { Length: > 0 } directory)
        {
            session.ProjectId = await _ProjectIdForDirectoryAsync(directory);
        }
    }

    // Internal so `AssistantAgentGateway` can resolve a project before its own profile/TTY/option checks run, without
    // growing a second copy of "how a project is found" next to this one (AC-773).
    internal async Task<Project?> FindProjectByIdAsync(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        if (Projects.Projects.Count == 0)
        {
            await Projects.LoadAsync();
        }

        return Projects.Projects.FirstOrDefault(candidate => candidate.Id == projectId);
    }

    // The directory as requested, never the isolated one a start derives from it: a run's own worktree belongs to no
    // project, the repository it was cut from does (AC-320).
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

    // A run that does asks for a promise it must not silently break: when no worktree can be made — no worktree
    // manager, no directory, or a directory that is not a git repository — this throws to the start's own catch, which
    // stands the run down with the reason rather than let it edit the operator's real checkout (AC-85, AC-174).
    private async Task<string?> _ResolveEmbeddedWorkingDirectoryAsync(SessionPanelViewModel session, EmbeddedSessionRequest request, SessionProfile profile)
    {
        if (!request.IsolateInWorktree)
        {
            return request.WorkingDirectory;
        }

        // A run's shared worktree (AC-174, Raymond 2026-07-22): the run already created one worktree and every step
        // runs in it so their work accumulates on one branch.
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

    // Creates one git worktree for a multi-session run (AC-174, Raymond 2026-07-22) — backs
    // `Cockpit.Plugins.Abstractions.ICockpitHost.CreateRunWorktreeAsync`.
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

    // Driven by the manager's event rather than by the record each creation returns, so a start that is cancelled or
    // fails afterwards still leaves the operator knowing their own branch moved (AC-349).
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

    // Whether `session` is still an embedded session this host owns — false once its workspace closed and it was torn
    // down, which is how a start racing that teardown knows to stand down.
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
        // waiter unblocks whether the session finished its work or is being torn down for any other reason — carrying
        // the reason when the host ended it itself (isolation refused), else null.
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

        // Mirror CloseSessionAsync's driver-side teardown: release any terminal, diagram and whiteboard couplings,
        // forget the agent-presence enrollment, the pane's unread inbox and its resource claims, and release the
        // session's worktree.
        _terminals?.SessionEnded(session.PaneId);
        _diagrams?.SessionEnded(session.PaneId);
        _whiteboards?.SessionEnded(session.PaneId);
        _agentCoordinator?.Forget(session.PaneId);
        _agentMessages?.Forget(session.PaneId);
        _agentClaims?.Forget(session.PaneId);
        _agentLineBudget?.Forget(session.PaneId);
        if (_worktreeManager is not null)
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

    // Close affordance entry point (#11): a busy session flips its sidebar row to an inline Close/Keep
    // prompt first, so a running turn is never killed on a single click; an idle/waiting/done session
    // closes straight away.
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

    // Confirms a pending close from the inline prompt and tears the session down.
    [RelayCommand]
    private async Task ConfirmCloseSessionAsync(SessionPanelViewModel session)
    {
        session.IsConfirmingClose = false;
        await CloseSessionAsync(session);
    }

    // Dismisses the inline close prompt, keeping the session.
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

    // Disposes every live session on app shutdown so each child claude process is killed and releases
    // its MCP permission-server connection — otherwise those open SSE streams keep the server (and the
    // whole process) alive after the window closes (bug #32).
    public async ValueTask DisposeAsync()
    {
        // AC-1202: IAsyncDisposable's contract is that a second call is a no-op — now exercised for real, since
        // TearDownCockpitAsync disposes the DI container after already disposing this explicitly.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The shutdown gives this a bounded budget and then hard-exits (Program.DisposeCockpit), so where it got to
        // is the one thing worth knowing when something it should have cleaned up is still there afterwards. Three
        // lines, at the two ends and around the assistant, because a teardown that is cut off leaves no other trace.
        Cockpit.App.Logging.LifecycleLog.Write(
            $"Cockpit teardown starting: {Sessions.Count} pane session(s), assistant {(AssistantHost?.Session is null ? "absent" : "present")}.");

        // Stop the hourly update timer (AC-188) so it does not keep ticking against a disposed view model.
        _periodicUpdateTimer?.Stop();

        // The paired-node cards' polls (AC-796) outlive the Options window on purpose, but not this view model —
        // without this, each one's `DispatcherTimer` keeps itself (and the card, and `INodeSessionsClient`)
        // reachable and ticking against the network long after everything else here has been torn down.
        Security.StopPairedNodePolling();

        // The key holder is a process-wide singleton, so leaving this wired would keep the whole view model alive
        // past its window (AC-41). The worktree manager is one too, and its notice handler holds this view model
        // just as firmly (AC-349).
        _secretKeyHolder.UnprotectedSecretsWritten -= OnUnprotectedSecretsWritten;
        if (_worktreeManager is not null)
        {
            _worktreeManager.SourceRefreshed -= _ToastWorktreeSource;
        }

        var panes = Sessions.ToList();
        var embedded = _embeddedSessions.Values.SelectMany(owned => owned).ToList();
        _pendingTeardownCount = panes.Count + embedded.Count + (AssistantHost?.Session is null ? 0 : 1);

        // AC-1134: parallel, not serial — there is no shared state between panes that requires an order, and a
        // teardown that waited for each in turn let one slow pane starve every pane behind it.
        await Task.WhenAll(panes.Select(async session =>
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnSessionCloseRequested;
            session.NameChanged -= OnSessionNameChanged;
            await session.DisposeAsync();
            Interlocked.Decrement(ref _pendingTeardownCount);
        }));

        // Embedded sessions (AC-122) live outside Sessions, so they need disposing here too or their pty outlives
        // the app.
        await Task.WhenAll(embedded.Select(async session =>
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnEmbeddedSessionCloseRequested;
            await session.DisposeAsync();
            Interlocked.Decrement(ref _pendingTeardownCount);
        }));

        Cockpit.App.Logging.LifecycleLog.Write("Pane and embedded sessions torn down; the assistant is next.");

        // The assistant is deliberately in neither collection (see `CreateAssistantSession`, and the remark at
        // `StartSessionAsync` that spells out why), which means the two loops above walk straight past it — so it was
        // the one session in the app whose driver never ran its teardown at shutdown (AC-956).
        if (AssistantHost?.Session is { } assistant)
        {
            await assistant.DisposeAsync();
            Interlocked.Decrement(ref _pendingTeardownCount);
            Cockpit.App.Logging.LifecycleLog.Write("Assistant session torn down.");
        }

        if (_worktreeManager is not null)
        {
            await Task.WhenAll(panes.Concat(embedded).Select(session => _worktreeManager.CleanupDockerNetworksAsync(session.PaneId)));
        }

        _embeddedSessions.Clear();
        Sessions.Clear();
        _lastStatus.Clear();

        Cockpit.App.Logging.LifecycleLog.Write("Cockpit teardown complete.");
    }

    // AC-1134: counts sessions still disposing when Program's shutdown budget expires.
    // Volatile.Read pairs with concurrent Interlocked.Decrement calls from teardown tasks.
    public int PendingTeardownCount => Volatile.Read(ref _pendingTeardownCount);

    private int _pendingTeardownCount;
    private bool _disposed;
}
