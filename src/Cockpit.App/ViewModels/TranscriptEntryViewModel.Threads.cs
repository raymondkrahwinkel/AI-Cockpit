using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Assistant threads: which project threads a row is shown in. The threads are views of one conversation, so a row in
// two projects is one row carrying two ids, never a copy (voorstel § 9.1).
public partial class TranscriptEntryViewModel
{
    // The projects this row was put in itself; null when it has none of its own. Recorded in the log.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnProjects))]
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

    partial void OnThreadProjectIdsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(ThreadTags));

    partial void OnThreadProjectSourceChanged(ProjectSource value) => OnPropertyChanged(nameof(ThreadTags));

    // The tags "All" draws on the row: one per project, or General. Names come from the session's project lookup.
    public IReadOnlyList<ThreadTagViewModel> ThreadTags => ThreadProjectIds.Count == 0
        ? [new ThreadTagViewModel(null, "General", ProjectSource.None)]
        : [.. ThreadProjectIds.Select(id => new ThreadTagViewModel(id, Session?.ProjectName(id) ?? id, SourceFor(id)))];
}

// One project tag on a row, with the tooltip saying how the row got there.
public sealed record ThreadTagViewModel(string? ProjectId, string Name, ProjectSource Source)
{
    public string Tooltip => Source switch
    {
        ProjectSource.Explicit => $"{Name} · you put this here",
        ProjectSource.Session => $"{Name} · from a {Name} session",
        ProjectSource.TicketPrefix => $"{Name} · from its ticket prefix",
        ProjectSource.AssistantTag => $"{Name} · tagged by the assistant",
        ProjectSource.Inherited => $"{Name} · follows the prompt of its turn",
        _ => ProjectId is null ? "General · not about one project" : Name,
    };
}
