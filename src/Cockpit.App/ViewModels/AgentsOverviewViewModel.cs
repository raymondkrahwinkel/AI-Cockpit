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

public sealed class AgentsOverviewViewModel
{
    public const string DockPanelId = "agents";

    private readonly CockpitViewModel _cockpit;
    private readonly Dictionary<INotifyPropertyChanged, AgentOverviewCardViewModel> _cards = [];
    private readonly List<INotifyCollectionChanged> _collections = [];
    private readonly List<IServerProjects> _serverProjects = [];

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
    }

    private void _Reconcile()
    {
        var catalogs = _cockpit.ServerGroups.Select(group => group.Server.Projects).ToList();
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
            .Concat(_cockpit.ServerGroups.SelectMany(group => group.Sessions)).ToList();
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
    }

    private void _OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => _Reconcile();

    private void _OnProjectsChanged() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var card in _cards.Values)
        {
            card.Refresh();
        }
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
        foreach (var column in Columns)
        {
            if (column != target)
            {
                column.Cards.Remove(card);
            }
        }

        if (!target.Cards.Contains(card))
        {
            target.Cards.Add(card);
        }
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
    private readonly ObservableCollection<ChildAgentViewModel> _emptyChildren = [];
    private SessionPanelViewModel? _childOwner;

    [ObservableProperty]
    private bool _areChildrenExpanded;

    internal AgentOverviewCardViewModel(INotifyPropertyChanged source, CockpitViewModel cockpit)
    {
        _cockpit = cockpit;
        _session = source as SessionPanelViewModel;
        _serverSession = source as ServerSessionRowViewModel;
        _serverGroup = _serverSession is null ? null : cockpit.ServerGroups.First(group => group.Sessions.Contains(_serverSession));
        Machine = _serverGroup?.Name ?? "here";
        OpenCommand = new AsyncRelayCommand(async () =>
        {
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
            var owner = _Owner;
            if (child is null || owner is null || !ReferenceEquals(child.Owner, owner) || !owner.ChildAgents.Contains(child))
            {
                return;
            }
            var run = child.Reference.RunId;
            var revision = child.Reference.SourceRevision;
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
            var running = Children.Count(child => child.Reference.State == "running");
            var unknown = Children.Count(child => child.Reference.State == "unknown");
            return $"{running} running · {Children.Count - running - unknown} other · {unknown} unknown";
        }
    }
    public string Machine { get; }
    public string Title => _session?.Title ?? _serverSession!.Title;
    public string? Profile => _session is not null ? _session.ActiveProfileLabel : _serverSession!.Profile;
    public string Statusline => _session?.Statusline ?? _serverSession!.Statusline;
    public string Project => (_session?.ProjectId ?? _serverSession?.Handle.ProjectId) is { } id
        ? (_serverGroup is null
            ? _cockpit.Projects.Projects.FirstOrDefault(project => project.Id == id)?.Name
            : _serverGroup.Server.Projects.Current.Settings.Projects.FirstOrDefault(project => project.Id == id)?.Name) ?? "Project unavailable"
        : "No project";
    public bool TerminalOnly => _session is TtyViewModel || _serverSession?.Handle.IsTerminal == true;
    public SessionStatus Status => _session?.SessionStatus ?? _serverSession!.Status;
    public bool NeedsAttention => _session?.RequestsAttention ?? Status is SessionStatus.NeedsAttention or SessionStatus.Failed;
    public bool IsSelected => _session?.IsSelected == true || _serverSession?.Pane?.IsSelected == true;
    public string StatusBrushKey => _session?.SessionStatusBrushKey ?? _serverSession!.StatusBrushKey;
    public string StatusLabel => _session?.SessionStatusLabel ?? _serverSession!.StatusLabel;

    internal void Refresh()
    {
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
