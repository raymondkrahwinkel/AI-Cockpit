using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Plugins;
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
    private IServerSignIns? _serverSignIns;
    private Func<ISessionHandle, string, Task<SessionViewModel>>? _remotePaneOver;
    private INodeSessionsClient? _serverChoices;
    private readonly List<SessionPanelViewModel> _remotePanes = [];
    private Func<ServerAdminViewModel, Task>? _showServerAdmin;
    private IPluginProviderConfigViews? _providerConfigViews;

    public ObservableCollection<ServerGroupViewModel> ServerGroups { get; } = [];

    // What the session grid draws: `Sessions` in its own order, then the opened remote panes. Mirrored change by change,
    // never rebuilt, since a TTY pane's view owns its pty and a rebuilt container would close it.
    public ObservableCollection<SessionPanelViewModel> GridPanes { get; } = [];

    public bool HasServerGroups => ServerGroups.Count > 0;

    // AC-1487: the server a `--remote` window shows; null in the cockpit itself. The window then hides what is local.
    public string? RemoteWindowServer { get; private init; }

    public bool IsRemoteWindow => RemoteWindowServer is not null;

    // "Remote · huis-cockpit · 38 ms · key laptop-raymond (admin)" in the remote window's status bar (mockup v2 tab 5).
    public string RemoteWindowStatus => ServerGroups.FirstOrDefault()?.RemoteWindowStatus ?? $"Remote · {RemoteWindowServer} · connecting";

    // The header lines above the grid: one per server whose group stands open.
    public IEnumerable<ServerGroupViewModel> OpenServerGroups => ServerGroups.Where(group => group.IsExpanded);

    private void _WireServerGroups(
        IRemoteServers? remoteServers,
        Func<ISessionHandle, string, Task<SessionViewModel>>? remotePaneOver,
        INodeSessionsClient? serverChoices,
        IServerSignIns? signIns = null)
    {
        if (remoteServers is null || remotePaneOver is null)
        {
            return;
        }

        _remoteServers = remoteServers;
        _serverSignIns = signIns;
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

    // AC-1487: the `--remote` window's view model: no local session, store or backend, and Admin shown by the window.
    internal static CockpitViewModel ForRemoteWindow(
        string server,
        IRemoteServers servers,
        INodeSessionsClient? serverChoices,
        IServerSignIns? signIns,
        Func<ServerAdminViewModel, Task> showServerAdmin)
    {
        var cockpit = new CockpitViewModel(dockPanelRegistry: null, sessionRegistry: null, withSamples: false)
        {
            RemoteWindowServer = server,
            _showServerAdmin = showServerAdmin,
        };
        cockpit._WireServerGroups(servers, SessionViewModel.OverRemoteAsync, serverChoices, signIns);
        return cockpit;
    }

    // The headless scenes' way in: the same wiring over a stand-in server, without the node tools behind the start card.
    internal void ShowServers(IRemoteServers servers, Func<ISessionHandle, string, Task<SessionViewModel>> paneOver, IServerSignIns? signIns = null) =>
        _WireServerGroups(servers, paneOver, serverChoices: null, signIns);

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
            group.Dispose();
            ServerGroups.Remove(group);
            foreach (var row in group.Sessions)
            {
                _ = _CloseRemotePaneAsync(row);
            }
        }

        foreach (var server in servers.Where(server => ServerGroups.All(group => !ReferenceEquals(group.Server, server))))
        {
            var group = new ServerGroupViewModel(server, profile => _serverSignIns?.Start(server.Name, profile, CancellationToken.None), _logger);

            // Subscribed before the state is read again below, so a change raised in between is not lost.
            group.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ServerGroupViewModel.IsExpanded))
                {
                    _OnOpenServerGroupsChanged();
                }
            };
            group.Health.OpenRequested += workflow => _OnUiThread(() => _OpenWorkflowSession(group, workflow));
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
                _ApplyServerLink(group);
                OnPropertyChanged(nameof(ServerStatusLabels));
                OnPropertyChanged(nameof(RemoteWindowStatus));
            });
            Follow();
            group.State = server.State;
            ServerGroups.Add(group);
            _ReconcileServerGroup(group);
        }

        OnPropertyChanged(nameof(HasServerGroups));
        OnPropertyChanged(nameof(ServerStatusLabels));
        OnPropertyChanged(nameof(RemoteWindowStatus));
        _OnOpenServerGroupsChanged();
        foreach (var session in Sessions)
        {
            session.ShowsWhereItRuns = HasServerGroups;
        }
    }

    // AC-1469: while the line is down the panes' composers and permission buttons are off; they come back on their own.
    private static void _ApplyServerLink(ServerGroupViewModel group)
    {
        foreach (var row in group.Sessions)
        {
            if (row.Pane is SessionViewModel pane)
            {
                _ApplyLink(pane, group);
            }
        }
    }

    // Up only once /whoami answered too, so the grant is known before a button is drawn or withheld.
    private static void _ApplyLink(SessionViewModel pane, ServerGroupViewModel group) =>
        pane.SetRemoteLink(
            group.State is { IsConnected: true, Key: not null },
            group.State.Key?.MayAnswerPermissions == true,
            group.State.Key?.AllowsForSession == true);

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

    [RelayCommand]
    private static void ToggleServerHealth(ServerGroupViewModel group) => group.Health.IsOpen = !group.Health.IsOpen;

    // A run waiting for a permission is a session of the same name on that server; without one, the first session
    // that needs attention stands in.
    private void _OpenWorkflowSession(ServerGroupViewModel group, string workflow)
    {
        var row = group.Sessions.FirstOrDefault(candidate => string.Equals(candidate.Title, workflow, StringComparison.OrdinalIgnoreCase))
            ?? group.Sessions.FirstOrDefault(candidate => candidate.Status == SessionStatus.NeedsAttention);
        if (row is not null)
        {
            OpenServerSessionCommand.Execute(row);
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
            _ApplyLink(pane, group);
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
        if (_remoteServers is null)
        {
            return;
        }

        try
        {
            await _remoteServers.DisconnectAsync(group.Name);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not disconnect from {Server}.", group.Name);
            ToastHost.Add($"Could not disconnect from {group.Name}: {exception.Message}", ToastSeverity.Error, null, null);
        }
    }

    // AC-1446: the Options dialog on the server, for an admin key. The server's profiles and projects, read on the same
    // key, are what the scope editor offers; without them it still offers "all".
    [RelayCommand]
    private async Task OpenServerAdminAsync(ServerGroupViewModel group)
    {
        var show = _showServerAdmin ?? (_dialogService is { } dialogs ? dialogs.ShowServerAdminDialogAsync : null);
        if (show is null || group.State.Key is not { } key)
        {
            return;
        }

        IReadOnlyList<string> profiles = [];
        IReadOnlyList<NodeProjectChoice> projects = [];
        if (_serverChoices is not null)
        {
            try
            {
                var snapshot = await _serverChoices.ReadAsync(group.Name);
                profiles = [.. snapshot.Profiles.Select(profile => profile.Label)];
                projects = [.. snapshot.Projects.Select(project => new NodeProjectChoice(project.Id, project.Name))];
            }
            catch (Exception exception)
            {
                _logger?.LogInformation(exception, "Could not read the profiles and projects of {Server}.", group.Name);
            }
        }

        var assistant = new ServerAssistantViewModel(group.Name, group.Server.AssistantAdministration, group.Server.Profiles);
        var plugins = group.Server.Plugins;
        await Plugins.LoadAsync();
        var admin = new ServerAdminViewModel(group.Name, key.Label, group.Server.Administration, profiles, projects, plugins, group.Server.Projects, dialogService: _dialogService, runningSessions: () => group.Sessions.Count, logger: _logger, serverVersion: key.Version)
        {
            Profiles = new ServerProfilesViewModel(new ServerProfileServices(group.Name, group.Server.Profiles, _serverSignIns, plugins, LocalPluginUiPart)),
            Assistant = assistant,
            HasPluginSettings = row => plugins is IPluginChannelRelay && (_ServerSettingsFor(row) is not null || _LocalSettingsPartFor(row) is not null),
            OpenPluginSettings = row => _OpenServerPluginSettingsAsync(group.Name, plugins, group.Server.Profiles, group.Server.Projects, row),
            IsPluginOnDesktop = row => Plugins.Plugins.Any(local => local.FolderId == PluginFolderName.Normalize(row.Id)),
            InstallPluginOnDesktop = row => Plugins.InstallFromStoreByIdAsync(row.Id),
            RestartDesktop = () => _appRestart?.Restart(),
        };

        // AC-1475: "Open Server health" on the Assistant page opens this group's Health tab (AC-1457).
        assistant.OpenHealthRequested += () => _OnUiThread(() =>
        {
            group.IsExpanded = true;
            group.Health.IsOpen = true;
        });
        await show(admin);
    }

    // AC-1511: the view the plugin installed here offers for a server, keyed like its own settings.
    private (string PluginName, Func<IPluginUiChannel, Control> CreateView)? _ServerSettingsFor(ServerPluginRowViewModel row) =>
        PluginServerSettings.TryGetValue(row.Id, out var byId) ? byId
        : PluginServerSettings.TryGetValue(row.FolderId, out var byFolder) ? byFolder
        : null;

    // AC-1571: the UI part of the same plugin on this desktop, for opening its own settings view against the server.
    // Set once the UI parts have initialized.
    internal Func<string, (Type UiType, ICockpitUiHost Host)?> LocalPluginUiPart { get; set; } = _ => null;

    private (Type UiType, ICockpitUiHost Host)? _LocalSettingsPartFor(ServerPluginRowViewModel row) =>
        LocalPluginUiPart(row.Id) is { Host.HasSettings: true } part ? part : null;

    // Opened beside the Admin dialog, with a channel that goes to the plugin's backend part on that server. The plugin's
    // local settings view over the server's storage wins (AC-1571); a view it offers only for a server (AC-1511) is the
    // fallback for a plugin without one.
    private async Task _OpenServerPluginSettingsAsync(string server, IPluginAdministration? plugins, IServerProfiles profiles, IServerProjects projects, ServerPluginRowViewModel row)
    {
        if (_dialogService is null || plugins is not IPluginChannelRelay relay)
        {
            return;
        }

        if (_LocalSettingsPartFor(row) is { } part)
        {
            if (await ServerPluginSettings.OpenAsync(part.UiType, part.Host, relay, row.FolderId, () => _PluginProfilesAsync(profiles), () => _PluginProjectsAsync(projects)) is { } remote)
            {
                await _dialogService.ShowServerPluginSettingsAsync($"{row.Name} on {server}", remote.CreateView, remote.PersistAsync);
            }

            return;
        }

        if (_ServerSettingsFor(row) is { } settings)
        {
            var channel = new RemotePluginUiChannel(relay, row.FolderId);
            await _dialogService.ShowServerPluginSettingsAsync($"{settings.PluginName} on {server}", () => settings.CreateView(channel));
        }
    }

    // AC-1571: the server's profiles as a plugin's host gives them, from the Admin API's own list. No config directory:
    // that path stays on the server.
    private static async Task<IReadOnlyList<PluginProfileInfo>> _PluginProfilesAsync(IServerProfiles profiles) =>
        [.. (await profiles.ListAsync()).Select(profile => new PluginProfileInfo(profile.Label, profile.Provider, "")
        {
            ModelSuggestions = _Known(profile, WellKnownPluginSessionOptions.Model),
            EffortSuggestions = _Known(profile, WellKnownPluginSessionOptions.Effort),
        })];

    // AC-1737: the server's projects, read again from its API so a project added there since shows up.
    private static async Task<IReadOnlyList<PluginProjectInfo>> _PluginProjectsAsync(IServerProjects projects)
    {
        await projects.LoadAsync();
        return [.. projects.Current.Settings.Projects.Select(project => new PluginProjectInfo(project.Id, project.Name))];
    }

    private static IReadOnlyList<string> _Known(RemoteProfile profile, string option) =>
        profile.KnownValues?.GetValueOrDefault(option) is { } values ? [.. values.Select(value => value.Value)] : [];

    [RelayCommand]
    private async Task OpenServerStartAsync(ServerGroupViewModel group)
    {
        group.IsExpanded = true;
        group.Start.Error = "";
        group.Start.Reason = "";
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

            // AC-1472: the projects from the backend API, the same list and scope Admin's Projects page writes to.
            await group.Server.Projects.LoadAsync();
            group.Start.Fill(
                snapshot.Profiles.Select(profile => new NodeProfileChoice(profile.Label, profile.Purpose)),
                group.Server.Projects.Current.Settings.Projects.Select(project => new NodeProjectChoice(project.Id, project.Name)));
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

            if (started.FailureReason is { Length: > 0 } reason)
            {
                group.Start.Error = $"{group.Name} did not start the session.";
                group.Start.Reason = reason;
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
