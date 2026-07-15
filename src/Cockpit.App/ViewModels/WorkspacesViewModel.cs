using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The workspace tab strip above the grid: which workspaces exist, which one is active, and the commands
/// that add, rename, close and switch between them. Holds <see cref="WorkspaceSettings"/> as the one source
/// of truth and persists after every change, the way the layout and shortcut settings already do — there is
/// no separate in-memory model to drift from what is on disk.
/// </summary>
/// <remarks>
/// Deliberately thin on rendering: the strip binds to <see cref="Tabs"/>, and the grid binds to
/// <see cref="Active"/>'s panes. Nothing here knows what a pane looks like — that split is what lets the
/// same manager drive a Sessions workspace and a Dashboard.
/// </remarks>
public sealed partial class WorkspacesViewModel : ObservableObject, ISingletonService
{
    private readonly IWorkspaceSettingsStore? _store;

    /// <summary>Design-time/test constructor: a manager with no persistence behind it.</summary>
    public WorkspacesViewModel()
        : this(null)
    {
    }

    public WorkspacesViewModel(IWorkspaceSettingsStore? store)
    {
        _store = store;
        _settings = WorkspaceSettings.Default;
        _RefreshTabs();
    }

    [ObservableProperty]
    private WorkspaceSettings _settings;

    /// <summary>The tab strip's items, rebuilt whenever the workspace set or the selection changes.</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    /// <summary>The active workspace — what the grid renders. Never null once loaded (<see cref="WorkspaceSettings.Normalized"/> guarantees one).</summary>
    public Workspace? Active => Settings.Active;

    /// <summary>Whether the strip is worth showing at all: a single workspace is the cockpit as it has always looked, and a lone tab is chrome that earns nothing.</summary>
    public bool ShowTabStrip => Settings.Workspaces.Count > 1;

    /// <summary>True when the active workspace hosts widgets — gates the ⚙ dashboard settings and the "Add widget" affordance.</summary>
    public bool IsDashboardActive => Active?.Type == WorkspaceType.Dashboard;

    /// <summary>Loads the saved workspaces. Called once at startup; a no-op without a store (design time).</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return;
        }

        Settings = await _store.LoadAsync(cancellationToken);
    }

    [RelayCommand]
    private Task AddWorkspaceAsync(WorkspaceType type) =>
        _ApplyAsync(Settings.WithWorkspace(Workspace.Create(_UniqueName(type), type)));

    [RelayCommand]
    private Task CloseWorkspaceAsync(string workspaceId) => _ApplyAsync(Settings.WithoutWorkspace(workspaceId));

    [RelayCommand]
    private Task SelectWorkspaceAsync(string workspaceId) => _ApplyAsync(Settings.WithActive(workspaceId));

    /// <summary>Ctrl+Shift+Left — the previous workspace, wrapping past the first (Raymond, 2026-07-15).</summary>
    [RelayCommand]
    private Task SelectPreviousWorkspaceAsync() => _ApplyAsync(Settings.WithSteppedActive(-1));

    /// <summary>Ctrl+Shift+Right — the next workspace, wrapping past the last.</summary>
    [RelayCommand]
    private Task SelectNextWorkspaceAsync() => _ApplyAsync(Settings.WithSteppedActive(1));

    [RelayCommand]
    private Task RenameWorkspaceAsync((string WorkspaceId, string Name) rename)
    {
        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == rename.WorkspaceId) is not { } workspace
            || string.IsNullOrWhiteSpace(rename.Name))
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(workspace with { Name = rename.Name.Trim() }));
    }

    /// <summary>Applies a dashboard's grid settings (its ⚙). Ignored for a Sessions workspace, which has no grid to set.</summary>
    public Task SetDashboardLayoutAsync(string workspaceId, DashboardLayout layout)
    {
        if (Settings.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { Type: WorkspaceType.Dashboard } dashboard)
        {
            return Task.CompletedTask;
        }

        return _ApplyAsync(Settings.WithUpdated(dashboard with { Layout = layout.Clamped() }));
    }

    /// <summary>
    /// Places a widget on the active dashboard, at the first free cell its size fits
    /// (<see cref="DashboardGridMath.PlaceNext"/>). Ignored unless a dashboard is active — a Sessions
    /// workspace cannot hold a widget, and the affordance that calls this is hidden there anyway.
    /// </summary>
    public Task AddWidgetAsync(string widgetId, int columnSpan = 1, int rowSpan = 1)
    {
        if (Active is not { Type: WorkspaceType.Dashboard } dashboard)
        {
            return Task.CompletedTask;
        }

        var cell = DashboardGridMath.PlaceNext([.. dashboard.Panes.Select(pane => pane.Cell)], dashboard.Layout, columnSpan, rowSpan);
        var pane = new WorkspacePane(Guid.NewGuid().ToString("n"), PaneKind.Widget) { WidgetId = widgetId, Cell = cell };
        return _ApplyAsync(Settings.WithUpdated(dashboard.WithPane(pane)));
    }

    /// <summary>Removes a pane from the active workspace (the pane's ✕).</summary>
    public Task RemovePaneAsync(string paneId) =>
        Active is not { } workspace ? Task.CompletedTask : _ApplyAsync(Settings.WithUpdated(workspace.WithoutPane(paneId)));

    /// <summary>Moves a pane to <paramref name="cell"/> after a drag. Position only — the pane itself is never rebuilt, which is what keeps a dragged terminal from losing its pty (leermoment 2026-07-13).</summary>
    public Task MovePaneAsync(string paneId, GridCell cell) =>
        Active is not { } workspace ? Task.CompletedTask : _ApplyAsync(Settings.WithUpdated(workspace.WithPaneMoved(paneId, cell)));

    private async Task _ApplyAsync(WorkspaceSettings settings)
    {
        if (ReferenceEquals(settings, Settings))
        {
            return;
        }

        Settings = settings;
        if (_store is not null)
        {
            await _store.SaveAsync(settings);
        }
    }

    partial void OnSettingsChanged(WorkspaceSettings value)
    {
        _RefreshTabs();
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(ShowTabStrip));
        OnPropertyChanged(nameof(IsDashboardActive));
    }

    private void _RefreshTabs()
    {
        Tabs.Clear();
        foreach (var workspace in Settings.Workspaces)
        {
            Tabs.Add(new WorkspaceTabViewModel(workspace, isActive: workspace.Id == Active?.Id));
        }
    }

    /// <summary>"Dashboard", then "Dashboard 2", … — a name the operator can rename, but never a strip of identical tabs.</summary>
    private string _UniqueName(WorkspaceType type)
    {
        var baseName = type == WorkspaceType.Dashboard ? "Dashboard" : "Sessions";
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
}
