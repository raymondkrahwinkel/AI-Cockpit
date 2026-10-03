using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Toasts;
using Cockpit.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// AC-1456: the connect servers as groups in the session list (variant A). A remote pane is opened from its row and stands
// in the grid beside this laptop's panes, but never in `Sessions`: nothing local registers, persists or messages it.
public partial class CockpitViewModel
{
    private IRemoteServers? _remoteServers;
    private Func<ISessionHandle, string, Task<SessionViewModel>>? _remotePaneOver;
    private INodeSessionsClient? _serverChoices;
    private readonly List<SessionPanelViewModel> _remotePanes = [];

    public ObservableCollection<ServerGroupViewModel> ServerGroups { get; } = [];

    // What the session grid draws: `Sessions` in its own order, then the opened remote panes. Mirrored change by change,
    // never rebuilt, since a TTY pane's view owns its pty and a rebuilt container would close it.
    public ObservableCollection<SessionPanelViewModel> GridPanes { get; } = [];

    public bool HasServerGroups => ServerGroups.Count > 0;

    // The header lines above the grid: one per server whose group stands open.
    public IEnumerable<ServerGroupViewModel> OpenServerGroups => ServerGroups.Where(group => group.IsExpanded);

    private void _WireServerGroups(
        IRemoteServers? remoteServers,
        Func<ISessionHandle, string, Task<SessionViewModel>>? remotePaneOver,
        INodeSessionsClient? serverChoices)
    {
        if (remoteServers is null || remotePaneOver is null)
        {
            return;
        }

        _remoteServers = remoteServers;
        _remotePaneOver = remotePaneOver;
        _serverChoices = serverChoices;
        remoteServers.Changed += (_, _) => _OnUiThread(_SyncServerGroups);
        Sessions.CollectionChanged += (_, args) =>
        {
            foreach (var session in args.NewItems?.OfType<SessionPanelViewModel>() ?? [])
            {
                session.ShowsWhereItRuns = HasServerGroups;
            }
        };
        _SyncServerGroups();
        _ = _ReloadServersAsync();
    }

    // The headless scenes' way in: the same wiring over a stand-in server, without the node tools behind the start card.
    internal void ShowServers(IRemoteServers servers, Func<ISessionHandle, string, Task<SessionViewModel>> paneOver) =>
        _WireServerGroups(servers, paneOver, serverChoices: null);

    private void _MirrorSessionsIntoGrid()
    {
        foreach (var session in Sessions)
        {
            GridPanes.Add(session);
        }

        Sessions.CollectionChanged += (_, change) =>
        {
            switch (change.Action)
            {
                case NotifyCollectionChangedAction.Add when change.NewItems is { } added:
                    var at = change.NewStartingIndex < 0 ? Sessions.Count - added.Count : change.NewStartingIndex;
                    foreach (var session in added.OfType<SessionPanelViewModel>())
                    {
                        GridPanes.Insert(at++, session);
                    }

                    break;
                case NotifyCollectionChangedAction.Remove when change.OldItems is { } removed:
                    foreach (var session in removed.OfType<SessionPanelViewModel>())
                    {
                        GridPanes.Remove(session);
                    }

                    break;
                case NotifyCollectionChangedAction.Move:
                    GridPanes.Move(change.OldStartingIndex, change.NewStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Replace when change.NewItems is [SessionPanelViewModel replacement]:
                    GridPanes[change.NewStartingIndex] = replacement;
                    break;
                default:
                    foreach (var local in GridPanes.Except(_remotePanes).ToList())
                    {
                        GridPanes.Remove(local);
                    }

                    for (var index = 0; index < Sessions.Count; index++)
                    {
                        GridPanes.Insert(index, Sessions[index]);
                    }

                    break;
            }
        };
    }

    private async Task _ReloadServersAsync()
    {
        try
        {
            await (_remoteServers?.ReloadAsync() ?? Task.CompletedTask);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not read the connect servers from the registry.");
        }
    }

    private void _SyncServerGroups()
    {
        var servers = _remoteServers?.Servers ?? [];
        foreach (var group in ServerGroups.Where(group => !servers.Contains(group.Server)).ToList())
        {
            ServerGroups.Remove(group);
            foreach (var row in group.Sessions)
            {
                _ = _CloseRemotePaneAsync(row);
            }
        }

        foreach (var server in servers.Where(server => ServerGroups.All(group => !ReferenceEquals(group.Server, server))))
        {
            var group = new ServerGroupViewModel(server);
            group.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ServerGroupViewModel.IsExpanded))
                {
                    _OnOpenServerGroupsChanged();
                }
            };
            ISessionRegistry? followed = null;
            void Follow()
            {
                if (server.Sessions is { } registry && !ReferenceEquals(registry, followed))
                {
                    followed = registry;
                    registry.Changed += (_, _) => _OnUiThread(() => _ReconcileServerGroup(group));
                }
            }

            server.StateChanged += (_, _) => _OnUiThread(() =>
            {
                Follow();
                group.State = server.State;
                _ReconcileServerGroup(group);
                OnPropertyChanged(nameof(ServerStatusLabels));
            });
            Follow();
            ServerGroups.Add(group);
            _ReconcileServerGroup(group);
        }

        OnPropertyChanged(nameof(HasServerGroups));
        OnPropertyChanged(nameof(ServerStatusLabels));
        _OnOpenServerGroupsChanged();
        foreach (var session in Sessions)
        {
            session.ShowsWhereItRuns = HasServerGroups;
        }
    }

    private void _OnOpenServerGroupsChanged()
    {
        OnPropertyChanged(nameof(OpenServerGroups));
        OnPropertyChanged(nameof(ShowSessionGrid));
        OnPropertyChanged(nameof(ShowSessionEmptyState));
    }

    private void _ReconcileServerGroup(ServerGroupViewModel group)
    {
        foreach (var row in group.Reconcile())
        {
            _ = _CloseRemotePaneAsync(row);
        }
    }

    // "huis-cockpit: connected · 38 ms" in the status bar, one per server.
    public IEnumerable<string> ServerStatusLabels => ServerGroups.Select(group => group.StatusBarLabel).ToList();

    [RelayCommand]
    private async Task OpenServerSessionAsync(ServerSessionRowViewModel row)
    {
        if (row.Pane is null)
        {
            if (_remotePaneOver is null || ServerGroups.FirstOrDefault(group => group.Sessions.Contains(row)) is not { } group)
            {
                return;
            }

            var pane = await _remotePaneOver(row.Handle, group.Name);
            pane.ShowsWhereItRuns = true;
            row.Pane = pane;
            _remotePanes.Add(pane);
            GridPanes.Add(pane);
            _OnRemotePanesChanged();
        }

        if (Workspaces.Active is { } active && active.Type != WorkspaceType.Sessions
            && SessionWorkspacePlacement.FirstSessionsWorkspaceId(Workspaces.Settings) is { } sessionsDesk)
        {
            Workspaces.SelectWorkspaceCommand.Execute(sessionsDesk);
        }

        SelectedSession = row.Pane;
    }

    // Stop on the server, after the one confirmation; a stop that does not reach the server leaves the pane standing.
    private async Task _StopRemoteSessionAsync(SessionPanelViewModel pane)
    {
        if (ServerGroups.SelectMany(group => group.Sessions.Select(row => (Group: group, Row: row)))
                .FirstOrDefault(entry => ReferenceEquals(entry.Row.Pane, pane)) is not { Row: { } row } entry
            || entry.Group.Server.Launcher is not { } launcher)
        {
            return;
        }

        try
        {
            await launcher.StopSessionAsync(row.Handle.PaneId);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not stop {Session} on {Server}.", row.Title, entry.Group.Name);
            ToastHost.Add($"Could not stop \"{row.Title}\" on {entry.Group.Name}: {exception.Message}", ToastSeverity.Error, null, null);
            return;
        }

        await _CloseRemotePaneAsync(row);
    }

    private async Task _CloseRemotePaneAsync(ServerSessionRowViewModel row)
    {
        if (row.Pane is not { } pane)
        {
            return;
        }

        row.Pane = null;
        _remotePanes.Remove(pane);
        GridPanes.Remove(pane);
        if (ReferenceEquals(SelectedSession, pane))
        {
            SelectedSession = VisibleSessions.FirstOrDefault(session => !ReferenceEquals(session, pane));
        }

        _OnRemotePanesChanged();
        try
        {
            await pane.DisposeAsync();
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "A remote pane did not close cleanly.");
        }
    }

    private void _OnRemotePanesChanged()
    {
        OnPropertyChanged(nameof(VisibleSessions));
        OnPropertyChanged(nameof(GridColumns));
        OnPropertyChanged(nameof(ShowZoomButton));
        OnPropertyChanged(nameof(ShowSessionGrid));
        OnPropertyChanged(nameof(ShowSessionEmptyState));
        OnPropertyChanged(nameof(HasSessionsHere));
        RefreshPaneVisibility();
    }

    [RelayCommand]
    private async Task DisconnectServerAsync(ServerGroupViewModel group)
    {
        if (_remoteServers is not null)
        {
            await _remoteServers.DisconnectAsync(group.Name);
        }
    }

    [RelayCommand]
    private async Task OpenServerStartAsync(ServerGroupViewModel group)
    {
        group.IsExpanded = true;
        group.Start.Error = "";
        group.Start.IsOpen = true;
        if (_serverChoices is null)
        {
            return;
        }

        group.Start.IsBusy = true;
        try
        {
            // The node tools on the same key: the server filters both lists to its scope (AC-1367).
            var snapshot = await _serverChoices.ReadAsync(group.Name);
            if (snapshot.Error is { Length: > 0 } error)
            {
                group.Start.Error = error;
                return;
            }

            group.Start.Fill(
                snapshot.Profiles.Select(profile => new NodeProfileChoice(profile.Label, profile.Purpose)),
                snapshot.Projects.Select(project => new NodeProjectChoice(project.Id, project.Name)));
        }
        finally
        {
            group.Start.IsBusy = false;
        }
    }

    [RelayCommand]
    private static void CancelServerStart(ServerGroupViewModel group) => group.Start.IsOpen = false;

    [RelayCommand]
    private async Task StartOnServerAsync(ServerGroupViewModel group)
    {
        if (group.Server.Launcher is not { } launcher || group.Start.SelectedProfile is not { } profile)
        {
            group.Start.Error = "Pick a profile first.";
            return;
        }

        group.Start.IsBusy = true;
        try
        {
            var prompt = group.Start.Prompt.Trim();
            var started = await launcher.StartSessionAsync(new SessionLaunchRequest(
                launcher.Workspaces.ActiveWorkspaceId ?? "",
                new SessionProfile(profile.Label, new ServerProvider()),
                prompt.Length == 0 ? null : prompt,
                WorkingDirectory: null,
                SessionName: null,
                Kind: PaneSessionKind.Sdk,
                LaunchOptions: null,
                IsolateInWorktree: null,
                ProjectId: group.Start.SelectedProject?.Id,
                StartedByTheAssistant: false));
            if (started is null)
            {
                group.Start.Error = $"{group.Name} did not start the session.";
                return;
            }

            group.Start.IsOpen = false;
            group.Start.Prompt = "";
            _ReconcileServerGroup(group);
            if (group.Sessions.FirstOrDefault(row => row.Handle.PaneId == started.PaneId) is { } row)
            {
                await OpenServerSessionAsync(row);
            }
        }
        catch (Exception exception)
        {
            group.Start.Error = exception.Message;
        }
        finally
        {
            group.Start.IsBusy = false;
        }
    }

    // The server reads only the label; the provider config is the server's own, and never crosses.
    private sealed record ServerProvider() : ProviderConfig(default(SessionProvider));
}
