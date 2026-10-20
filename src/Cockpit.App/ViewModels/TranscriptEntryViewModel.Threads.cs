using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// Assistant threads: which project threads a row is shown in. The threads are views of one conversation, so a row in
// two projects is one row carrying two ids, never a copy (voorstel § 9.1).
public partial class TranscriptEntryViewModel
{
    // The projects this row was put in itself; null when it has none of its own. Recorded in the log.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnProjects))]
    [NotifyPropertyChangedFor(nameof(CarriesThreadTag))]
    [NotifyPropertyChangedFor(nameof(ShowsThreadTagsHere))]
    private IReadOnlyList<string>? _projectIds;

    [ObservableProperty]
    private IReadOnlyDictionary<string, ProjectSource>? _projectSources;

    // Which span of this prompt belongs to which project (tag_prompt, § 9.4).
    [ObservableProperty]
    private IReadOnlyList<TranscriptProjectPart>? _projectParts;

    // On a prompt: the projects the rest of its turn follows, when the assistant named them with set_thread.
    [ObservableProperty]
    private IReadOnlyList<string>? _turnProjectIds;

    public bool HasOwnProjects => ProjectIds is not null;

    // Dimmed in place while the operator drags it to another thread.
    [ObservableProperty]
    private bool _isBeingDragged;

    // What the threads actually file this row under: its own projects, or its turn's. Set by the owning session,
    // which is the one that can see the row's turn; never recorded, because it follows from what is.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral))]
    private IReadOnlyList<string> _threadProjectIds = [];

    [ObservableProperty]
    private ProjectSource _threadProjectSource;

    public bool IsGeneral => ThreadProjectIds.Count == 0;

    // Null asks about General.
    public bool IsInThread(string? projectId) => projectId is null
        ? ThreadProjectIds.Count == 0
        : ThreadProjectIds.Contains(projectId, StringComparer.Ordinal);

    public ProjectSource SourceFor(string projectId) =>
        ProjectSources?.GetValueOrDefault(projectId) is { } own and not ProjectSource.None ? own : ThreadProjectSource;

    // A row that opens a turn: what the operator said, and what other sessions sent. The rest of a turn follows it.
    public bool StartsTurn => Kind is TranscriptEntryKind.UserText or TranscriptEntryKind.Inbox or TranscriptEntryKind.Status;

    // Waiting on the operator: a question card (either route) or an Allow/Deny. What "Questions for you" lists and counts.
    public bool IsOpenQuestion => IsAwaitingAnswer;

    // --- Inbox and status rows ------------------------------------------------------------------------------------

    public bool IsInboxRow => Kind == TranscriptEntryKind.Inbox;

    public bool IsStatusRow => Kind == TranscriptEntryKind.Status;

    public object? InboxBranch => IsInboxRow ? this : null;

    public object? StatusBranch => IsStatusRow ? this : null;

    public string? SenderPaneId { get; init; }

    // The sender's name as it was when the message came in: the pane may be long closed by the time this is read back.
    public string? SenderTitle { get; init; }

    public string? MessageKind { get; init; }

    // For a watch report: the pane it is about, since the watcher is not a pane itself.
    public string? SubjectPaneId { get; init; }

    // A run of status lines folds under its first: that row carries the count, the rest stay out until it is opened.
    internal TranscriptEntryViewModel? StatusRunAnchor { get; set; }

    internal bool IsStatusFolded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusRun))]
    [NotifyPropertyChangedFor(nameof(StatusRunLabel))]
    private int _statusRunCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRunLabel))]
    [NotifyPropertyChangedFor(nameof(StatusRunIconKind))]
    private bool _isStatusRunExpanded;

    public bool HasStatusRun => StatusRunCount > 1;

    public string StatusRunLabel => IsStatusRunExpanded
        ? "Show fewer"
        : StatusRunCount == 2 ? "1 more update" : $"{StatusRunCount - 1} more updates";

    public MaterialIconKind StatusRunIconKind => IsStatusRunExpanded ? MaterialIconKind.ChevronUp : MaterialIconKind.ChevronDown;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleStatusRun() => Session?.ToggleStatusRun(this);

    // Only what is finished reads green; a closed session or an idle one asks nothing and stays faint.
    public bool IsDoneStatus => MessageKind?.Trim().ToLowerInvariant() is "done" or "merged";

    public MaterialIconKind StatusIconKind => IsDoneStatus ? MaterialIconKind.Check : MaterialIconKind.CircleSmall;

    // Where All draws the tag: what opens a turn, the first row of a reply or of a block in it, a question or an
    // approval, a row put somewhere of its own. Not every tool step, which would repeat its prompt's tag down the page.
    public bool CarriesThreadTag => StartsTurn || HasOwnProjects || HasQuestionPrompts || RequiredApproval
        || StartsProjectBlock || (IsAssistantMarkdown && !IsReplyContinuation);

    // All tags every such row; a thread only the rows that are in another thread as well.
    public bool ShowsThreadTagsHere => Session is { Threads: not null } session && CarriesThreadTag
        && (session.ShownThread is null || ThreadProjectIds.Count > 1);

    internal void RaiseThreadTags() => OnPropertyChanged(nameof(ShowsThreadTagsHere));

    // The turn's projects as the session last worked them out, so the next row of the turn need not walk back to it.
    internal IReadOnlyList<string>? TurnProjectsCache { get; set; }

    // The projects of the reply block this row is in, when the reply was written in blocks per project (§ 9.4).
    internal IReadOnlyList<string>? BlockProjects { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CarriesThreadTag))]
    [NotifyPropertyChangedFor(nameof(ShowsThreadTagsHere))]
    private bool _startsProjectBlock;

    public string ShowInLabel => ThreadProjectIds.Count == 0 ? "Show in General" : $"Show in {ThreadTags[0].Name}";

    public string SenderLabel => SenderTitle is { Length: > 0 } title ? title : SenderPaneId ?? string.Empty;

    public string InboxTitle => MessageKind is { Length: > 0 } kind
        ? char.ToUpperInvariant(kind[0]) + kind[1..]
        : "Message";

    // A status row reads as one line: its first, with the sender in front.
    public string StatusLine
    {
        get
        {
            var first = Text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
            return SenderLabel.Length == 0 ? first : $"{SenderLabel} · {first}";
        }
    }

    partial void OnThreadProjectIdsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(ThreadTags));
        OnPropertyChanged(nameof(ShowInLabel));
        OnPropertyChanged(nameof(ShowsThreadTagsHere));
    }

    partial void OnThreadProjectSourceChanged(ProjectSource value) => OnPropertyChanged(nameof(ThreadTags));

    // The tags "All" draws on the row: one per project, or General. Names come from the session's project lookup.
    // An array, not a collection expression: a one-element expression is a list whose IList.Contains throws on an
    // item of another type, and an ItemsControl asks exactly that.
    public IReadOnlyList<ThreadTagViewModel> ThreadTags => ThreadProjectIds.Count == 0
        ? new[] { new ThreadTagViewModel(null, "General", ProjectSource.None) }
        : ThreadProjectIds.Select(id => new ThreadTagViewModel(id, Session?.ProjectName(id) ?? id, SourceFor(id))).ToArray();
}

// One project tag on a row, with the tooltip saying how the row got there.
public sealed record ThreadTagViewModel(string? ProjectId, string Name, ProjectSource Source)
{
    public string Tooltip => Source switch
    {
        ProjectSource.Explicit => $"{Name} · you put this here",
        ProjectSource.Session => $"{Name} · from a {Name} session it names or that sent it",
        ProjectSource.TicketPrefix => $"{Name} · from a ticket key",
        ProjectSource.AssistantTag => $"{Name} · tagged by the assistant",
        ProjectSource.Inherited => $"{Name} · follows the prompt of its turn",
        ProjectSource.Mentioned => $"{Name} · named in the prompt",
        _ => ProjectId is null ? "General · not about one project" : Name,
    };
}
