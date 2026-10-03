using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1456: one connect server in the session list, beside this laptop's desks. Its rows are the server's registry as
// the stream keeps it, so nothing here polls; a session outside the key's scope is not in that registry, and therefore
// neither listed nor counted. Built and reconciled on the UI thread only.
public sealed partial class ServerGroupViewModel : ObservableObject
{
    public ServerGroupViewModel(IRemoteServer server)
    {
        Server = server;
        Name = server.Name;
        _state = server.State;
        Start = new ServerStartViewModel(server.Name);
    }

    public IRemoteServer Server { get; }

    public string Name { get; }

    public ObservableCollection<ServerSessionRowViewModel> Sessions { get; } = [];

    public ServerStartViewModel Start { get; }

    // While open, the server's header line stands above the grid (variant A, decided 2026-10-01).
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected),
        nameof(StatusLabel),
        nameof(SidebarStatus),
        nameof(StatusBarLabel),
        nameof(HeaderDetail),
        nameof(IsOperateKey),
        nameof(HoldsAssistant),
        nameof(HasAssistantLine),
        nameof(AssistantTitle),
        nameof(AssistantDetail))]
    private RemoteServerState _state;

    // The assistant's name on the server, from its pane; "The assistant" until the server has one.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssistantTitle))]
    private string _assistantName = "The assistant";

    public bool IsConnected => State.IsConnected;

    public string StatusLabel => State switch
    {
        { KeyRefused: true } => "Key refused",
        { IsConnected: true, LatencyMs: { } latency } => $"Connected · {latency} ms",
        { IsConnected: true } => "Connected",
        _ => "Reconnecting",
    };

    // The sidebar row is narrower: the latency alone while connected, the state's word otherwise.
    public string SidebarStatus => (State is { IsConnected: true, LatencyMs: { } latency } ? $"{latency} ms" : StatusLabel) + " · +1 hidden";

    public string StatusBarLabel => $"{Name}: {StatusLabel.ToLowerInvariant()}";

    public string HeaderDetail => State.Key is { } key
        ? string.Join(" · ", new[] { key.Version is { Length: > 0 } version ? $"v{version}" : null, _Uptime(key.StartedAt) }.OfType<string>())
        : "";

    // Admin stays out of sight for a key that may use it until F5.6b (AC-1446); an operate key sees the lock (tab 6).
    public bool IsOperateKey => string.Equals(State.Key?.Capability, "operate", StringComparison.OrdinalIgnoreCase);

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
        { HoldsAssistant: true } key => $"This laptop is a window onto the assistant there. Key \"{key.Label}\" holds the assistant: voice and the chat window go there.",
        _ => "This key does not see the conversation.",
    };

    // Brings the rows in line with the server's registry, keeping each row (and its open pane) for a session still there.
    // Returns the rows whose session is gone, so the cockpit can close their panes.
    public IReadOnlyList<ServerSessionRowViewModel> Reconcile()
    {
        AssistantName = Server.Sessions?.Assistant?.Title is { Length: > 0 } title ? title : "The assistant";
        var handles = Server.Sessions?.All.Where(handle => !handle.IsEmbedded).ToList() ?? [];
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

    private static string? _Uptime(DateTimeOffset? startedAt)
    {
        if (startedAt is not { } since)
        {
            return null;
        }

        var up = DateTimeOffset.UtcNow - since;
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

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrushKey), nameof(Detail))]
    private SessionStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string _profile = "";

    [ObservableProperty]
    private SessionPanelViewModel? _pane;

    public string Detail => $"{Profile} · {_StatusWord(Status)}";

    public string StatusBrushKey => Status switch
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

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _error = "";

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
