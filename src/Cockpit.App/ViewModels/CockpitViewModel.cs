using System.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Toasts;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Updates;
using Cockpit.Core.Backup;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Rendering;
using Cockpit.Core.Configuration;
using Cockpit.Core.Rendering;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Audio;
using Cockpit.Core.Debugging;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Shortcuts;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

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
public partial class CockpitViewModel : ViewModelBase, ISingletonService, IAsyncDisposable, IPluginContributionSink
{
    private static readonly Core.Audio.AudioFormat AudioFormat = new();

    private readonly Func<SessionViewModel>? _sessionFactory;
    private readonly Func<TtyViewModel>? _ttySessionFactory;
    private readonly ISessionDialogService? _dialogService;
    private readonly IAudioCaptureService? _captureService;
    private readonly IAudioPlaybackService? _playbackService;
    private readonly IAttentionNotifier? _attentionNotifier;
    private readonly INotificationSettingsStore? _notificationSettingsStore;
    private readonly IShortcutSettingsStore? _shortcutSettingsStore;
    private readonly IBackupService? _backupService;
    private readonly IAppRestartService? _appRestart;
    private readonly IUpdateService? _updates;
    private readonly IUpdateSettingsStore? _updateSettingsStore;
    private ShortcutSettings _shortcutSettings = ShortcutSettings.Default;
    private readonly ITranscriptDisplaySettingsStore? _transcriptDisplaySettingsStore;
    private readonly ISessionBehaviorSettingsStore? _sessionBehaviorSettingsStore;
    private readonly ILayoutSettingsStore? _layoutSettingsStore;
    private readonly IDebugSettingsStore? _debugSettingsStore;
    private readonly IDelegationMcpToggle? _delegationMcpToggle;
    private readonly IConsentBroker? _consentBroker;
    private readonly ResourceMonitor? _resourceMonitor;
    private readonly IVoiceSettingsStore? _voiceSettingsStore;
    private readonly ITerminalSettingsStore? _terminalSettingsStore;
    private readonly IAudioDeviceProvider? _audioDeviceProvider;
    private readonly PluginDiagnostics? _pluginDiagnostics;
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

    /// <summary>Left-menu accordion sections contributed by plugins (#14), shown under the session list. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSideSection> PluginSideSections { get; } = [];

    /// <summary>Left-menu launcher buttons contributed by plugins (#14); clicking one runs the plugin's action (typically opening a dialog).</summary>
    public ObservableCollection<PluginSideButton> PluginSideButtons { get; } = [];

    /// <summary>Controls contributed by plugins to every session's header bar, each built per session from that session's own context. Empty = nothing rendered.</summary>
    public ObservableCollection<PluginSessionHeaderItem> PluginSessionHeaderItems { get; } = [];

    /// <summary>What plugins can *do* to one session (#: session actions) — gathered into the single menu in every session's header, rather than a button each.</summary>
    public ObservableCollection<PluginSessionAction> PluginSessionHeaderActions { get; } = [];

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

        var loses = workspace.Type == WorkspaceType.Dashboard
            ? _Count(workspace.Panes.Count, "widget")
            : _Count(Sessions.Count(session => session.WorkspaceId == workspace.Id), "session");

        var message = loses is null
            ? $"Close “{workspace.Name}”?"
            : workspace.Type == WorkspaceType.Dashboard
                ? $"Close “{workspace.Name}” and everything on it?\n\nIt holds {loses}. Closing the workspace discards its layout, and this cannot be undone."
                // Sessions are stopped, not just forgotten — so the prompt says so rather than letting the
                // operator find out afterwards.
                : $"Close “{workspace.Name}” and everything on it?\n\nIt holds {loses}, which will be stopped. This cannot be undone.";

        if (await ConfirmAsync("Close workspace", message, confirmLabel: "Close"))
        {
            await CloseWorkspaceAsync(workspaceId);
        }
    }

    /// <summary>"3 widgets" / "1 session", or null when there is nothing to lose — an empty workspace needs no warning about what it holds.</summary>
    private static string? _Count(int count, string noun) =>
        count == 0 ? null : count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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

    /// <summary>Reads the recorded plugin issues and raises the startup banner; called after plugin phase-2 completes. Errors (a plugin that did not load) and warnings (one that loaded but is flagged, e.g. built against a newer SDK) read differently, since the operator can do different things about them.</summary>
    public void RefreshPluginFailures()
    {
        var issues = _pluginDiagnostics?.Failures ?? [];
        var errors = issues.Where(issue => issue.Severity == PluginIssueSeverity.Error).ToList();
        var warnings = issues.Where(issue => issue.Severity == PluginIssueSeverity.Warning).ToList();

        HasPluginFailures = issues.Count > 0;
        PluginFailureBanner = (errors.Count, warnings.Count) switch
        {
            (0, 0) => string.Empty,
            (1, 0) => $"A plugin failed to load: {errors[0].DisplayName}. See the Plugin store → Installed for details.",
            (> 1, 0) => $"{errors.Count} plugins failed to load. See the Plugin store → Installed for details.",
            (0, 1) => $"A plugin may be incompatible with this app: {warnings[0].DisplayName}. See the Plugin store → Installed for details.",
            (0, _) => $"{warnings.Count} plugins may be incompatible with this app. See the Plugin store → Installed for details.",
            _ => $"{errors.Count} plugins failed to load and {warnings.Count} may be incompatible. See the Plugin store → Installed for details.",
        };
    }

    [RelayCommand]
    private void DismissPluginFailures() => HasPluginFailures = false;

    void IPluginContributionSink.AddPluginSideSection(string pluginId, string title, Func<Control> createView) =>
        _OnUiThread(() => PluginSideSections.Add(new PluginSideSection(pluginId, title, createView)));

    void IPluginContributionSink.AddPluginSideButton(string pluginId, string title, Action onInvoke) =>
        _OnUiThread(() => PluginSideButtons.Add(new PluginSideButton(pluginId, title, onInvoke)));

    void IPluginContributionSink.AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView) =>
        _OnUiThread(() => PluginSessionHeaderItems.Add(new PluginSessionHeaderItem(createView)));

    void IPluginContributionSink.AddPluginSessionHeaderAction(PluginSessionAction action) =>
        _OnUiThread(() => PluginSessionHeaderActions.Add(action));

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
            onSaved: () => ((IPluginContributionSink)this).NotifySettingsSaved(pluginId));
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
            onSaved: pane.Refresh);
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
        Workspaces?.Active is { Type: WorkspaceType.Sessions, SingleSessionLayout: { } single }
            ? single
            : GlobalSingleSessionLayout;

    /// <summary>The active workspace's stacking, its own override else Options'. Bound to the grid's <see cref="Controls.SessionTilePanel.StackVertically"/>.</summary>
    public bool StackSessionsVertically =>
        Workspaces?.Active is { Type: WorkspaceType.Sessions, StackSessionsVertically: { } stack }
            ? stack
            : GlobalStackSessionsVertically;

    /// <summary>
    /// Two-way for the Sessions ⚙: whether this desk follows Options. Unticking it starts the override from
    /// what the desk is doing right now, so taking control changes nothing until the operator changes
    /// something — a checkbox that rearranges your sessions the moment you tick it is one nobody ticks twice.
    /// </summary>
    public bool WorkspaceFollowsGlobalLayout
    {
        get => Workspaces?.Active is not { Type: WorkspaceType.Sessions } sessions
            || (sessions.SingleSessionLayout is null && sessions.StackSessionsVertically is null);
        set
        {
            if (Workspaces?.Active is not { Type: WorkspaceType.Sessions } sessions || value == WorkspaceFollowsGlobalLayout)
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
            if (Workspaces?.Active is not { Type: WorkspaceType.Sessions } sessions || value == SingleSessionLayout)
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
            if (Workspaces?.Active is not { Type: WorkspaceType.Sessions } sessions || value == StackSessionsVertically)
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

    public bool CanCheckForUpdates => _updates is not null;

    public bool HasUpdate => UpdateUrl.Length > 0;

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

    /// <summary>When true, sending "exit" closes the session after its turn completes (T10). Applied to all open sessions.</summary>
    [ObservableProperty]
    private bool _autoCloseOnExit;

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

    // ── AC-68 slice 3: first-use calibration ─────────────────────────────────────────────────────────────────
    private readonly ITranscriptionCalibrator? _transcriptionCalibrator;
    private readonly ITranscriptionCalibrationStore? _transcriptionCalibrationStore;
    private TranscriptionCalibration? _transcriptionCalibration;

    /// <summary>True while a calibration measurement runs — disables Run and shows progress (AC-68 slice 3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunCalibration))]
    private bool _isCalibrating;

    /// <summary>Progress/status text for the calibration (AC-68 slice 3): "Preparing model…", "Measuring…", or a result note.</summary>
    [ObservableProperty]
    private string _calibrationStatus = string.Empty;

    /// <summary>Whether a measured calibration exists to show its bars and rationale (AC-68 slice 3).</summary>
    [ObservableProperty]
    private bool _hasCalibration;

    /// <summary>Measured transcription latency in milliseconds (AC-68 slice 3) — the speed bar's value.</summary>
    [ObservableProperty]
    private double _calibrationLatencyMs;

    /// <summary>Measured desktop hitch in milliseconds (AC-68 slice 3) — the hitch bar's value.</summary>
    [ObservableProperty]
    private double _calibrationHitchMs;

    [ObservableProperty]
    private string _calibrationSpeedText = string.Empty;

    [ObservableProperty]
    private string _calibrationHitchText = string.Empty;

    [ObservableProperty]
    private string _calibrationRationale = string.Empty;

    /// <summary>True when the measured hitch reads as smooth (AC-68 slice 3) — colours the hitch bar/rationale.</summary>
    [ObservableProperty]
    private bool _calibrationDesktopSmooth;

    /// <summary>Calibration needs the model, so it can run only when voice is on and a calibrator is present (AC-68 slice 3).</summary>
    public bool CanRunCalibration => _transcriptionCalibrator is not null && VoiceEnabled && !IsCalibrating;

    /// <summary>Whether the "Run calibration" affordance is offered at all — only in a graph that has a calibrator.</summary>
    public bool ShowCalibration => _transcriptionCalibrator is not null;

    /// <summary>
    /// Measures transcription latency and desktop hitch on this machine's configured backend (AC-68 slice 3) and
    /// remembers it. A failed measurement is reported on the status line, never thrown into the dialog.
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
        try
        {
            var progress = new Progress<string>(text => CalibrationStatus = text);
            _ApplyCalibration(await _transcriptionCalibrator.MeasureAsync(progress));
            CalibrationStatus = "Measured";
        }
        catch (Exception)
        {
            // A calibration is a nice-to-have; a model that would not load or a cancelled run must not crash Options.
            CalibrationStatus = "Calibration could not run — check that voice works first.";
        }
        finally
        {
            IsCalibrating = false;
        }
    }

    private void _ApplyCalibration(TranscriptionCalibration calibration)
    {
        _transcriptionCalibration = calibration;
        HasCalibration = true;
        CalibrationLatencyMs = calibration.LatencyMs;
        CalibrationHitchMs = calibration.HitchMs;
        CalibrationSpeedText = $"{(calibration.LatencyMs / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s";
        CalibrationHitchText = $"{calibration.HitchMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms";
        CalibrationRationale = TranscriptionCalibrationReport.Rationale(calibration);
        CalibrationDesktopSmooth = TranscriptionCalibrationReport.IsDesktopSmooth(calibration);
    }

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
    private bool _voiceCleanupEnabled = true;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.AutoDetectLocalLlm"/>: auto-detect the running Ollama/LM Studio server and its model. On by default; when off, the server is set by hand below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocalLlmServerPicker))]
    [NotifyPropertyChangedFor(nameof(ShowManualLlmFields))]
    private bool _voiceAutoDetectLocalLlm = true;

    /// <summary>Which detected server auto-detect prefers when both are running (Options combo box).</summary>
    public IReadOnlyList<LocalLlmPreferenceOption> LocalLlmPreferences { get; } =
    [
        new("Auto-detect", LocalLlmPreference.Auto),
        new("Ollama", LocalLlmPreference.Ollama),
        new("LM Studio", LocalLlmPreference.LmStudio),
    ];

    [ObservableProperty]
    private LocalLlmPreferenceOption _selectedLocalLlmPreference = new("Auto-detect", LocalLlmPreference.Auto);

    /// <summary>The server-preference combo box is only meaningful while cleanup is on and auto-detect is choosing the server.</summary>
    public bool ShowLocalLlmServerPicker => VoiceCleanupEnabled && VoiceAutoDetectLocalLlm;

    /// <summary>The manual model + URL fields are shown only when cleanup is on and auto-detect is off — otherwise Cockpit decides both, and the two would contradict the picker above.</summary>
    public bool ShowManualLlmFields => VoiceCleanupEnabled && !VoiceAutoDetectLocalLlm;

    /// <summary>Model id the cleanup step asks the local LLM for (see <see cref="VoiceCleanupEnabled"/>).</summary>
    [ObservableProperty]
    private string _voiceCleanupModel = "qwen2.5:3b-instruct";

    /// <summary>Base URL of the local OpenAI-compatible LLM server (Ollama/LM Studio) used for cleanup, without the <c>/v1</c> suffix.</summary>
    [ObservableProperty]
    private string _voiceCleanupBaseUrl = "http://localhost:11434";

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

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.StopReadAloudWhenSpeaking"/> (AC-9). Off by default — the threshold cannot tell your voice from the cockpit's own coming out of a speaker.</summary>
    [ObservableProperty]
    private bool _voiceStopReadAloudWhenSpeaking;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.StopReadAloudLevelThreshold"/>. Decimal because that is what NumericUpDown binds.</summary>
    [ObservableProperty]
    private decimal _voiceStopReadAloudLevelThreshold = 0.15m;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.OpenMicSilenceTimeoutMs"/>: trailing silence (ms) that ends an open-mic utterance. Tunable.</summary>
    [ObservableProperty]
    private int _voiceOpenMicSilenceTimeoutMs = 800;

    /// <summary>The open-mic coordinator, wired at startup, exposing the runtime on/off toggle bound to the sidebar mic button (open-mic is turned on/off live, not via a settings checkbox).</summary>
    [ObservableProperty]
    private OpenMicCoordinator? _openMic;

    /// <summary>Mirrors <see cref="Cockpit.Core.Voice.VoiceSettings.NaturalizeReadAloud"/>: rewrite read-aloud text into natural speech via the local LLM before synthesis (#35). Off by default.</summary>
    [ObservableProperty]
    private bool _voiceNaturalizeReadAloud;

    /// <summary>Selectable read-aloud voices (#35) offered by the Options flyout combo box.</summary>
    public IReadOnlyList<PiperVoiceOption> TtsVoices => PiperVoiceCatalog.Voices;

    /// <summary>Piper voice used for read-aloud (#35). The model downloads lazily on first use, the same as the Whisper model.</summary>
    [ObservableProperty]
    private PiperVoiceOption _selectedTtsVoice = PiperVoiceCatalog.Default;

    /// <summary>Piper voice the Dutch segments of a mixed-language read-aloud reply route to when naturalization tags the languages (#35). Drawn from the same <see cref="TtsVoices"/> list.</summary>
    [ObservableProperty]
    private PiperVoiceOption _selectedDutchTtsVoice = PiperVoiceCatalog.DutchDefault;

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

    /// <summary>Pushes the auto-close-on-exit toggle to every open session as it changes.</summary>
    partial void OnAutoCloseOnExitChanged(bool value)
    {
        foreach (var session in Sessions)
        {
            session.AutoCloseOnExit = value;
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
        // existed, or in the design-time graph — belongs to the first one rather than to nothing.
        return active.Type == WorkspaceType.Sessions
            && (session.WorkspaceId == active.Id
                || (session.WorkspaceId.Length == 0 && Workspaces.Settings.Workspaces[0].Id == active.Id));
    }

    /// <summary>The sessions on the workspace now showing — what the sidebar lists, so it never offers a session the grid is hiding.</summary>
    public IEnumerable<SessionPanelViewModel> VisibleSessions => Sessions.Where(BelongsToActiveWorkspace);

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
        IWorkflowTemplateLibrary? workflowTemplateLibrary = null,
        ISecretProtectionService? secretProtection = null,
        IWorkspaceSettingsStore? workspaceSettingsStore = null,
        IWidgetRegistry? widgetRegistry = null,
        IConsentBroker? consentBroker = null,
        ITranscriptionAdvisor? transcriptionAdvisor = null,
        ITranscriptionCalibrator? transcriptionCalibrator = null,
        ITranscriptionCalibrationStore? transcriptionCalibrationStore = null)
    {
        // Without a store this is the default single Sessions workspace and nothing persists — which is exactly
        // what the unit-test and design-time graphs want, and is why the tab strip stays hidden there.
        //
        // The toast host goes in so a refused save is said rather than dropped: the strip's changes are all
        // fire-and-forget, so without somewhere to report to, a write the config gate turned down would be
        // silence and a lost arrangement.
        Workspaces = new WorkspacesViewModel(workspaceSettingsStore, widgetRegistry, ToastHost);
        _WireWorkspaceVisibility();

        // The Security tab (encrypting the credentials at rest). Absent in the design-time/unit-test graph, and
        // the tab simply reports "not encrypted" then rather than the dialog failing to open at all.
        Security = new SecurityOptionsViewModel(secretProtection ?? new UnprotectedSecrets());
        _ = Security.RefreshAsync();

        // The Debug tab's diagnostics panel (AC-58). Absent in the design-time/unit-test graph, where the collector
        // is not registered; the panel then reports it is unavailable rather than the dialog failing to open.
        Diagnostics = new DiagnosticsViewModel(diagnosticsCollector, _BuildSessionDescriptors);

        _updates = updateService;
        _updateSettingsStore = updateSettingsStore;
        _backupService = backupService;
        _appRestart = appRestartService;
        DelegatedTasks = delegatedTasks ?? new DelegatedTasksViewModel();
        _audioDeviceProvider = audioDeviceProvider;
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
        _sessionFactory = sessionFactory;
        _ttySessionFactory = ttySessionFactory;
        _dialogService = dialogService;
        _captureService = captureService;
        _playbackService = playbackService;
        _attentionNotifier = attentionNotifier;
        _notificationSettingsStore = notificationSettingsStore;
        _transcriptDisplaySettingsStore = transcriptDisplaySettingsStore;
        _sessionBehaviorSettingsStore = sessionBehaviorSettingsStore;
        _layoutSettingsStore = layoutSettingsStore;
        _voiceSettingsStore = voiceSettingsStore;
        _terminalSettingsStore = terminalSettingsStore;
        _debugSettingsStore = debugSettingsStore;
        // The orchestrator loads its own setting on startup (before the UI), so its live value seeds the toggle here.
        _delegationMcpToggle = delegationMcpToggle;
        _orchestratorMcpEnabled = delegationMcpToggle?.McpEnabled ?? true;
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
        _ = LoadSessionBehaviorSettingsAsync();
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
                ? Sessions.FirstOrDefault(session => session.PaneId == paneId)
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
            var pane = Sessions.FirstOrDefault(session => session.PendingConsent?.Id == promptId);
            if (pane is not null)
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
            ShortcutAction.ManageProfiles => ManageProfilesCommand,
            ShortcutAction.McpServers => OpenMcpServersCommand,
            ShortcutAction.PluginStore => Plugins.OpenStoreDialogCommand,
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

    private async Task LoadSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        var settings = await _sessionBehaviorSettingsStore.LoadAsync();
        AutoCloseOnExit = settings.AutoCloseOnExit;
    }

    /// <summary>Persists the session-behaviour settings edited in the Options flyout to <c>cockpit.json</c>.</summary>
    [RelayCommand]
    private async Task SaveSessionBehaviorSettingsAsync()
    {
        if (_sessionBehaviorSettingsStore is null)
        {
            return;
        }

        await _sessionBehaviorSettingsStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = AutoCloseOnExit });
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
            $"AI-Cockpit and its sessions are using {_Megabytes(usage.MemoryBytes)} of {_Megabytes(MachineMemory.TotalBytes())}. On macOS the system kills the whole app when memory gets tight — sessions and all.{advice}",
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
                "AI-Cockpit itself",
                "the app, its windows and its transcripts",
                _Megabytes(usage.Parts.OwnBytes),
                _Share(usage.Parts.OwnBytes, usage.MemoryBytes)))
            .Concat(usage.Parts.Children.Select(child => new ResourceRowViewModel(
                child.Name,
                "a tool server AI-Cockpit started",
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
    /// Reads the update preferences and, if they say so, looks once for a newer build (#71). Called at startup. A
    /// failed check is silent here: the cockpit has just opened, and a toast saying GitHub was unreachable is noise
    /// about a thing nobody asked for. Ask from the Options tab and it says exactly what went wrong.
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
            CheckForUpdatesOnStartup = settings.CheckOnStartup;
            IncludeNightlyBuilds = settings.Channel == UpdateChannel.Nightly;
        }

        if (!CheckForUpdatesOnStartup)
        {
            return;
        }

        var result = await updates.CheckAsync(IncludeNightlyBuilds ? UpdateChannel.Nightly : UpdateChannel.Stable);
        if (result.Release is not { } release)
        {
            return;
        }

        _Announce(release);

        // The toast is the whole point of checking on startup: a newer build nobody is told about is a newer build
        // nobody installs. Raised on the host this view model already owns rather than through IToastService —
        // that service is built *from* this view model, and injecting it here would be a circle the container walks
        // in forever.
        ToastHost.Add(
            $"{release.Name} is out. You are on {CurrentBuild}.",
            ToastSeverity.Information,
            "Open it",
            OpenUpdate);
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

        var result = await updates.CheckAsync(IncludeNightlyBuilds ? UpdateChannel.Nightly : UpdateChannel.Stable);

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
    public void OpenUpdate()
    {
        if (UpdateUrl.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // A browser that will not open is not worth taking the cockpit down for; the URL is on screen either way.
            UpdateStatus = $"Could not open a browser. The release is at {UpdateUrl}";
        }
    }

    private void _Announce(AppRelease release)
    {
        UpdateUrl = release.Url;
        UpdateStatus = $"{release.Name} is available (published {release.PublishedAt.ToLocalTime():d MMMM yyyy}).";
        OnPropertyChanged(nameof(HasUpdate));
    }

    partial void OnCheckForUpdatesOnStartupChanged(bool value) => _SaveUpdateSettings();

    partial void OnIncludeNightlyBuildsChanged(bool value) => _SaveUpdateSettings();

    private void _SaveUpdateSettings() => _ = _updateSettingsStore?.SaveAsync(
        new UpdateSettings(
            CheckForUpdatesOnStartup,
            IncludeNightlyBuilds ? UpdateChannel.Nightly : UpdateChannel.Stable));

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

            BackupStatus = "Restored. Restarting AI-Cockpit to read it.";
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
        VoiceAutoDetectLocalLlm = settings.AutoDetectLocalLlm;
        SelectedLocalLlmPreference = LocalLlmPreferences.FirstOrDefault(option => option.Value == settings.LocalLlmPreference)
                                     ?? LocalLlmPreferences[0];
        VoiceCleanupModel = settings.CleanupModel;
        VoiceCleanupBaseUrl = settings.CleanupBaseUrl;
        VoicePushToTalkKeyName = settings.PushToTalkKeyName;
        VoiceGlobalPushToTalk = settings.GlobalPushToTalk;
        // First load is app startup — capture what the hotkey actually armed with, so a later save can tell a real
        // change from a toggle-and-back. Reopening the Options dialog reloads but must not move the baseline.
        _voiceGlobalPushToTalkRunning ??= settings.GlobalPushToTalk;
        VoiceAutoSubmit = settings.AutoSubmitAfterVoice;
        VoiceOpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs;
        VoiceStopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking;
        VoiceStopReadAloudLevelThreshold = (decimal)settings.StopReadAloudLevelThreshold;
        VoiceNaturalizeReadAloud = settings.NaturalizeReadAloud;
        SelectedTtsVoice = TtsVoices.FirstOrDefault(voice => voice.VoiceId == settings.TtsVoiceId) ?? PiperVoiceCatalog.Default;
        SelectedDutchTtsVoice = TtsVoices.FirstOrDefault(voice => voice.VoiceId == settings.TtsVoiceIdDutch) ?? PiperVoiceCatalog.DutchDefault;
        SelectedSttLanguage = SttLanguages.FirstOrDefault(language => language.Code == settings.SttLanguage) ?? SttLanguages[0];

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
            CleanupModel = string.IsNullOrWhiteSpace(VoiceCleanupModel) ? "qwen2.5:3b-instruct" : VoiceCleanupModel.Trim(),
            CleanupBaseUrl = string.IsNullOrWhiteSpace(VoiceCleanupBaseUrl) ? "http://localhost:11434" : VoiceCleanupBaseUrl.Trim(),
            PushToTalkKeyName = string.IsNullOrWhiteSpace(VoicePushToTalkKeyName) ? "F9" : VoicePushToTalkKeyName.Trim(),
            GlobalPushToTalk = VoiceGlobalPushToTalk,
            AutoSubmitAfterVoice = VoiceAutoSubmit,
            OpenMicEnabled = current.OpenMicEnabled,
            OpenMicSilenceTimeoutMs = VoiceOpenMicSilenceTimeoutMs > 0 ? VoiceOpenMicSilenceTimeoutMs : 800,
            StopReadAloudWhenSpeaking = VoiceStopReadAloudWhenSpeaking,
            StopReadAloudLevelThreshold = (double)VoiceStopReadAloudLevelThreshold,
            NaturalizeReadAloud = VoiceNaturalizeReadAloud,
            TtsVoiceId = SelectedTtsVoice.VoiceId,
            TtsVoiceIdDutch = SelectedDutchTtsVoice.VoiceId,
            SttLanguage = SelectedSttLanguage.Code,
            InputDeviceName = SelectedInputDevice.DeviceName ?? "",
            OutputDeviceName = SelectedOutputDevice.DeviceName ?? "",
        });

        // Push the read-aloud settings to already-open sessions so toggling naturalization or the voice
        // takes effect immediately, rather than only on the next session (the enabled/PTT flags keep the
        // load-at-start behaviour, which the hold path re-reads).
        foreach (var session in Sessions)
        {
            session.NaturalizeReadAloud = VoiceNaturalizeReadAloud;
            session.TtsVoiceId = SelectedTtsVoice.VoiceId;
            session.DutchTtsVoiceId = SelectedDutchTtsVoice.VoiceId;
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
    /// </summary>
    public async Task<string> StartSessionForPluginAsync(SessionProfile profile, string? prompt, string? workingDirectory)
    {
        var name = $"{profile.Label} — {DateTime.Now:HH:mm}";

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
            SdkLaunchOptions: profile.Defaults?.OptionDefaults);

        await _LaunchSessionFromResultAsync(result);

        // The prompt goes in after the session exists, through the same seam a plugin's inject uses — a session that
        // is not up yet cannot be typed into, and pretending otherwise loses the prompt.
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            Sessions.LastOrDefault()?.InjectText(prompt);
        }

        return name;
    }

    // Mints and starts the matching session (SDK chat or TTY terminal) from a confirmed result, recording
    // the result on the panel so the context-menu Duplicate can replay it.
    private async Task _LaunchSessionFromResultAsync(NewSessionResult result)
    {
        if (_sessionFactory is null || _ttySessionFactory is null)
        {
            return;
        }

        if (result.Kind == SessionKind.Sdk)
        {
            var session = _sessionFactory();
            session.LaunchResult = result;
            AddSession(session, result.SessionName, result.Profile.Label);
            await session.StartConfiguredAsync(result.Profile, result.Mode, result.Model, result.Effort, result.EnabledMcpServerNames, result.WorkingDirectory, result.Resume, result.SdkLaunchOptions);
        }
        else
        {
            var session = _ttySessionFactory();
            session.LaunchResult = result;
            AddSession(session, result.SessionName, result.Profile.Label);

            // Claude's permission-mode/model/effort are its own vocabulary, not every provider's — a plugin
            // TTY provider (Codex, say) gets its own declared options via PluginTtyOptions instead, and never
            // both for the same launch (see NewSessionResult.PluginTtyOptions).
            var isClaudeProfile = result.Profile.Provider is SessionProvider.ClaudeCli;
            session.LaunchConfigured(
                result.Profile,
                isClaudeProfile ? result.Mode.Value : null,
                isClaudeProfile ? result.Model.Value : null,
                isClaudeProfile ? result.Effort.Value : null,
                result.WorkingDirectory,
                result.Resume,
                result.PluginTtyOptions);
        }
    }

    /// <summary>Context-menu Rename: begin the sidebar row's inline rename.</summary>
    [RelayCommand]
    private void RenameSession(SessionPanelViewModel session) => session.BeginRename();

    /// <summary>Context-menu Move up: shift the session one place earlier in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionUp(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index > 0)
        {
            Sessions.Move(index, index - 1);
        }
    }

    /// <summary>Context-menu Move down: shift the session one place later in the sidebar order.</summary>
    [RelayCommand]
    private void MoveSessionDown(SessionPanelViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index >= 0 && index < Sessions.Count - 1)
        {
            Sessions.Move(index, index + 1);
        }
    }

    /// <summary>Context-menu Duplicate: start a new session with the same profile/model/mode as this one (≈ Fork).</summary>
    [RelayCommand]
    private async Task DuplicateSessionAsync(SessionPanelViewModel session)
    {
        if (session.LaunchResult is { } result)
        {
            await _LaunchSessionFromResultAsync(result with { SessionName = $"{session.Title} (copy)" });
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

    /// <summary>Opens the Options dialog (#13) from the sidebar, passing this view model as its DataContext.</summary>
    [RelayCommand]
    private async Task OptionsAsync()
    {
        if (_dialogService is null)
        {
            return;
        }

        await _RefreshAudioDevicesAsync();
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
        await SaveSessionBehaviorSettingsCommand.ExecuteAsync(null);
        await SaveLayoutSettingsCommand.ExecuteAsync(null);
        await SaveVoiceSettingsCommand.ExecuteAsync(null);
        await SaveTerminalSettingsCommand.ExecuteAsync(null);
        await SaveShortcutSettingsCommand.ExecuteAsync(null);
        await SaveDebugSettingsCommand.ExecuteAsync(null);
        await SaveRenderingSettingsCommand.ExecuteAsync(null);
        AllSettingsStatus = "Saved";
    }

    private void AddSession(SessionPanelViewModel session, string? name, string profileLabel)
    {
        _sessionCounter++;
        // A session always lives on a Sessions workspace (Raymond): the one showing, else the first there is,
        // else a new one. Started while only a dashboard exists, it would otherwise run on a desk that cannot
        // show it — invisible rather than absent, which is the worse of the two.
        session.WorkspaceId = Workspaces.EnsureSessionWorkspace();
        // A friendly name from the dialog wins; otherwise fall back to "<profile> - <N>" so the sidebar
        // shows which profile — and therefore which provider — each session runs under.
        session.Title = string.IsNullOrWhiteSpace(name) ? $"{profileLabel} - {_sessionCounter}" : name.Trim();
        // Start the session on the current transcript-display preference; OnShowTimestampsChanged keeps
        // it live afterwards (T7).
        session.ShowTimestamps = ShowTimestamps;
        // Same for the auto-close-on-exit behaviour (T10); the session raises CloseRequested when an
        // "exit" turn completes and the cockpit runs its normal close flow.
        session.AutoCloseOnExit = AutoCloseOnExit;
        // Seed the diagnostic-controls preference (#73); OnShowDebugControlsChanged keeps it live afterwards.
        session.ShowDebugControls = ShowDebugControls;
        // Seed a TTY session with the current global terminal-appearance preference (#40); further
        // changes reach it live via OnTerminalFontFamilyChanged/OnTerminalFontSizeChanged. No effect on
        // SDK sessions — the setting is TTY-only.
        if (session is TtyViewModel tty)
        {
            tty.TerminalFontFamily = TerminalFontFamily;
            tty.TerminalFontSize = TerminalFontSize;
            // Seed the current stacked-vertically layout (#54); OnStackSessionsVerticallyChanged keeps it
            // live afterwards, same pattern as the font settings above.
            tty.IsVerticalLayout = StackSessionsVertically;
        }

        session.CloseRequested += OnSessionCloseRequested;

        _lastStatus[session] = session.SessionStatus;
        session.PropertyChanged += OnSessionPropertyChanged;

        Sessions.Add(session);
        SelectedSession = session;
    }

    /// <summary>
    /// Sets a session's agent/workflow statusline by its <see cref="SessionPanelViewModel.PaneId"/> (#AC-13) — the
    /// line shown under its title in the header and sidebar. Returns whether a live session matched; false is a
    /// no-op (the session may have closed), never an error. Must be called on the UI thread — the host API that a
    /// plugin/agent reaches this through marshals to it.
    /// </summary>
    public bool SetSessionStatusline(string paneId, string statusline)
    {
        if (Sessions.FirstOrDefault(session => session.PaneId == paneId) is not { } target)
        {
            return false;
        }

        target.Statusline = statusline ?? string.Empty;
        return true;
    }

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
        return true;
    }

    /// <summary>
    /// Edge-triggered attention routing: fires the presence-aware notifier once, on the transition
    /// into <see cref="SessionStatus.NeedsAttention"/> — not on every status touch while it stays
    /// there. The notifier itself decides present-toast vs away-webhook.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionPanelViewModel.SessionStatus) || sender is not SessionPanelViewModel session)
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
        if (session.SessionStatus == SessionStatus.Done && previous == SessionStatus.Busy)
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
        _lastStatus.Remove(session);

        Sessions.RemoveAt(index);
        await session.DisposeAsync();

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
        foreach (var session in Sessions.ToList())
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
            session.CloseRequested -= OnSessionCloseRequested;
            await session.DisposeAsync();
        }

        Sessions.Clear();
        _lastStatus.Clear();
    }
}
