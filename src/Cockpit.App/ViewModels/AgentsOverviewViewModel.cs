using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

public sealed partial class AgentsOverviewViewModel : ObservableObject
{
    public const string DockPanelId = "agents";

    private readonly CockpitViewModel _cockpit;
    private readonly Dictionary<INotifyPropertyChanged, AgentOverviewCardViewModel> _cards = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private readonly List<IServerProjects> _serverProjects = [];

    [ObservableProperty] private bool _showOnlyWaiting;
    [ObservableProperty] private bool _showDelegatedTasks;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedMachine = "All machines";
    [ObservableProperty] private string _selectedProject = "All projects";
    [ObservableProperty] private bool _showOlder;
    private bool _restoring;

    internal void RestoreFilters()
    {
        _restoring = true;
        ShowOnlyWaiting = _cockpit.AgentsFilters.WaitingOnly;
        ShowDelegatedTasks = _cockpit.AgentsFilters.DelegatedTasks;
        SearchText = _cockpit.AgentsFilters.Search;
        SelectedMachine = _cockpit.AgentsFilters.Machine;
        SelectedProject = _cockpit.AgentsFilters.Project;
        _restoring = false;
    }

    private async Task _SaveFiltersAsync()
    {
        try
        {
            await _cockpit.SaveAgentsFiltersAsync(new(ShowOnlyWaiting, ShowDelegatedTasks, SearchText, SelectedMachine, SelectedProject));
        }
        catch (Exception exception)
        {
            Program.Services?.GetService<ILogger<AgentsOverviewViewModel>>()?.LogWarning(exception, "Could not save Agents filters.");
        }
    }

    public ObservableCollection<string> Machines { get; } = ["All machines"];
    public ObservableCollection<string> Projects { get; } = ["All projects"];
    public int WaitingCount => _cards.Values.Count(card => card.IsWaitingForYou);
    public bool ShowsMore => !ShowOlder && _cards.Values.Any(card => !card.NeedsAttention
        && card.Status is not (SessionStatus.Busy or SessionStatus.WorkingBackground)
        && card.ActivityAt < DateTimeOffset.UtcNow.AddHours(-24));
    public IRelayCommand ShowMoreCommand => new RelayCommand(() => ShowOlder = true);

    internal void RefreshCards()
    {
        foreach (var card in _cards.Values)
        {
            card.Refresh();
            _Place(card);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (args.PropertyName is nameof(ShowOnlyWaiting) or nameof(ShowDelegatedTasks) or nameof(SearchText)
            or nameof(SelectedMachine) or nameof(SelectedProject) or nameof(ShowOlder))
        {
            foreach (var card in _cards.Values)
            {
                _Place(card);
            }
            if (!_restoring && args.PropertyName != nameof(ShowOlder))
            {
                _ = _SaveFiltersAsync();
            }
        }
    }

    public IReadOnlyList<AgentsOverviewColumnViewModel> Columns { get; } =
    [
        new("Working", "CockpitStatusBusyBrush"),
        new("Waiting for you", "CockpitStatusWaitingBrush"),
        new("Done", "CockpitStatusDoneBrush"),
        new("Idle", "CockpitTextFaintBrush"),
    ];

    public AgentsOverviewViewModel(CockpitViewModel cockpit)
    {
        _cockpit = cockpit;
        cockpit.DelegatedTasks.Tasks.CollectionChanged += _OnCollectionChanged;
        cockpit.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CockpitViewModel.SelectedSession))
            {
                foreach (var card in _cards.Values)
                {
                    card.Refresh();
                }
            }
        };
        _Reconcile();
        RestoreFilters();
    }

    private void _Reconcile()
    {
        var catalogs = _cockpit.ServerGroups.Select(group => group.Server?.Projects).OfType<IServerProjects>().ToList();
        foreach (var catalog in _serverProjects.Where(catalog => !catalogs.Contains(catalog)).ToList())
        {
            catalog.Changed -= _OnProjectsChanged;
            _serverProjects.Remove(catalog);
        }

        foreach (var catalog in catalogs.Where(catalog => !_serverProjects.Contains(catalog)))
        {
            _serverProjects.Add(catalog);
            catalog.Changed += _OnProjectsChanged;
            _ = _LoadProjectsAsync(catalog);
        }

        foreach (var collection in _collections)
        {
            collection.CollectionChanged -= _OnCollectionChanged;
        }

        _collections.Clear();
        _collections.Add(_cockpit.Sessions);
        _collections.Add(_cockpit.ServerGroups);
        _collections.Add(_cockpit.Projects.Projects);
        _collections.AddRange(_cockpit.ServerGroups.Select(group => group.Sessions));
        foreach (var collection in _collections)
        {
            collection.CollectionChanged += _OnCollectionChanged;
        }

        var sources = _cockpit.Sessions.Cast<INotifyPropertyChanged>()
            .Concat(_cockpit.ServerGroups.SelectMany(group => group.Sessions))
            .Concat(_cockpit.DelegatedTasks.Tasks).ToList();
        foreach (var source in _cards.Keys.Where(source => !sources.Contains(source)).ToList())
        {
            var card = _cards[source];
            source.PropertyChanged -= _OnCardChanged;
            card.Dispose();
            foreach (var column in Columns)
            {
                column.Cards.Remove(card);
            }

            _cards.Remove(source);
        }

        foreach (var source in sources)
        {
            if (!_cards.TryGetValue(source, out var card))
            {
                card = new AgentOverviewCardViewModel(source, _cockpit);
                _cards.Add(source, card);
                source.PropertyChanged += _OnCardChanged;
            }

            card.Refresh();
            _Place(card);
        }
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(ShowsMore));
    }

    private void _OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => _Reconcile();

    private void _OnProjectsChanged() => Dispatcher.UIThread.Post(() =>
    {
        RefreshCards();
    });

    private static async Task _LoadProjectsAsync(IServerProjects catalog)
    {
        try
        {
            await catalog.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Program.Services?.GetService<ILogger<AgentsOverviewViewModel>>()?.LogWarning(exception, "Could not read server project names for the Agents panel.");
        }
    }

    private void _OnCardChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is INotifyPropertyChanged source && _cards.TryGetValue(source, out var card))
        {
            card.Refresh();
            _Place(card);
        }
    }

    private void _Place(AgentOverviewCardViewModel card)
    {
        var target = Columns[card.NeedsAttention ? 1 : card.Status switch
        {
            SessionStatus.Busy or SessionStatus.WorkingBackground => 0,
            SessionStatus.Done => 2,
            _ => 3,
        }];
        bool visible = (!ShowOnlyWaiting || card.IsWaitingForYou)
            && (ShowDelegatedTasks || !card.IsDelegatedTask)
            && (ShowOlder || card.NeedsAttention || card.Status is SessionStatus.Busy or SessionStatus.WorkingBackground
                || card.ActivityAt >= DateTimeOffset.UtcNow.AddHours(-24))
            && (SelectedMachine == "All machines" || SelectedMachine == card.Machine)
            && (SelectedProject == "All projects" || SelectedProject == card.Project)
            && (card.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || card.Statusline.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        if (!Machines.Contains(card.Machine))
        {
            Machines.Add(card.Machine);
        }
        if (!Projects.Contains(card.Project))
        {
            Projects.Add(card.Project);
        }
        foreach (var column in Columns)
        {
            if (column != target || !visible)
            {
                column.Cards.Remove(card);
            }
        }

        if (visible && !target.Cards.Contains(card))
        {
            target.Cards.Add(card);
        }
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(ShowsMore));
    }
}

public sealed class AgentsOverviewColumnViewModel(string title, string brushKey)
{
    public string Title { get; } = title;
    public string BrushKey { get; } = brushKey;
    public ObservableCollection<AgentOverviewCardViewModel> Cards { get; } = [];
}

public sealed partial class AgentOverviewCardViewModel : ObservableObject, IDisposable
{
    private readonly SessionPanelViewModel? _session;
    private readonly ServerSessionRowViewModel? _serverSession;
    private readonly CockpitViewModel _cockpit;
    private readonly ServerGroupViewModel? _serverGroup;
    private readonly DelegatedTaskRowViewModel? _task;
    private readonly ObservableCollection<ChildAgentViewModel> _emptyChildren = [];
    private SessionPanelViewModel? _childOwner;

    [ObservableProperty]
    private bool _areChildrenExpanded;

    internal AgentOverviewCardViewModel(INotifyPropertyChanged source, CockpitViewModel cockpit)
    {
        _cockpit = cockpit;
        _session = source as SessionPanelViewModel;
        _task = source as DelegatedTaskRowViewModel;
        _serverSession = source as ServerSessionRowViewModel;
        _serverGroup = _serverSession is null ? null : cockpit.ServerGroups.First(group => group.Sessions.Contains(_serverSession));
        Machine = _serverGroup?.Name ?? "here";
        OpenCommand = new AsyncRelayCommand(async () =>
        {
            if (_task is not null)
            {
                cockpit.DelegatedTasks.SelectedTask = _task;
                await cockpit.ShowDelegatedTasksCommand.ExecuteAsync(null);
                return;
            }
            if (_serverSession is not null)
            {
                await cockpit.OpenServerSessionCommand.ExecuteAsync(_serverSession);
            }
            else if (_session is not null)
            {
                cockpit.SelectSessionCommand.Execute(_session);
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                WindowActivation.BringToFront(main);
            }
        });
        OpenChildCommand = new AsyncRelayCommand<ChildAgentViewModel>(async child =>
        {
            SessionPanelViewModel? owner = _Owner;
            if (child is null || owner is null || !ReferenceEquals(child.Owner, owner) || !owner.ChildAgents.Contains(child))
            {
                return;
            }
            string? run = child.Reference.RunId;
            string? revision = child.Reference.SourceRevision;
            await OpenCommand.ExecuteAsync(null);
            if (ReferenceEquals(owner, _Owner) && owner.ChildAgents.Contains(child)
                && run == child.Reference.RunId && revision == child.Reference.SourceRevision)
            {
                child.OpenCommand.Execute(null);
            }
        });
    }

    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand<ChildAgentViewModel> OpenChildCommand { get; }
    private SessionPanelViewModel? _Owner => _session ?? _serverSession?.Pane;
    public ObservableCollection<ChildAgentViewModel> Children => _Owner?.ChildAgents ?? _emptyChildren;
    public bool HasChildren => Children.Count > 0;
    public string ChildSummary
    {
        get
        {
            int running = Children.Count(child => child.Reference.State == "running");
            int unknown = Children.Count(child => child.Reference.State == "unknown");
            return $"{running} running · {Children.Count - running - unknown} other · {unknown} unknown";
        }
    }
    public string Machine { get; }
    public bool IsDelegatedTask => _task is not null;
    // shortcut: remote handles have no activity timestamp, use observed status changes until their snapshots expose one.
    public DateTimeOffset ActivityAt => _task?.ActivityAt ?? _session?.RestoreOffer?.State?.RecordedAt
        ?? _session?.LastActivityUtc ?? _observedActivityAt;
    private DateTimeOffset _observedActivityAt = DateTimeOffset.UtcNow;
    private SessionStatus _observedStatus;
    public string Title => _task?.Title ?? _session?.Title ?? _serverSession?.Title ?? string.Empty;
    public string? Profile => _task?.Profile ?? _session?.ActiveProfileLabel ?? _serverSession?.Profile;
    public string Statusline => _task is not null ? "Delegated task · no pane" : _session?.Statusline ?? _serverSession?.Statusline ?? string.Empty;
    public string Project => (_session?.ProjectId ?? _serverSession?.Handle.ProjectId) is { } id
        ? (_serverGroup is null
            ? _cockpit.Projects.Projects.FirstOrDefault(project => project.Id == id)?.Name
            : _serverGroup.Server?.Projects.Current.Settings.Projects.FirstOrDefault(project => project.Id == id)?.Name) ?? "Project unavailable"
        : "No project";
    public bool TerminalOnly => _session is TtyViewModel || _serverSession?.Handle.IsTerminal == true;
    public SessionStatus Status => _task is not null ? _task.Status switch
    {
        Cockpit.Core.Delegation.DelegatedTaskStatus.Running => SessionStatus.Busy,
        Cockpit.Core.Delegation.DelegatedTaskStatus.Queued => SessionStatus.WorkingBackground,
        Cockpit.Core.Delegation.DelegatedTaskStatus.Completed => SessionStatus.Done,
        Cockpit.Core.Delegation.DelegatedTaskStatus.Failed => SessionStatus.Failed,
        _ => SessionStatus.Idle,
    } : _session?.SessionStatus ?? _serverSession?.Status ?? SessionStatus.Idle;
    public bool NeedsAttention => _session?.RequestsAttention
        ?? (_serverSession?.NeedsAttention == true || Status is SessionStatus.NeedsAttention or SessionStatus.Failed);
    // A sleeping or unreachable swarm member keeps its cards, greyed, with its "Last seen" label (AC-1932).
    public string LastSeenLabel => _serverSession?.LastSeenLabel ?? string.Empty;
    public bool IsAsleep => LastSeenLabel.Length > 0;
    public bool ShowsAttention => NeedsAttention && !IsAsleep;
    public bool IsWaitingForYou => Status == SessionStatus.NeedsAttention && (_serverSession is null || _serverSession.LastSeenLabel.Length == 0);
    public bool IsSelected => _session?.IsSelected == true || _serverSession?.Pane?.IsSelected == true;
    public string StatusBrushKey => _task?.StatusBrushKey ?? _session?.SessionStatusBrushKey ?? _serverSession?.StatusBrushKey ?? "CockpitTextFaintBrush";
    public string StatusLabel => _task?.StatusText ?? _session?.SessionStatusLabel ?? _serverSession?.StatusLabel ?? string.Empty;

    internal void Refresh()
    {
        if (_observedStatus != Status)
        {
            _observedStatus = Status;
            _observedActivityAt = DateTimeOffset.UtcNow;
        }
        if (!ReferenceEquals(_childOwner, _Owner))
        {
            Dispose();
            _childOwner = _Owner;
            if (_childOwner is not null)
            {
                _childOwner.PropertyChanged += _OnChildrenChanged;
            }
        }
        OnPropertyChanged(string.Empty);
    }

    private void _OnChildrenChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SessionPanelViewModel.HasChildAgents) or null or "")
        {
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(ChildSummary));
        }
    }

    public void Dispose()
    {
        if (_childOwner is not null)
        {
            _childOwner.PropertyChanged -= _OnChildrenChanged;
            _childOwner = null;
        }
    }
}
