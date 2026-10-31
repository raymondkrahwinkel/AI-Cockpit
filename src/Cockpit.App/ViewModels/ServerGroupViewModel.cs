using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Core.Swarm;
using Cockpit.Core.Abstractions.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// AC-1456: one connect server in the session list, beside this laptop's desks. Its rows are the server's registry as
// the stream keeps it, so nothing here polls; a session outside the key's scope is not in that registry, and therefore
// neither listed nor counted. Built and reconciled on the UI thread only.
public sealed partial class ServerGroupViewModel : ObservableObject, IDisposable
{
    private readonly TimeProvider _time;
    private readonly ITimer _uptimeTimer;

    public ServerGroupViewModel(IRemoteServer server, Func<string, ILoginFlow?>? startSignIn = null, ILogger? logger = null, TimeProvider? time = null)
    {
        Server = server;
        Name = server.Name;
        _time = time ?? TimeProvider.System;
        _state = server.State;
        Start = new ServerStartViewModel(server.Name);
        Health = new ServerHealthViewModel(server, startSignIn, logger);
        _uptimeTimer = _time.CreateTimer(_ => OnPropertyChanged(nameof(HeaderDetail)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal ServerGroupViewModel(SwarmMachine machine, TimeProvider? time = null)
    {
        Name = machine.Member.Name;
        MemberId = machine.Member.Id;
        _time = time ?? TimeProvider.System;
        _state = new(false, null, null);
        Start = new ServerStartViewModel(Name);
        _uptimeTimer = _time.CreateTimer(_ => OnPropertyChanged(nameof(LastSeenLabel)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        UpdatePresence(machine.Presence);
    }

    public IRemoteServer? Server { get; }

    public SwarmMemberId? MemberId { get; }

    public bool IsSwarmMember => MemberId is not null;

    public bool IsConnectServer => !IsSwarmMember;

    [ObservableProperty] private bool _leads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(StatusLabel), nameof(SidebarStatus), nameof(LastSeenLabel), nameof(RowsOpacity), nameof(ShowStart))]
    private SwarmMemberPresence? _presence;

    public double RowsOpacity => Presence is { Status: not SwarmPresence.Online } ? 0.5 : 1;
    public bool ShowStart => IsExpanded && (!IsSwarmMember || IsConnected);

    public string LastSeenLabel => Presence is { Status: not SwarmPresence.Online } presence
        ? presence.LastSeen is { } at ? $"Last seen {at.ToLocalTime():HH:mm}" : "Last seen unknown"
        : "";

    internal void UpdatePresence(SwarmMemberPresence presence)
    {
        Presence = presence;
        foreach (var row in Sessions)
        {
            row.LastSeenLabel = LastSeenLabel;
        }
    }

    internal IReadOnlyList<ServerSessionRowViewModel> Reconcile(NodeSessionsSnapshot snapshot)
    {
        var gone = Sessions.Where(row => !snapshot.Sessions.Any(item => item.PaneId == row.Handle.PaneId)).ToList();
        foreach (var row in gone)
        {
            Sessions.Remove(row);
        }
        for (var index = 0; index < snapshot.Sessions.Count; index++)
        {
            var item = snapshot.Sessions[index];
            var row = Sessions.FirstOrDefault(candidate => candidate.Handle.PaneId == item.PaneId);
            if (row is null)
            {
                row = new ServerSessionRowViewModel(new SwarmSessionHandle(item));
                Sessions.Insert(index, row);
            }
            else
            {
                ((SwarmSessionHandle)row.Handle).Snapshot = item;
                if (Sessions.IndexOf(row) != index)
                {
                    Sessions.Move(Sessions.IndexOf(row), index);
                }
            }
            row.LastSeenLabel = LastSeenLabel;
            row.Refresh();
        }
        return gone;
    }

    public string Name { get; }

    public ObservableCollection<ServerSessionRowViewModel> Sessions { get; } = [];

    public ServerStartViewModel Start { get; }

    public ServerHealthViewModel? Health { get; }

    // While open, the server's header line stands above the grid (variant A, decided 2026-10-01).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStart))]
    private bool _isExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected),
        nameof(StatusLabel),
        nameof(SidebarStatus),
        nameof(StatusBarLabel),
        nameof(RemoteWindowStatus),
        nameof(HeaderDetail),
        nameof(IsOperateKey),
        nameof(IsAdminKey),
        nameof(HoldsAssistant),
        nameof(HasAssistantLine),
        nameof(AssistantTitle),
        nameof(AssistantDetail))]
    private RemoteServerState _state;

    // The assistant's name on the server, from its pane; "The assistant" until the server has one.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssistantTitle))]
    private string _assistantName = "The assistant";

    public bool IsConnected => IsSwarmMember ? Presence?.Status == SwarmPresence.Online : State.IsConnected;

    public string StatusLabel => IsSwarmMember ? Presence?.Status switch
    {
        SwarmPresence.Online => "online",
        SwarmPresence.Asleep => "asleep",
        _ => "unreachable",
    } : State switch
    {
        { KeyRefused: true } => "Key refused",
        { IsConnected: true, LatencyMs: { } latency } => $"Connected · {latency} ms",
        { IsConnected: true } => "Connected",
        _ => "Reconnecting",
    };

    // The sidebar row is narrower: the latency alone while connected, the state's word otherwise.
    public string SidebarStatus => !IsSwarmMember && State is { IsConnected: true, LatencyMs: { } latency } ? $"{latency} ms" : StatusLabel;

    public string StatusBarLabel => $"{Name}: {StatusLabel.ToLowerInvariant()}";

    // AC-1487: the `--remote` window's status bar names the key it works with; its label, never the key itself.
    public string RemoteWindowStatus => string.Join(" · ", new[]
    {
        "Remote",
        Name,
        SidebarStatus,
        State.Key is { } key ? $"key {key.Label} ({key.Capability})" : null,
    }.OfType<string>());

    public string HeaderDetail => State.Key is { } key
        ? string.Join(" · ", new[] { key.Version is { Length: > 0 } version ? $"v{version}" : null, _Uptime(key.StartedAt) }.OfType<string>())
        : "";

    // An operate key sees Admin locked (tab 6), so it knows the page exists; an admin key opens it (AC-1446).
    public bool IsOperateKey => string.Equals(State.Key?.Capability, "operate", StringComparison.OrdinalIgnoreCase);

    public bool IsAdminKey => string.Equals(State.Key?.Capability, "admin", StringComparison.OrdinalIgnoreCase);

    public bool HoldsAssistant => State.Key?.HoldsAssistant == true;

    public bool HasAssistantLine => State.Key is not null;

    public string AssistantTitle => State.Key switch
    {
        { HoldsAssistant: true } => $"{AssistantName} lives on {Name}",
        { AssistantHeldBy: { Length: > 0 } holder } => $"The assistant is held by {holder}",
        _ => $"No key holds the assistant on {Name}",
    };

    public string AssistantDetail => State.Key switch
    {
        { HoldsAssistant: true } key => $"Key \"{key.Label}\" holds the assistant. While this laptop holds the line, the assistant on {Name} stands down and you talk to your own assistant, which steers the node.",
        _ => "This key does not see the conversation.",
    };

    // Brings the rows in line with the server's registry, keeping each row (and its open pane) for a session still there.
    // Returns the rows whose session is gone, so the cockpit can close their panes.
    public IReadOnlyList<ServerSessionRowViewModel> Reconcile()
    {
        AssistantName = Server?.Sessions?.Assistant?.Title is { Length: > 0 } title ? title : "The assistant";
        var handles = Server?.Sessions?.All.Where(handle => !handle.IsEmbedded).ToList() ?? [];
        var gone = Sessions.Where(row => !handles.Contains(row.Handle)).ToList();
        foreach (var row in gone)
        {
            Sessions.Remove(row);
        }

        for (var index = 0; index < handles.Count; index++)
        {
            var row = Sessions.FirstOrDefault(existing => ReferenceEquals(existing.Handle, handles[index]));
            if (row is null)
            {
                Sessions.Insert(index, new ServerSessionRowViewModel(handles[index]));
                continue;
            }

            if (Sessions.IndexOf(row) != index)
            {
                Sessions.Move(Sessions.IndexOf(row), index);
            }

            row.Refresh();
        }

        return gone;
    }

    public void Dispose() => _uptimeTimer.Dispose();

    private string? _Uptime(DateTimeOffset? startedAt)
    {
        if (startedAt is not { } since)
        {
            return null;
        }

        var up = _time.GetUtcNow() - since;
        return up.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"up {(int)up.TotalDays} d {up.Hours} h")
            : string.Create(CultureInfo.InvariantCulture, $"up {up.Hours} h {up.Minutes} min");
    }
}

// AC-1456: one session on a connect server, as its row in the group. The pane is made when the row is first opened.
public sealed partial class ServerSessionRowViewModel : ObservableObject
{
    public ServerSessionRowViewModel(ISessionHandle handle)
    {
        Handle = handle;
        Refresh();
    }

    public ISessionHandle Handle { get; }

    // The machine badge the flat "Waiting for you" list shows on this row; null while the grouped list is showing.
    [ObservableProperty]
    private string? _machineBadge;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrushKey), nameof(Detail), nameof(StatusLabel))]
    private SessionStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string _profile = "";

    [ObservableProperty]
    private SessionPanelViewModel? _pane;

    public string Detail => $"{Profile} · {(LastSeenLabel.Length > 0 ? LastSeenLabel : StatusLabel)}";

    public string StatusLabel => _StatusWord(Status);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusline))]
    private string _statusline = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAttention))]
    private bool _needsAttention;
    [ObservableProperty] private string _activityBadge = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(ShowAttention), nameof(ShowStatusline), nameof(StatusBrushKey))]
    private string _lastSeenLabel = "";

    public bool ShowAttention => NeedsAttention && LastSeenLabel.Length == 0;
    public bool ShowStatusline => Statusline.Length > 0 && LastSeenLabel.Length == 0;

    public string StatusBrushKey => LastSeenLabel.Length > 0 ? "CockpitTextFaintBrush" : Status switch
    {
        SessionStatus.Busy => "CockpitStatusBusyBrush",
        SessionStatus.WorkingBackground => "CockpitStatusBackgroundBrush",
        SessionStatus.NeedsAttention or SessionStatus.Failed => "CockpitStatusWaitingBrush",
        SessionStatus.Done => "CockpitStatusDoneBrush",
        _ => "CockpitTextFaintBrush",
    };

    public void Refresh()
    {
        Title = Handle.Title;
        Status = Handle.SessionStatus;
        Profile = Handle.ActiveProfileLabel ?? "";
        Statusline = Handle.Statusline;
        NeedsAttention = Handle is SwarmSessionHandle remote ? remote.Snapshot.NeedsYou || Status == SessionStatus.NeedsAttention : Status == SessionStatus.NeedsAttention;
        ActivityBadge = Handle is SwarmSessionHandle member && member.Snapshot.HasOutstandingWork ? "Background work" : "";
        if (Pane is { } pane)
        {
            pane.Title = Title;
            pane.Statusline = Handle.Statusline;
            pane.SessionStatus = Status;
        }
    }

    private static string _StatusWord(SessionStatus status) => status switch
    {
        SessionStatus.Busy => "Busy",
        SessionStatus.WorkingBackground => "Working (background)",
        SessionStatus.NeedsAttention => "Needs attention",
        SessionStatus.Done => "Done",
        SessionStatus.Failed => "Failed",
        _ => "Idle",
    };
}

// AC-1456: "+ Start on <server>" as the card the mockup draws: the server's own projects and profiles, as far as this
// key's scope reaches, and a first message that goes along with the start. Only SDK, since the server runs it.
public sealed partial class ServerStartViewModel(string server) : ObservableObject
{
    public string Server { get; } = server;

    public string Heading => $"New session on {Server}";

    public string StartLabel => $"Start on {Server}";

    public string SidebarLabel => $"+ Start on {Server}";

    public string HelpText => $"Runs on {Server}. Profiles and projects are this machine's own. Choose No project to use its scratch directory.";

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _reason = "";

    [ObservableProperty]
    private string _prompt = "";

    public ObservableCollection<NodeProfileChoice> Profiles { get; } = [];

    public ObservableCollection<NodeProjectChoice> Projects { get; } = [];

    [ObservableProperty]
    private NodeProfileChoice? _selectedProfile;

    [ObservableProperty]
    private NodeProjectChoice? _selectedProject;

    // Under the profile when there is nothing to choose, so a scoped key reads as scoped rather than as broken.
    public string ProfileHint => Profiles.Count == 1 ? "The only profile this key may use" : "";

    // Under the project: the projects this key reaches, named, so the scope is visible where it bites.
    public string ProjectHint => string.Join(", ", Projects.Where(project => project.Id is not null).Select(project => project.Name));

    public void Fill(IEnumerable<NodeProfileChoice> profiles, IEnumerable<NodeProjectChoice> projects)
    {
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        Projects.Clear();
        // Null selects the server's scratch directory without changing profile or caller-scope authorization.
        Projects.Add(new NodeProjectChoice(null, "No project"));
        foreach (var project in projects)
        {
            Projects.Add(project);
        }

        SelectedProfile = Profiles.FirstOrDefault();
        SelectedProject = Projects.FirstOrDefault();
        OnPropertyChanged(nameof(ProfileHint));
        OnPropertyChanged(nameof(ProjectHint));
    }
}
