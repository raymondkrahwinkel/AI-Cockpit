using Avalonia.Threading;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Swarm;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

public partial class CockpitViewModel
{
    private ISwarmOptions? _swarmSidebar;
    private ISwarmLeadership? _swarmLeadership;
    private INodeSessionsClient? _swarmSessions;
    private DispatcherTimer? _swarmSidebarTimer;

    // The latest sidebar refresh, kept so a fault or a test can observe it.
    internal Task SwarmGroupsRefresh { get; private set; } = Task.CompletedTask;
    private readonly CancellationTokenSource _swarmSidebarStop = new();
    private bool _refreshingSwarm;
    private readonly HashSet<string> _swarmMemberNames = new(StringComparer.Ordinal);
    private string? _swarmSelfName;

    internal ISwarmLeadership? SwarmLeadership => _swarmLeadership;
    internal INodeSessionsClient? SwarmSessions => _swarmSessions;
    public string? AssistantLeadershipLabel => _swarmLeadership?.IsActive != true ? null
        : _swarmLeadership.MergeNotice ?? (LocalLeads ? "You lead" : null) ?? (_swarmLeadership.Current is { } leader ? $"Led by {leader.Name}" : "Waiting for a leader");
    public string? AssistantLocationLabel => _swarmLeadership?.IsActive != true ? null
        : $"You are on {_swarmSelfName ?? Environment.MachineName}. The assistant runs on {_swarmLeadership.Current?.Name ?? "the leader"}.";
    public bool AssistantLeaderIsServer => _swarmLeadership?.Current?.HostKind == SwarmHostKind.Server;

    public bool LocalLeads => _swarmLeadership?.IsSelf == true;
    public bool ShowLocalMachineHeading => !IsRemoteWindow && (HasServerGroups || _swarmSidebar is not null);

    internal void ShowSwarm(ISwarmOptions options, ISwarmLeadership leadership, INodeSessionsClient sessions, bool poll = true)
    {
        _swarmSidebar = options;
        _swarmLeadership = leadership;
        _swarmSessions = sessions;
        OnPropertyChanged(nameof(AssistantLeadershipLabel));
        OnPropertyChanged(nameof(AssistantLocationLabel));
        _remotePaneOver ??= SessionViewModel.OverRemoteAsync;
        leadership.Changed += _SwarmLeaderChanged;
        if (poll)
        {
            _swarmSidebarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _swarmSidebarTimer.Tick += _SwarmSidebarTick;
            _swarmSidebarTimer.Start();
            SwarmGroupsRefresh = RefreshSwarmGroupsAsync();
        }
        OnPropertyChanged(nameof(ShowLocalMachineHeading));
    }

    private void _SwarmSidebarTick(object? sender, EventArgs e) => SwarmGroupsRefresh = RefreshSwarmGroupsAsync();

    private void _SwarmLeaderChanged(object? sender, EventArgs e) => _OnUiThread(_UpdateSwarmLeader);

    private void _UpdateSwarmLeader()
    {
        OnPropertyChanged(nameof(LocalLeads));
        OnPropertyChanged(nameof(AssistantLeadershipLabel));
        OnPropertyChanged(nameof(AssistantLocationLabel));
        OnPropertyChanged(nameof(AssistantLeaderIsServer));
        foreach (var group in ServerGroups.Where(group => group.IsSwarmMember))
        {
            group.Leads = group.MemberId == _swarmLeadership?.Current?.Id;
        }
    }

    internal async Task RefreshSwarmGroupsAsync()
    {
        if (_refreshingSwarm || _swarmSidebar is null || _swarmSessions is null || _swarmSidebarStop.IsCancellationRequested)
        {
            return;
        }
        _refreshingSwarm = true;
        try
        {
            var machines = await _swarmSidebar.MachinesAsync(_swarmSidebarStop.Token);
            _swarmSelfName = machines.FirstOrDefault(machine => machine.IsSelf)?.Member.Name;
            var members = machines.Where(machine => !machine.IsSelf).ToArray();
            _swarmMemberNames.Clear();
            foreach (var member in members)
            {
                _swarmMemberNames.Add(member.Member.Name);
            }
            if (_remoteServers is not null)
            {
                _SyncServerGroups();
            }
            foreach (var group in ServerGroups.Where(group => group.IsSwarmMember && !members.Any(machine => machine.Member.Id == group.MemberId)).ToArray())
            {
                ServerGroups.Remove(group);
                group.Dispose();
                foreach (var row in group.Sessions)
                {
                    await _CloseRemotePaneAsync(row);
                }
            }
            foreach (var machine in members)
            {
                var group = ServerGroups.FirstOrDefault(group => group.MemberId == machine.Member.Id);
                if (group is null)
                {
                    group = new ServerGroupViewModel(machine);
                    group.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(ServerGroupViewModel.IsExpanded))
                        {
                            _OnOpenServerGroupsChanged();
                        }
                    };
                    group.Sessions.CollectionChanged += (_, _) => _WatchWaitingRows();
                    ServerGroups.Add(group);
                }
                group.UpdatePresence(machine.Presence);
                _ApplyServerLink(group);
                if (group.IsConnected)
                {
                    var snapshot = await _swarmSessions.ReadAsync(group.Name, _swarmSidebarStop.Token);
                    if (snapshot.Error is null)
                    {
                        foreach (var row in group.Reconcile(snapshot))
                        {
                            await _CloseRemotePaneAsync(row);
                        }
                    }
                    else
                    {
                        group.UpdatePresence(machine.Presence with { Status = SwarmPresence.NotReachable });
                        _ApplyServerLink(group);
                    }
                }
            }
            _UpdateSwarmLeader();
            OnPropertyChanged(nameof(HasServerGroups));
            OnPropertyChanged(nameof(ShowLocalMachineHeading));
            _OnOpenServerGroupsChanged();
        }
        catch (OperationCanceledException) when (_swarmSidebarStop.IsCancellationRequested) { }
        catch (Exception exception) { _logger?.LogWarning(exception, "Could not refresh swarm sidebar."); }
        finally { _refreshingSwarm = false; }
    }

    private async Task _StartOnMemberAsync(ServerGroupViewModel group)
    {
        if (_swarmSessions is null || !group.IsConnected)
        {
            group.Start.Error = $"{group.Name} is not reachable.";
            return;
        }
        if (group.Start.SelectedProfile is not { } profile)
        {
            group.Start.Error = "Pick a profile first.";
            return;
        }
        var started = await _swarmSessions.StartAsync(group.Name, profile.Label, group.Start.SelectedProject?.Id,
            group.Start.Prompt.Trim() is { Length: > 0 } prompt ? prompt : null, cancellationToken: _swarmSidebarStop.Token);
        if (started.Error is { } error)
        {
            group.Start.Error = error;
            return;
        }
        var snapshot = await _swarmSessions.ReadAsync(group.Name, _swarmSidebarStop.Token);
        if (snapshot.Error is { } readError)
        {
            group.Start.Error = $"Session started, but could not read it: {readError}";
            return;
        }
        foreach (var gone in group.Reconcile(snapshot))
        {
            await _CloseRemotePaneAsync(gone);
        }
        group.Start.IsOpen = false;
        group.Start.Prompt = "";
        if (group.Sessions.FirstOrDefault(row => row.Handle.PaneId == started.PaneId) is { } row)
        {
            await OpenServerSessionAsync(row);
        }
    }

    private void _StopSwarmSidebar()
    {
        _swarmSidebarStop.Cancel();
        foreach (var group in ServerGroups.Where(group => group.IsSwarmMember))
        {
            group.Dispose();
        }
        if (_swarmLeadership is not null)
        {
            _swarmLeadership.Changed -= _SwarmLeaderChanged;
        }
        if (_swarmSidebarTimer is not null)
        {
            _swarmSidebarTimer.Stop();
            _swarmSidebarTimer.Tick -= _SwarmSidebarTick;
        }
    }
}
