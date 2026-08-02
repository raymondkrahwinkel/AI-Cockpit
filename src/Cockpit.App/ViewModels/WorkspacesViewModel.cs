using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Material.Icons;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Secrets;
using Cockpit.Core.Toasts;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewModels;

// The workspace tab strip above the grid: which workspaces exist, which one is active, and the commands
// that add, rename, close and switch between them. Holds `WorkspaceSettings` as the one source
// of truth and persists after every change, the way the layout and shortcut settings already do — there is
// no separate in-memory model to drift from what is on disk.
// Deliberately thin on rendering: the strip binds to `Tabs`, and the grid binds to
// `Active`'s panes. Nothing here knows what a pane looks like — that split is what lets the
// same manager drive a Sessions workspace and a Dashboard.
public sealed partial class WorkspacesViewModel : ObservableObject, ISingletonService
{
    private readonly IWorkspaceSettingsStore? _store;
    private readonly IWidgetRegistry? _widgets;
    private readonly IWorkspaceTypeRegistry? _workspaceTypes;
    private readonly ToastHostViewModel? _toasts;

    // The built body of each plugin workspace, kept by workspace id so switching away and back shows the same
    // surface — and the same embedded session — rather than building a second one. Cleared for a workspace when
    // it is gone from the settings (see `_RefreshPluginBody`).
    private readonly Dictionary<string, Control> _pluginBodies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IWorkspaceContext> _pluginContexts = new(StringComparer.Ordinal);

    // Set when the saved workspaces could not be read — see `InitializeAsync`. Persistence stays off for the rest of the run.
    private bool _loadFailed;

    // Design-time/test constructor: a manager with no persistence and no widgets behind it.
    public WorkspacesViewModel()
        : this(null)
    {
    }

    // `toasts`:
    // Where a failed save is said out loud. The host `CockpitViewModel` already owns rather than
    // `IToastService`, which is built from that view model and would be a circle — the same reasoning its
    // own toasts carry. Null in the design-time and unit-test graphs, where there is no overlay to speak to.
    public WorkspacesViewModel(IWorkspaceSettingsStore? store, IWidgetRegistry? widgets = null, ToastHostViewModel? toasts = null, IWorkspaceTypeRegistry? workspaceTypes = null)
    {
        _store = store;
        _widgets = widgets;
        _workspaceTypes = workspaceTypes;
        _toasts = toasts;
        _settings = WorkspaceSettings.Default;
        _RefreshTabs();

        // Plugins initialize after this view model is built, so the widget list is empty right now and fills a
        // moment later. Without this the "Add widget" button reads that empty list once and stays disabled for
        // the rest of the run, however many widgets are installed — and a saved dashboard's panes, whose types
        // had not been registered yet, would render as nothing.
        if (_widgets is not null)
        {
            _widgets.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(AvailableWidgets));
                OnPropertyChanged(nameof(HasAvailableWidgets));
                _RefreshWidgetPanes();
                OnPropertyChanged(nameof(ShowDashboardEmptyState));
            };
        }

        // Same late-arrival reason for workspace types: a plugin registers its type a moment after this is built,
        // so the "+" menu must hear about it, and a saved desk of that type — which rendered as an unknown-type
        // placeholder until now — must rebuild its body once the plugin is there.
        if (_workspaceTypes is not null)
        {
            _workspaceTypes.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(AvailablePluginWorkspaceTypes));
                OnPropertyChanged(nameof(HasAvailablePluginWorkspaceTypes));
                OnPropertyChanged(nameof(WorkspaceMenuOptions));
                _RefreshPluginBody();
                OnPropertyChanged(nameof(ActivePluginBody));
                OnPropertyChanged(nameof(ShowUnknownPluginWorkspace));
            };
        }
    }

    [ObservableProperty]
    private WorkspaceSettings _settings;

    // The tab strip's items, rebuilt whenever the workspace set or the selection changes.
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    // The active dashboard's widget panes — what the dashboard grid renders. Empty for a Sessions workspace,
    // which draws the session grid instead.
    public ObservableCollection<WidgetPaneViewModel> WidgetPanes { get; } = [];

    // How many rows the dashboard has to draw: its configured height, or more once the widgets have grown past it.
    public int DashboardRows =>
        Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard
            ? 0
            : DashboardGridMath.RequiredRows([.. dashboard.Panes.Select(pane => pane.Cell)], dashboard.Layout);

    // The dashboard's column count — what the grid's ColumnDefinitions are built from.
    public int DashboardColumns => Active is { } dashboard && dashboard.Type == WorkspaceType.Dashboard ? dashboard.Layout.Columns : 0;

    // Two-way for the ⚙'s Columns spinner. Separate from `DashboardColumns`, which the grid reads:
    // that one reports what is being drawn, this one accepts what the operator asks for and persists it.
    public decimal DashboardColumnsSetting
    {
        get => Active?.Layout.Columns ?? DashboardLayout.DefaultColumns;
        set
        {
            if (Active is { } dashboard && dashboard.Type == WorkspaceType.Dashboard && (int)value != dashboard.Layout.Columns)
            {
                _ = SetDashboardLayoutAsync(dashboard.Id, dashboard.Layout with { Columns = (int)value });
            }
        }
    }

    // Two-way for the ⚙'s "Show grid lines" toggle — draws the cells the widgets snap to, off by default.
    public bool DashboardShowGridLines
    {
        get => Active?.Layout.ShowGridLines ?? false;
        set
        {
            if (Active is { } dashboard && dashboard.Type == WorkspaceType.Dashboard && value != dashboard.Layout.ShowGridLines)
            {
                _ = SetDashboardLayoutAsync(dashboard.Id, dashboard.Layout with { ShowGridLines = value });
            }
        }
    }

    // Two-way for the ⚙'s Rows spinner — the dashboard's starting height, which it grows past as widgets are added.
    public decimal DashboardRowsSetting
    {
        get => Active?.Layout.Rows ?? DashboardLayout.DefaultRows;
        set
        {
            if (Active is { } dashboard && dashboard.Type == WorkspaceType.Dashboard && (int)value != dashboard.Layout.Rows)
            {
                _ = SetDashboardLayoutAsync(dashboard.Id, dashboard.Layout with { Rows = (int)value });
            }
        }
    }

    // True when a dashboard is active and holds nothing yet — the "Add widget" empty state, not the session one.
    public bool ShowDashboardEmptyState => IsDashboardActive && WidgetPanes.Count == 0;

    // Every widget type the installed plugins contribute — what the "Add widget" picker lists. Empty until a widget-providing plugin is installed.
    public IReadOnlyList<WidgetRegistration> AvailableWidgets => _widgets?.Widgets ?? [];

    // True when at least one plugin contributes a widget; gates the "Add widget" picker so it never opens an empty list.
    public bool HasAvailableWidgets => AvailableWidgets.Count > 0;

    // Whether the session grid and its empty state apply at all — false on a dashboard, which owns the content area instead.
    public bool IsSessionsActive => Active?.Type == WorkspaceType.Sessions;

    // The active workspace — what the grid renders. Never null once loaded (`WorkspaceSettings.Normalized` guarantees one).
    public Workspace? Active => Settings.Active;

    // The strip is always shown. It used to hide itself at a single workspace — "a lone tab is chrome that
    // earns nothing" — which was wrong twice over, and Raymond found both: deleting one of two made the strip
    // vanish, so a correct single deletion looked like it took both; and a workspace that existed but was
    // hidden reappeared out of nowhere the moment a second one was added. A tab is where you see which desk
    // you are on, and it has to keep saying so when there is one.
    public bool ShowTabStrip => true;

    // True when the active workspace hosts widgets — gates the ⚙ dashboard settings and the "Add widget" affordance.
    public bool IsDashboardActive => Active?.Type == WorkspaceType.Dashboard;

    // True when the projects overview is the active workspace (AC-162) — the host draws the project cards instead of a grid.
    public bool IsProjectsActive => Active?.Type == WorkspaceType.Projects;

    // True when the active workspace is a plugin-registered type: the host draws neither the session grid nor the
    // widget grid, but the plugin's own full-surface body (`ActivePluginBody`).
    public bool IsPluginWorkspaceActive => Active is { } active && !active.Type.IsBuiltIn;

    // Every plugin-registered workspace type — what the "+" menu offers below the two host types. Empty until a workspace-providing plugin is installed.
    public IReadOnlyList<WorkspaceTypeRegistration> AvailablePluginWorkspaceTypes => _workspaceTypes?.WorkspaceTypes ?? [];

    // True when at least one plugin contributes a workspace type — gates the "+" menu's plugin section so it never shows an empty heading.
    public bool HasAvailablePluginWorkspaceTypes => AvailablePluginWorkspaceTypes.Count > 0;

    // The "+" menu's entries: the two host types, then every plugin-registered type, in one uniform shape so the
    // menu is a single list bound to a single command (a plugin type with no vector icon falls back to a neutral
    // plugin mark).
    public IReadOnlyList<WorkspaceMenuOption> WorkspaceMenuOptions =>
    [
        new("Sessions", MaterialIconKind.ChatOutline, "AI sessions and terminals", WorkspaceType.Sessions),
        new("Dashboard", MaterialIconKind.ViewDashboardOutline, "Widgets", WorkspaceType.Dashboard),
        // The projects overview is deliberately absent: it is always open and cannot be added a second time, so
        // offering it here would be a menu entry whose only possible outcome is nothing happening.
        .. AvailablePluginWorkspaceTypes.Select(type =>
            new WorkspaceMenuOption(type.Title, type.IconKind ?? MaterialIconKind.PuzzleOutline, type.Description, new WorkspaceType(type.Id))),
    ];

    // The active plugin workspace's body, built by its plugin and cached per workspace (see
    // `_RefreshPluginBody`). Null when a host type is active, or when the active plugin type's plugin
    // is not installed — the view shows the unknown-type placeholder then (`ShowUnknownPluginWorkspace`).
    public Control? ActivePluginBody =>
        Active is { } active && _pluginBodies.TryGetValue(active.Id, out var body) ? body : null;

    // True when a plugin workspace is active but its body could not be built — the plugin that registers this type
    // is not installed. The view shows a placeholder rather than an empty surface, and the body rebuilds itself the
    // moment the plugin registers (the registry raises `Changed`).
    public bool ShowUnknownPluginWorkspace =>
        IsPluginWorkspaceActive && ActivePluginBody is null;

    // Loads the saved workspaces. Called once at startup; a no-op without a store (design time).
    // Never throws — its caller discards the task, so a throw would land on a task nobody observes. What makes
    // that worth more than a log line: the constructor's default is a whole, valid `WorkspaceSettings`,
    // every change here persists all of it, and so a failed load does not merely hide the operator's workspaces
    // for the session — the first thing they touched would write that default over the ones they actually have.
    // A failed load therefore turns persistence off for the rest of the run: what is on disk is theirs, unread,
    // and this view model has nothing worth putting in its place.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            Settings = await _store.LoadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _loadFailed = true;
            _toasts?.Add(
                $"Your saved workspaces could not be read: {exception.Message} This cockpit has started on a default one, and nothing will be saved over yours until it is restarted.",
                ToastSeverity.Error,
                actionLabel: null,
                onAction: null);
        }
    }

    // The Sessions workspace a new session belongs on, creating one when there is none (Raymond, 2026-07-15:
    // "een sessie moet vanaf nu altijd in een session workspace zitten"). Starting a session while only a
    // dashboard exists would otherwise put it on a desk that cannot show it — the session would run, invisibly,
    // which is worse than refusing.
    // Returns the id synchronously — the caller is stamping a session it is building right now, and cannot
    // wait on a disk write to know where it belongs. Persisting is fire-and-forget, the same way every other
    // change here settles.
    public string EnsureSessionWorkspace()
    {
        if (Active is { } active && active.Type == WorkspaceType.Sessions)
        {
            return active.Id;
        }

        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions) is { } existing)
        {
            // One exists but a dashboard is showing: switch to it, so the session appears where it was put
            // rather than somewhere the operator has to go and find.
            _ = _ApplyAsync(Settings.WithActive(existing.Id));
            return existing.Id;
        }

        var created = Workspace.Create(_UniqueName(WorkspaceType.Sessions), WorkspaceType.Sessions);
        _ = _ApplyAsync(Settings.WithWorkspace(created));
        return created.Id;
    }

    [RelayCommand]
    private Task AddWorkspaceAsync(WorkspaceType type) =>
        _ApplyAsync(Settings.WithWorkspace(Workspace.Create(_UniqueName(type), type)));

    // Creates a Sessions desk called `name` and returns it — the "+" menu's own act, but with the
    // name given rather than composed, for a caller that was told what to call it (AC-545: the assistant, asked out
    // loud for "a desk for the release work").
    // It does become the active desk, because `WorkspaceSettings.WithWorkspace` makes what it adds
    // active. Deliberate and kept: asking for a desk to be made is asking to be shown it, and an empty new desk has
    // nothing on it to interrupt. That is the opposite of a *spawn* onto an existing desk, which must leave
    // the operator where they are — see `CockpitViewModel.StartSessionOnWorkspaceAsync`.
    //
    // The name is taken as given and not made unique. Two desks may share a label the way two sessions may; what a
    // caller spawns onto afterwards is the id this returns, not the name.
    public async Task<Workspace> CreateSessionsWorkspaceAsync(string name)
    {
        var created = Workspace.Create(name, WorkspaceType.Sessions);
        await _ApplyAsync(Settings.WithWorkspace(created));
        return created;
    }

    // Brings the workspace of type `workspaceTypeId` to the front, creating one when none is open
    // — the programmatic entry behind a plugin surfacing its own workspace on an intent ("Start in Autopilot",
    // AC-150) and behind the sidebar's way to the projects overview (AC-162). Mirrors
    // `EnsureSessionWorkspace`: an existing one is activated in place rather than duplicated, so asking
    // twice lands on the one desk instead of stacking empty copies.
    public Task OpenWorkspaceAsync(string workspaceTypeId)
    {
        var type = WorkspaceType.FromId(workspaceTypeId);
        if (Active is { } active && active.Type == type)
        {
            return Task.CompletedTask;
        }

        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Type == type) is { } existing)
        {
            return _ApplyAsync(Settings.WithActive(existing.Id));
        }

        // Name the tab after the plugin type's registered title ("Autopilot"), the way the "+" menu does; a host
        // type has no registration, and its own id already reads as the title ("Projects").
        var title = _workspaceTypes?.WorkspaceTypes.FirstOrDefault(registration => registration.Id == type.Id)?.Title ?? type.Id;
        return _ApplyAsync(Settings.WithWorkspace(Workspace.Create(_UniqueName(title), type)));
    }

    // Whether closing this workspace would do anything. False for the last one — the cockpit always needs a
    // desk to render — and for an id nothing holds. The caller asks before it starts tearing down what is on
    // the workspace, since stopping its sessions and then finding the workspace stays is the one outcome worse
    // than either.
    public bool CanClose(string workspaceId) =>
        Settings.Workspaces.Count > 1
        && Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is { } workspace
        // The projects overview is a fixture: always there, exactly once, never closed. Answering false here is
        // what greys its ✕ and its menu entry; WorkspaceSettings refuses the removal itself, so a caller that
        // does not ask still cannot take it away.
        && workspace.Type != WorkspaceType.Projects;

    [RelayCommand]
    private Task CloseWorkspaceAsync(string workspaceId) => _ApplyAsync(Settings.WithoutWorkspace(workspaceId));

    [RelayCommand]
    private Task SelectWorkspaceAsync(string workspaceId) => _ApplyAsync(Settings.WithActive(workspaceId));

    // Drops a dragged tab at `targetIndex` in the strip (Raymond, 2026-07-15). Persists, so
    // the order you arranged is the order you come back to; the selection stays where it was, since
    // rearranging the desks is not the same as walking to another one.
    public Task MoveWorkspaceAsync(string workspaceId, int targetIndex) =>
        _ApplyAsync(Settings.WithMoved(workspaceId, targetIndex));

    // Ctrl+Shift+Left — the previous workspace, wrapping past the first (Raymond, 2026-07-15).
    [RelayCommand]
    private Task SelectPreviousWorkspaceAsync() => _ApplyAsync(Settings.WithSteppedActive(-1));

    // Ctrl+Shift+Right — the next workspace, wrapping past the last.
    [RelayCommand]
    private Task SelectNextWorkspaceAsync() => _ApplyAsync(Settings.WithSteppedActive(1));

    // Renames a desk and persists it. Public because the tab strip is no longer the only caller — the assistant
    // renames one too (AC-592) — and a command executed from code hides whether it was awaited.
    [RelayCommand]
    public Task RenameWorkspaceAsync((string WorkspaceId, string Name) rename)
    {
        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == rename.WorkspaceId) is not { } workspace
            || string.IsNullOrWhiteSpace(rename.Name))
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(workspace with { Name = rename.Name.Trim() }));
    }

    // Applies a dashboard's grid settings (its ⚙). Ignored for a Sessions workspace, which has no grid to set.
    public Task SetDashboardLayoutAsync(string workspaceId, DashboardLayout layout)
    {
        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard)
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(dashboard with { Layout = layout.Clamped() }));
    }

    // Overrides how this Sessions workspace arranges its panes, or hands it back to Options — null follows the
    // global setting (Raymond, 2026-07-15). Both are written together because they are one decision made on
    // one ⚙: a desk either arranges itself or it follows, and a half-override is a state nothing in the UI can
    // express. Ignored for a Dashboard, which has its own grid.
    public Task SetSessionLayoutAsync(string workspaceId, bool? singleSession, bool? stackVertically)
    {
        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } sessions || sessions.Type != WorkspaceType.Sessions)
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(sessions with
        {
            SingleSessionLayout = singleSession,
            StackSessionsVertically = stackVertically,
        }));
    }

    // Places a widget on the active dashboard, at the first free cell its size fits
    // (`DashboardGridMath.PlaceNext`). Ignored unless a dashboard is active — a Sessions
    // workspace cannot hold a widget, and the affordance that calls this is hidden there anyway.
    public Task AddWidgetAsync(string widgetId, int columnSpan = 1, int rowSpan = 1)
    {
        if (Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard)
        {
            return Task.CompletedTask;
        }

        var cell = DashboardGridMath.PlaceNext([.. dashboard.Panes.Select(pane => pane.Cell)], dashboard.Layout, columnSpan, rowSpan);
        var pane = new WorkspacePane(Guid.NewGuid().ToString("n"), PaneKind.Widget) { WidgetId = widgetId, Cell = cell };
        return _ApplyAsync(Settings.WithUpdated(dashboard.WithPane(pane)));
    }

    // Places a widget picked from the "Add widget" list, at the size its type asks for.
    [RelayCommand]
    private Task PlaceWidgetAsync(WidgetRegistration? registration) =>
        registration is null
            ? Task.CompletedTask
            : AddWidgetAsync(registration.Id, registration.DefaultColumnSpan, registration.DefaultRowSpan);

    // Adds a workspace from the "+" menu — a host type or a plugin type alike, since the menu option carries the type either way.
    [RelayCommand]
    private Task AddWorkspaceOptionAsync(WorkspaceMenuOption? option) =>
        option is null
            ? Task.CompletedTask
            : _ApplyAsync(Settings.WithWorkspace(Workspace.Create(_UniqueName(option.Title), option.Type)));

    // Moves a widget from the dashboard showing to another one (F5): dragged onto its tab, it leaves this
    // desk and lands on the first free cell over there — its own size, not squeezed into whatever it was
    // dropped over, because the target's arrangement is not the operator's to disturb from another workspace.
    //
    // The pane keeps its id, which is what carries the widget's settings: instance storage is keyed by it, so
    // a moved system monitor arrives still showing the metrics it was set to. Rebuilding it as a new pane would
    // quietly reset it — the same rule the session grid learned the hard way on 2026-07-13.
    //
    // Both ends are applied in one write. Two (remove here, add there) can half-land, and a half-landed move is
    // a widget that exists nowhere.
    // False when the move does not apply: same desk, no dashboard either end, or no such pane.
    public async Task<bool> MovePaneToWorkspaceAsync(string paneId, string targetWorkspaceId)
    {
        if (Active is not { } source || source.Type != WorkspaceType.Dashboard
            || source.Id == targetWorkspaceId
            || source.Panes.FirstOrDefault(pane => pane.Id == paneId) is not { } moving
            || Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == targetWorkspaceId) is not { } target || target.Type != WorkspaceType.Dashboard)
        {
            return false;
        }

        var cell = DashboardGridMath.PlaceNext(
            [.. target.Panes.Select(pane => pane.Cell)],
            target.Layout,
            moving.Cell.ColumnSpan,
            moving.Cell.RowSpan);

        await _ApplyAsync(Settings
            .WithUpdated(source.WithoutPane(paneId))
            .WithUpdated(target.WithPane(moving with { Cell = cell })));

        return true;
    }

    // Removes a pane from the active workspace (the pane's ✕).
    public Task RemovePaneAsync(string paneId) =>
        Active is not { } workspace ? Task.CompletedTask : _ApplyAsync(Settings.WithUpdated(workspace.WithoutPane(paneId)));

    // Removes a pane from a specific workspace by id, whether or not it is the one on screen (AC-410): a session
    // on a Sessions desk the operator is not currently looking at must still lose its persisted pane record when
    // it closes. A no-op when `workspaceId` names no workspace.
    public Task RemovePaneAsync(string workspaceId, string paneId) =>
        Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } workspace
            ? Task.CompletedTask
            : _ApplyAsync(Settings.WithUpdated(workspace.WithoutPane(paneId)));

    // Adds a pane to a specific workspace by id (AC-410) — the counterpart to
    // `RemovePaneAsync(string, string)`, used to persist an AI session's pane record at the moment
    // it starts, on whichever Sessions workspace it belongs to, active or not. A no-op when
    // `workspaceId` names no workspace.
    public Task AddPaneAsync(string workspaceId, WorkspacePane pane) =>
        Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } workspace
            ? Task.CompletedTask
            : _ApplyAsync(Settings.WithUpdated(workspace.WithPane(pane)));

    // Updates a persisted AI-session pane's title and whether it was chosen (AC-514) — the counterpart to
    // `AddPaneAsync` for a name that changes after the pane already exists: an operator's inline
    // rename, or a name a plugin/agent suggested. A no-op — no write at all — only when
    // `workspaceId` names no workspace; a workspace with no pane matching
    // `paneId` still writes (`Workspace.WithPaneRenamed` leaves its panes
    // untouched in that case, but the write goes through regardless, the same as `MovePaneAsync`).
    public Task RenamePaneAsync(string workspaceId, string paneId, string title, bool nameIsChosen) =>
        Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } workspace
            ? Task.CompletedTask
            : _ApplyAsync(Settings.WithUpdated(workspace.WithPaneRenamed(paneId, title, nameIsChosen)));

    // Moves a pane to `cell` after a drag. Position only — the pane itself is never rebuilt, which is what keeps a dragged terminal from losing its pty (leermoment 2026-07-13).
    public Task MovePaneAsync(string paneId, GridCell cell) =>
        Active is not { } workspace ? Task.CompletedTask : _ApplyAsync(Settings.WithUpdated(workspace.WithPaneMoved(paneId, cell)));

    // Drops a dragged widget on a cell: the cell takes it, or its occupant swaps places with it
    // (`DashboardGridMath.Drop`). Applies the whole arrangement at once, so a swap cannot
    // half-land and leave two widgets stacked on one cell. A drop the math refuses — off the grid, or over more
    // than one widget — leaves the dashboard alone, the same way a refused resize does.
    public Task DropPaneAsync(string paneId, int column, int row)
    {
        if (Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard
            || DashboardGridMath.Drop([.. dashboard.Panes.Select(pane => (pane.Id, pane.Cell))], paneId, (column, row), dashboard.Layout) is not { } arranged)
        {
            return Task.CompletedTask;
        }

        var updated = dashboard with
        {
            Panes = [.. dashboard.Panes.Select(pane => pane with { Cell = arranged.First(entry => entry.Id == pane.Id).Cell })],
        };

        return _ApplyAsync(Settings.WithUpdated(updated));
    }

    // Resizes a widget by dragging its corner: the cell under the pointer becomes its new bottom-right. A size
    // that would leave the grid or cover a neighbour is refused, so the pane stops at the obstacle and keeps
    // its last good size (`DashboardGridMath.Resize`).
    public Task ResizePaneAsync(string paneId, int column, int row)
    {
        if (Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard
            || DashboardGridMath.Resize([.. dashboard.Panes.Select(pane => (pane.Id, pane.Cell))], paneId, (column, row), dashboard.Layout) is not { } resized)
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(dashboard.WithPaneMoved(paneId, resized)));
    }

    // The active dashboard as a file. Credentials are dropped on the way out (`DashboardExporter`),
    // so a dashboard you hand to someone carries its arrangement and its settings but never a key.
    // Scrubs by the shared name rule *and* the keys the widget-providing plugins declared themselves —
    // the rule cannot guess a key called "pat", and a declaration that only protected the backup and the
    // at-rest encryption but not the file you hand to someone would protect the wrong two of the three.
    public string? ExportActiveDashboard()
    {
        if (Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard || _widgets is null)
        {
            return null;
        }

        var export = DashboardExporter.ToExport(dashboard, _ConfigOf, new SecretFields(_widgets.DeclaredSecretKeys));
        return JsonSerializer.Serialize(export, _FileJson);
    }

    // Adds a dashboard from an exported file. Returns what came of it — including the widget types this
    // cockpit does not have, which were skipped — or null when the file is not one this build can read.
    public async Task<DashboardImport?> ImportDashboardAsync(string json)
    {
        DashboardExport? export;
        try
        {
            export = JsonSerializer.Deserialize<DashboardExport>(json, _FileJson);
        }
        catch (JsonException)
        {
            // A file that is not a dashboard is a thing to say so about, not to throw over.
            return null;
        }

        if (export is null || !DashboardExporter.CanRead(export) || _widgets is null)
        {
            return null;
        }

        var import = DashboardExporter.FromExport(export, _widgets.IsInstalled, _UniqueName(export.Name));

        // Read before anything lands. A widget's settings travel as the raw JSON it wrote, so a file whose
        // envelope parses can still carry settings that do not — and finding that out after the workspace was
        // applied left a dashboard on the strip with its widgets unconfigured and an exception on the way out.
        // That is the half-landed import the one-write rule exists to prevent, and it made a liar of the promise
        // above it: a file this build cannot read has to be said, not thrown.
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> settings = [];
        try
        {
            foreach (var (paneId, config) in import.Config)
            {
                settings[paneId] = config.ToDictionary(
                    entry => entry.Key,
                    entry => JsonSerializer.Deserialize<JsonElement>(entry.Value));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        await _ApplyAsync(Settings.WithWorkspace(import.Workspace));

        // After the workspace lands, so the instances exist to write to.
        foreach (var (paneId, config) in settings)
        {
            if (WidgetPanes.FirstOrDefault(pane => pane.Id == paneId) is { } placed)
            {
                placed.WriteConfig(config);
            }
        }

        return import;
    }

    // A name that does not collide with what is already there — importing the same dashboard twice gives "Monitoring" and "Monitoring 2", not two of the same tab.
    private string _UniqueName(string preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "Dashboard" : preferred.Trim();
        if (Settings.Workspaces.All(workspace => workspace.Name != baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (Settings.Workspaces.All(workspace => workspace.Name != candidate))
            {
                return candidate;
            }
        }
    }

    private IReadOnlyDictionary<string, string> _ConfigOf(string paneId) =>
        WidgetPanes.FirstOrDefault(pane => pane.Id == paneId)?.ReadConfig() ?? new Dictionary<string, string>();

    private static readonly JsonSerializerOptions _FileJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Puts `settings` on screen and on disk — every change here settles through this one path.
    // Never throws. Nearly every caller discards the task it returns (`_ = _ApplyAsync(…)`), because
    // arranging a desk is not something the operator waits on — so an exception out of here would land on a task
    // nobody observes and simply be gone. The write can genuinely fail: it goes through
    // `CockpitConfigFileAccess.UpdateAsync`, which refuses rather than writes when the config's write gate
    // times out or the file is unreadable. Saying nothing would leave the change on screen and absent from disk,
    // and the operator would find out at the next start, with their arrangement gone and no reason given.
    private async Task _ApplyAsync(WorkspaceSettings settings)
    {
        if (ReferenceEquals(settings, Settings))
        {
            return;
        }

        Settings = settings;
        if (_store is null || _loadFailed)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(settings);
        }
        catch (Exception exception)
        {
            // Left on screen rather than reverted: the operator is mid-gesture, and yanking the widget back under
            // their pointer explains nothing. What they need is to know it did not land, while it is still theirs
            // to retry.
            _toasts?.Add(
                $"This workspace change could not be saved: {exception.Message} It is on screen, but it will be gone after a restart.",
                ToastSeverity.Error,
                actionLabel: null,
                onAction: null);
        }
    }

    partial void OnSettingsChanged(WorkspaceSettings value)
    {
        _RefreshTabs();
        _RefreshWidgetPanes();
        _RefreshPluginBody();
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(ShowTabStrip));
        OnPropertyChanged(nameof(IsDashboardActive));
        OnPropertyChanged(nameof(IsSessionsActive));
        OnPropertyChanged(nameof(IsProjectsActive));
        OnPropertyChanged(nameof(IsPluginWorkspaceActive));
        OnPropertyChanged(nameof(ActivePluginBody));
        OnPropertyChanged(nameof(ShowUnknownPluginWorkspace));
        OnPropertyChanged(nameof(ShowDashboardEmptyState));
        OnPropertyChanged(nameof(DashboardRows));
        OnPropertyChanged(nameof(DashboardColumns));
        OnPropertyChanged(nameof(DashboardColumnsSetting));
        OnPropertyChanged(nameof(DashboardRowsSetting));
        OnPropertyChanged(nameof(DashboardShowGridLines));
    }

    // Rebuilds the active dashboard's panes. A pane whose widget type no longer resolves is skipped rather
    // than fatal: uninstalling or disabling a plugin leaves its widgets behind in a saved dashboard, and that
    // must cost the operator the pane, not the workspace.
    private void _RefreshWidgetPanes()
    {
        if (_widgets is null || Active is not { } dashboard || dashboard.Type != WorkspaceType.Dashboard)
        {
            WidgetPanes.Clear();
            return;
        }

        // Reconcile rather than rebuild. Clearing and re-creating every pane on any change threw away each
        // plugin's control — so moving one widget silently reset the others, and a clock that had been placed
        // before its plugin finished registering came back as a second copy stacked on the first. Same rule as
        // the session grid (2026-07-13): a pane is updated in place, never rebuilt, or it loses what it holds.
        var wanted = dashboard.Panes
            .Where(pane => pane.WidgetId is not null)
            .ToList();

        foreach (var stale in WidgetPanes.Where(existing => wanted.All(pane => pane.Id != existing.Id)).ToList())
        {
            WidgetPanes.Remove(stale);
        }

        foreach (var pane in wanted)
        {
            if (WidgetPanes.FirstOrDefault(existing => existing.Id == pane.Id) is { } known)
            {
                // Only the placement can have changed; the widget behind it is the same instance.
                known.Pane = pane;
                continue;
            }

            if (_widgets.CreateInstance(pane.WidgetId!, pane.Id) is { } instance)
            {
                WidgetPanes.Add(new WidgetPaneViewModel(pane, instance.Registration, instance.Context));
            }

            // A pane whose widget type does not resolve is skipped, not fatal: uninstalling or disabling a
            // plugin leaves its widgets behind in a saved dashboard, and that must cost the pane, not the
            // workspace. It reappears when the plugin registers (the registry raises Changed).
        }
    }

    // Builds the active plugin workspace's body once and keeps it, and drops the bodies of workspaces that are
    // gone. Built on first show, not rebuilt on every switch: rebuilding would call the plugin's body factory
    // again — starting a second embedded session — so a body is created once per workspace and reused when the
    // operator switches away and back. A body whose plugin type is not registered stays unbuilt; the view shows
    // the unknown-type placeholder, and this rebuilds it when the registry raises Changed.
    private void _RefreshPluginBody()
    {
        // The embedded sessions of a closed plugin workspace are torn down by the shell's CloseForWorkspace; here
        // we release the cached control so a reused id cannot show a stale body.
        foreach (var staleId in _pluginBodies.Keys.Where(id => Settings.Workspaces.All(workspace => workspace.Id != id)).ToList())
        {
            // The workspace is really gone (not just switched away from): tell its body so a long-running job (Autopilot's
            // autonomous run) is cancelled and torn down, before the cached control is released.
            if (_pluginContexts.TryGetValue(staleId, out var context) && context is WorkspaceContext concrete)
            {
                concrete.RaiseClosed();
            }

            _pluginContexts.Remove(staleId);
            _pluginBodies.Remove(staleId);
        }

        if (Active is { } active
            && !active.Type.IsBuiltIn
            && !_pluginBodies.ContainsKey(active.Id)
            && _workspaceTypes?.CreateBody(active.Type.Id, active.Id) is { } built)
        {
            _pluginContexts[active.Id] = built.Context;
            _pluginBodies[active.Id] = built.Registration.CreateBody(built.Context);
        }
    }

    private void _RefreshTabs()
    {
        Tabs.Clear();
        foreach (var workspace in Settings.Workspaces)
        {
            Tabs.Add(new WorkspaceTabViewModel(workspace, isActive: workspace.Id == Active?.Id, icon: _IconFor(workspace)));
        }
    }

    // The tab icon for a workspace: a plugin type's own registered icon, else the host default the tab view model carries for a Sessions/Dashboard workspace (null keeps that default).
    private MaterialIconKind? _IconFor(Workspace workspace) =>
        workspace.Type.IsBuiltIn
            ? null
            : _workspaceTypes?.WorkspaceTypes.FirstOrDefault(type => type.Id == workspace.Type.Id)?.IconKind;

    // "Dashboard", then "Dashboard 2", … — a name the operator can rename, but never a strip of identical tabs.
    // The type's own id is the name: the built-in ids read as titles ("Sessions", "Dashboard", "Launcher"), and a
    // plugin type at least says what it is. It used to hard-code the two host names, so every other type — the
    // launcher included — came out of this called "Sessions".
    private string _UniqueName(WorkspaceType type) => _UniqueName(type.Id);
}
