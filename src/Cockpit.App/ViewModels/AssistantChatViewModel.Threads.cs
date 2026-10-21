using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Layout;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// Where the chat view stands: its own window, the Panels stand's dock rail, or the Simple stand's conversation column.
public enum AssistantChatHost
{
    Floating,
    Docked,
    SimpleColumn,
}

// Which rail the chat can afford at its own width (voorstel § 7.2), the same ladder in the window and the dock.
public enum AssistantRailStand
{
    Labels,
    Icons,
    Picker,
}

public enum AssistantThreadItemKind
{
    Questions,
    All,
    General,
    Project,

    // Every project on one page: its sessions, what waits and the latest word from it.
    Overview,
}

// One entry of the thread rail: Questions for you, All, General or a project.
public sealed partial class AssistantThreadItemViewModel(AssistantThreadItemKind kind, string name, string? projectId = null, string? logoPath = null) : ObservableObject
{
    // The project's own logo, drawn by ProjectLogoBadge like every other surface that shows a project; its initial without one.
    public string? LogoPath { get; } = logoPath;

    public AssistantThreadItemKind Kind { get; } = kind;

    public string Name { get; } = name;

    public string? ProjectId { get; } = projectId;

    // What `SessionViewModel.ShownThread` takes for this entry; Questions shows no transcript at all.
    public string? ThreadKey => Kind switch
    {
        AssistantThreadItemKind.General => SessionViewModel.GeneralThread,
        AssistantThreadItemKind.Project => ProjectId,
        _ => null,
    };

    public bool IsThread => Kind is AssistantThreadItemKind.General or AssistantThreadItemKind.Project;

    // Only a project's thread closes; General is where everything without one lands.
    public bool IsProject => Kind == AssistantThreadItemKind.Project;

    public bool HasIcon => !IsThread;

    public MaterialIconKind IconKind => Kind switch
    {
        AssistantThreadItemKind.Questions => MaterialIconKind.HelpCircleOutline,
        AssistantThreadItemKind.Overview => MaterialIconKind.ViewGridOutline,
        _ => MaterialIconKind.FormatListBulleted,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaiting))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(IsAttention))]
    private int _waitingCount;

    public bool HasWaiting => WaitingCount > 0;

    // Questions carries the waiting row tint while anything is open, the way a session that needs you does.
    public bool IsAttention => Kind == AssistantThreadItemKind.Questions && HasWaiting;

    public string Tooltip => HasWaiting ? $"{Name} · {WaitingCount} waiting for you" : Name;

    // While a row is dragged: a thread it can go to, and the one under the pointer (§ 9.6).
    [ObservableProperty]
    private bool _isDropCandidate;

    [ObservableProperty]
    private bool _isDropHover;
}

// Assistant threads: the rail beside the conversation (variant B), its width ladder and the operator's choice of it.
public sealed partial class AssistantChatViewModel
{
    // The chat's own width, so the mockup's dock widths (labels from 600, icons from 340) less the 40 px tab strip:
    // labels need the 184 px rail beside a readable conversation, and below 300 even the 56 px icon rail is too much.
    public const double LabelsMinWidth = 540;
    public const double RailMinWidth = 300;

    private readonly AssistantThreadItemViewModel _questionsItem = new(AssistantThreadItemKind.Questions, "Questions");
    private readonly AssistantThreadItemViewModel _allItem = new(AssistantThreadItemKind.All, "All");
    private readonly AssistantThreadItemViewModel _generalItem = new(AssistantThreadItemKind.General, "General");
    private readonly AssistantThreadItemViewModel _overviewItem = new(AssistantThreadItemKind.Overview, "Overview");

    // Every project a row of the conversation is in, gathered as rows arrive so an append costs nothing.
    private readonly HashSet<string> _projectsWithRows = new(StringComparer.Ordinal);

    private bool _threadsInitialised;

    // Questions, All, General and the projects, General first and the projects by name (open question 6).
    public ObservableCollection<AssistantThreadItemViewModel> ThreadItems { get; } = [];

    // The same without Questions, for the narrow stand's picker: Questions has its own button there.
    public ObservableCollection<AssistantThreadItemViewModel> ThreadChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuestionsPage))]
    [NotifyPropertyChangedFor(nameof(IsTranscriptPage))]
    [NotifyPropertyChangedFor(nameof(IsOverviewPage))]
    [NotifyPropertyChangedFor(nameof(ThreadTitle))]
    [NotifyPropertyChangedFor(nameof(ThreadSubtitle))]
    [NotifyPropertyChangedFor(nameof(SelectedThreadChoice))]
    private AssistantThreadItemViewModel? _selectedThreadItem;

    public bool IsQuestionsPage => SelectedThreadItem?.Kind == AssistantThreadItemKind.Questions;

    public bool IsOverviewPage => SelectedThreadItem?.Kind == AssistantThreadItemKind.Overview;

    public bool IsTranscriptPage => !IsQuestionsPage && !IsOverviewPage;

    // The picker's own selection, which never holds Questions.
    public AssistantThreadItemViewModel? SelectedThreadChoice
    {
        get => IsQuestionsPage ? null : SelectedThreadItem;
        set
        {
            if (value is not null)
            {
                SelectedThreadItem = value;
            }
        }
    }

    public int OpenQuestionCount => Session?.OpenQuestionCount ?? 0;

    public bool HasOpenQuestions => OpenQuestionCount > 0;

    public string ThreadTitle => SelectedThreadItem?.Kind switch
    {
        AssistantThreadItemKind.Questions => "Questions for you",
        AssistantThreadItemKind.Overview => "Overview",
        AssistantThreadItemKind.All or null => "All threads",
        _ => SelectedThreadItem.Name,
    };

    public string ThreadSubtitle => DropHint ?? (IsDraggingRow ? "Drop on a thread to move it there · hold Ctrl to add" : _ThreadSubtitle);

    private string _ThreadSubtitle => SelectedThreadItem?.Kind switch
    {
        AssistantThreadItemKind.Questions => "A question stays here until you answer it.",
        AssistantThreadItemKind.Overview => "Every project: its sessions, what waits and the latest word.",
        AssistantThreadItemKind.All or null => "Every project in one stream. The tag shows where a row belongs.",
        AssistantThreadItemKind.General => "What is not about one project.",
        _ => SelectedThreadItem.WaitingCount switch
        {
            0 => "What you type here goes to this project.",
            var waiting => $"{waiting} waiting for you",
        },
    };

    partial void OnSelectedThreadItemChanged(AssistantThreadItemViewModel? value)
    {
        IsRailOverlayOpen = false;

        // Questions shows All behind it, so its cards carry their project tags like All's rows do.
        if (value is not null && Session is { } session)
        {
            session.ShownThread = value.ThreadKey;
        }

        if (IsOverviewPage)
        {
            _RebuildOverview();
        }
    }

    // --- Width ladder and the operator's choice (§ 7.2, § 8) ---------------------------------------------------------

    // The chat's own width, not the window's: the same view sits in the floating window and the dock.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RailStand))]
    [NotifyPropertyChangedFor(nameof(LabelsFit))]
    private double _viewWidth;

    public bool LabelsFit => ViewWidth <= 0 || ViewWidth >= LabelsMinWidth;

    private AssistantThreadRail _RailPreference =>
        (IsDocked ? _cockpit?.AssistantThreadRailDock : _cockpit?.AssistantThreadRailWindow) ?? AssistantThreadRail.Labels;

    // The choice wins where labels fit; the threshold only says whether they can.
    public AssistantRailStand RailStand => ViewWidth > 0 && ViewWidth < RailMinWidth
        ? AssistantRailStand.Picker
        : LabelsFit && _RailPreference == AssistantThreadRail.Labels ? AssistantRailStand.Labels : AssistantRailStand.Icons;

    // Never saved: the overlay is there only while it is open.
    [ObservableProperty]
    private bool _isRailOverlayOpen;

    partial void OnViewWidthChanged(double value) => _RaiseRailStand();

    partial void OnIsDockedChanged(bool value) => _RaiseRailStand();

    private void _RaiseRailStand()
    {
        if (RailStand != AssistantRailStand.Icons)
        {
            IsRailOverlayOpen = false;
        }

        OnPropertyChanged(nameof(RailStand));
        OnPropertyChanged(nameof(ShowsLabelRail));
        OnPropertyChanged(nameof(ShowsIconRail));
        OnPropertyChanged(nameof(ShowsPicker));
        OnPropertyChanged(nameof(RailToggleText));
        OnPropertyChanged(nameof(RailToggleTip));
        OnPropertyChanged(nameof(RailToggleCollapses));
    }

    partial void OnIsRailOverlayOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(RailToggleTip));
        OnPropertyChanged(nameof(RailToggleCollapses));
    }

    public bool ShowsLabelRail => RailStand == AssistantRailStand.Labels;

    public bool ShowsIconRail => RailStand == AssistantRailStand.Icons;

    public bool ShowsPicker => RailStand == AssistantRailStand.Picker;

    // « while labels show (in the rail or the overlay), » while only icons do.
    public bool RailToggleCollapses => ShowsLabelRail || IsRailOverlayOpen;

    public string RailToggleText => ShowsLabelRail ? "Icons only" : string.Empty;

    public string RailToggleTip => ShowsLabelRail
        ? "Show threads as icons only"
        : IsRailOverlayOpen
            ? "Close the thread list"
            : LabelsFit ? "Show threads with labels" : "Show threads with labels over the chat";

    [RelayCommand]
    private async Task ToggleRailAsync()
    {
        if (ShowsLabelRail)
        {
            await _SetRailPreferenceAsync(AssistantThreadRail.IconsOnly);
        }
        else if (LabelsFit)
        {
            await _SetRailPreferenceAsync(AssistantThreadRail.Labels);
        }
        else
        {
            IsRailOverlayOpen = !IsRailOverlayOpen;
        }
    }

    private async Task _SetRailPreferenceAsync(AssistantThreadRail rail)
    {
        if (_cockpit is not null)
        {
            await _cockpit.SetAssistantThreadRailAsync(IsDocked, rail);
        }

        _RaiseRailStand();
    }

    [RelayCommand]
    private void OpenQuestions() => SelectedThreadItem = _questionsItem;

    // "Also in WayMark" under a row: that project's thread, where the same row stands too.
    [RelayCommand]
    private void OpenThread(string? projectId)
    {
        if (ThreadItems.FirstOrDefault(item => item.Kind == AssistantThreadItemKind.Project && item.ProjectId == projectId) is { } item)
        {
            SelectedThreadItem = item;
        }
    }

    // Raised for the view, which owns the scroll: a row picked from Questions is shown where it lives.
    public event Action<TranscriptEntryViewModel>? RowRevealRequested;

    [RelayCommand]
    private void ShowInThread(TranscriptEntryViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedThreadItem = row.ThreadProjectIds.Count == 0
            ? _generalItem
            : ThreadItems.FirstOrDefault(item => item.ProjectId == row.ThreadProjectIds[0]) ?? _allItem;
        RowRevealRequested?.Invoke(row);
    }

    // --- Keeping the rail in step with the conversation --------------------------------------------------------------

    private void _InitThreads()
    {
        if (_threadsInitialised)
        {
            return;
        }

        _threadsInitialised = true;
        if (_cockpit is not null)
        {
            _cockpit.PropertyChanged += _OnCockpitRailPreferenceChanged;
        }

        _RebuildThreadItems();
        SelectedThreadItem = _allItem;
    }

    private void _OnCockpitRailPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CockpitViewModel.AssistantThreadRailDock) or nameof(CockpitViewModel.AssistantThreadRailWindow))
        {
            _RaiseRailStand();
        }
    }

    private void _WatchThreads(SessionViewModel? previous, SessionViewModel? next)
    {
        if (previous is not null)
        {
            previous.ThreadsChanged -= _OnThreadsChanged;
            previous.RowMoved -= _OnRowMoved;
        }

        if (next is not null)
        {
            next.ThreadsChanged += _OnThreadsChanged;
            next.RowMoved += _OnRowMoved;
            next.ShownThread = SelectedThreadItem?.ThreadKey;
        }

        _projectsWithRows.Clear();
        _RebuildThreadItems();
    }

    private void _OnThreadRowsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            var before = _projectsWithRows.Count;
            foreach (TranscriptEntryViewModel row in e.NewItems)
            {
                _projectsWithRows.UnionWith(row.ThreadProjectIds);
            }

            if (_projectsWithRows.Count != before)
            {
                _RebuildThreadItems();
            }

            return;
        }

        _projectsWithRows.Clear();
        _RebuildThreadItems();
    }

    // A move or an answer: the projects and every count are read again.
    private void _OnThreadsChanged()
    {
        _projectsWithRows.Clear();
        _RebuildThreadItems();
    }

    private void _RebuildThreadItems()
    {
        if (_projectsWithRows.Count == 0 && Session is { } session)
        {
            foreach (var row in session.Transcript)
            {
                _projectsWithRows.UnionWith(row.ThreadProjectIds);
            }
        }

        // A project with a live session gets its thread before anything is said in it, so it can be talked to.
        var projectIds = _projectsWithRows
            .Concat(LiveSessions.Select(pane => pane.ProjectId).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Where(id => Session?.IsThreadClosed(id) != true)
            .Select(id => (Id: id, Name: Session?.ProjectName(id) ?? _ProjectName(id) ?? id))
            .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _RebuildClosedThreadItems();

        var selectedKey = SelectedThreadItem is { } selected ? (selected.Kind, selected.ProjectId) : (AssistantThreadItemKind.All, null);
        var existing = ThreadItems.Where(item => item.Kind == AssistantThreadItemKind.Project).ToDictionary(item => item.ProjectId!, StringComparer.Ordinal);
        var items = new List<AssistantThreadItemViewModel> { _questionsItem, _allItem, _generalItem };
        items.AddRange(projectIds.Select(project => existing.TryGetValue(project.Id, out var kept) && kept.Name == project.Name
                && kept.LogoPath == _ProjectLogo(project.Id)
            ? kept
            : new AssistantThreadItemViewModel(AssistantThreadItemKind.Project, project.Name, project.Id, _ProjectLogo(project.Id))));
        items.Add(_overviewItem);

        if (!items.SequenceEqual(ThreadItems))
        {
            ThreadItems.Clear();
            ThreadChoices.Clear();
            foreach (var item in items)
            {
                ThreadItems.Add(item);
                if (item.Kind != AssistantThreadItemKind.Questions)
                {
                    ThreadChoices.Add(item);
                }
            }

            SelectedThreadItem = items.FirstOrDefault(item => (item.Kind, item.ProjectId) == selectedKey) ?? _allItem;
        }

        if (Session is { } targets)
        {
            targets.ThreadTargets = [.. projectIds.Select(project => (project.Id, project.Name))];
        }

        _RefreshThreadCounts();
    }

    private string? _ProjectLogo(string projectId) =>
        _cockpit?.Projects.Projects.FirstOrDefault(project => project.Id == projectId)?.LogoPath;

    private string? _ProjectName(string projectId) =>
        _cockpit?.Projects.Projects.FirstOrDefault(project => project.Id == projectId)?.Name;

    // Questions answered today, newest first, folded under the open ones (§ 2). By the day they were asked: the
    // transcript keeps no answer time.
    public ObservableCollection<TranscriptEntryViewModel> AnsweredToday { get; } = [];

    public bool HasAnsweredToday => AnsweredToday.Count > 0;

    public string AnsweredTodayLabel => $"Answered today ({AnsweredToday.Count})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnsweredTodayIconKind))]
    private bool _isAnsweredTodayExpanded;

    public MaterialIconKind AnsweredTodayIconKind => IsAnsweredTodayExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    [RelayCommand]
    private void ToggleAnsweredToday() => IsAnsweredTodayExpanded = !IsAnsweredTodayExpanded;

    private void _RefreshAnsweredToday()
    {
        var today = DateTimeOffset.Now.Date;
        var answered = (Session?.Transcript ?? [])
            .Where(row => (row.HasQuestionPrompts || row.RequiredApproval) && !row.IsAwaitingAnswer && row.Timestamp.Date == today)
            .Reverse()
            .ToList();
        if (answered.SequenceEqual(AnsweredToday))
        {
            return;
        }

        AnsweredToday.Clear();
        foreach (var row in answered)
        {
            AnsweredToday.Add(row);
        }

        OnPropertyChanged(nameof(HasAnsweredToday));
        OnPropertyChanged(nameof(AnsweredTodayLabel));
    }

    private void _RefreshThreadCounts()
    {
        _RefreshAnsweredToday();
        if (IsOverviewPage)
        {
            _RebuildOverview();
        }

        _questionsItem.WaitingCount = OpenQuestionCount;
        foreach (var item in ThreadItems.Where(item => item.IsThread))
        {
            item.WaitingCount = Session?.OpenQuestionCountIn(item.ThreadKey) ?? 0;
        }

        OnPropertyChanged(nameof(OpenQuestionCount));
        OnPropertyChanged(nameof(HasOpenQuestions));
        OnPropertyChanged(nameof(ThreadSubtitle));
    }
}
