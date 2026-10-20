using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// What the threads need to know about the rest of the cockpit: a project's name, and which project a pane is in.
// Handed in by whoever hosts the assistant; a pane without it files everything under General.
public sealed record ThreadLookup(
    Func<string, string?> ProjectName,
    Func<string, string?> ProjectOfPane,
    Func<string, string?> PaneTitle);

// Assistant threads (voorstel § 4, § 9): one conversation, shown per project. The session owns the rows, so it is the
// one place that can see a row's turn and say which threads it is in; the views only pick which thread they show.
public partial class SessionViewModel
{
    // The thread a row with no project at all is in. Distinct from null, which is All.
    public const string GeneralThread = "";

    // Kinds a sender gives a message that only reports a state. A fixed list (open question 4) rather than a flag on
    // notify, so no sender has to learn anything new; anything else is an inbox card.
    private static readonly HashSet<string> StatusKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "done", "merged", "closed", "idle", "gate",
    };

    public ThreadLookup? Threads
    {
        get => _threads;
        set
        {
            _threads = value;
            OnPropertyChanged(nameof(ShowsThreadTags));
        }
    }

    private ThreadLookup? _threads;

    public string? ProjectName(string projectId) => Threads?.ProjectName(projectId);

    // Which thread the transcript shows: null for All, `GeneralThread`, or a project id. What the operator types
    // while one is open goes into it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThreadTags))]
    private string? _shownThread;

    // All draws a project tag on rows; inside a thread the tag would only repeat where you are.
    public bool ShowsThreadTags => Threads is not null && ShownThread is null;

    partial void OnShownThreadChanged(string? value) => _RebuildVisibleTranscript();

    // Everything waiting on the operator, oldest first: the question cards and the Allow/Deny rows.
    public ObservableCollection<TranscriptEntryViewModel> OpenQuestions { get; } = [];

    public int OpenQuestionCount => OpenQuestions.Count;

    // A question in two projects counts once in the total and once in each of its threads (§ 9.5).
    public int OpenQuestionCountIn(string? thread) => OpenQuestions.Count(row => _InThread(row, thread));

    // Raised when a row's threads or the open questions change, for whatever counts per thread.
    public event Action? ThreadsChanged;

    private bool _InShownThread(TranscriptEntryViewModel entry) => _InThread(entry, ShownThread);

    private static bool _InThread(TranscriptEntryViewModel entry, string? thread) => thread switch
    {
        null => true,
        GeneralThread => entry.IsGeneral,
        _ => entry.IsInThread(thread),
    };

    private bool _IsShown(TranscriptEntryViewModel entry) => entry.IsRowVisible && _InShownThread(entry);

    // The projects a prompt typed or spoken now is put in: the open thread's, or none in All.
    // ponytail: the thread open when the turn starts, not when it was queued; carry it on QueuedPrompt if that bites.
    private (IReadOnlyList<string>? Ids, IReadOnlyDictionary<string, ProjectSource>? Sources) _ComposerProjects() => ShownThread switch
    {
        null => (null, null),
        GeneralThread => ([], null),
        var project => ([project], new Dictionary<string, ProjectSource> { [project] = ProjectSource.Explicit }),
    };

    // Files one row, from its own projects or its turn's. Appends land here one at a time, so the turn is read off
    // the row above rather than walked again.
    private void _StampThread(int index)
    {
        var row = Transcript[index];
        if (row.StartsTurn || row.ProjectIds is not null)
        {
            row.ThreadProjectIds = row.ProjectIds ?? [];
            row.ThreadProjectSource = row.ProjectIds is { Count: > 0 } ids ? row.SourceFor(ids[0]) : ProjectSource.None;
            return;
        }

        var turn = _TurnProjects(index);
        row.ThreadProjectIds = turn;
        row.ThreadProjectSource = turn.Count == 0 ? ProjectSource.None : ProjectSource.Inherited;
    }

    private void _StampThreadsFrom(int index)
    {
        for (var i = Math.Max(0, index); i < Transcript.Count; i++)
        {
            _StampThread(i);
        }
    }

    // What the rows of a turn follow: what the assistant named with set_thread, else the operator's prompts in the
    // run of rows that opened it, else the messages that did.
    private IReadOnlyList<string> _TurnProjects(int index)
    {
        if (index > 0 && Transcript[index - 1] is { StartsTurn: false, HasOwnProjects: false } above)
        {
            return above.ThreadProjectIds;
        }

        var end = index - 1;
        while (end >= 0 && !Transcript[end].StartsTurn)
        {
            end--;
        }

        if (end < 0)
        {
            return [];
        }

        var start = end;
        while (start > 0 && Transcript[start - 1].StartsTurn)
        {
            start--;
        }

        var opening = Enumerable.Range(start, end - start + 1).Select(i => Transcript[i]).ToList();
        if (opening.LastOrDefault(row => row.TurnProjectIds is not null) is { TurnProjectIds: { } named })
        {
            return named;
        }

        // A status line rarely is what the assistant answers, so it only counts when nothing else opened the turn.
        var leading = opening.Where(row => row.IsUserRow).ToList() is { Count: > 0 } prompts
            ? prompts
            : opening.Where(row => row.IsInboxRow).ToList() is { Count: > 0 } messages ? messages : opening;
        return [.. leading.SelectMany(row => row.ProjectIds ?? []).Distinct(StringComparer.Ordinal)];
    }

    private void _SyncOpenQuestion(TranscriptEntryViewModel entry)
    {
        var open = entry.IsOpenQuestion;
        if (open == OpenQuestions.Contains(entry))
        {
            return;
        }

        if (open)
        {
            OpenQuestions.Add(entry);
        }
        else
        {
            OpenQuestions.Remove(entry);
        }

        OnPropertyChanged(nameof(OpenQuestionCount));
        ThreadsChanged?.Invoke();
    }

    private void _RebuildOpenQuestions()
    {
        OpenQuestions.Clear();
        foreach (var entry in Transcript.Where(entry => entry.IsOpenQuestion))
        {
            OpenQuestions.Add(entry);
        }

        OnPropertyChanged(nameof(OpenQuestionCount));
        ThreadsChanged?.Invoke();
    }

    // A row's own projects moved (a drag, set_thread, a restore): it and the rest of its turn are filed again.
    private void _OnRowProjectsChanged(TranscriptEntryViewModel entry)
    {
        var index = _IndexOfLast(entry);
        if (index < 0)
        {
            return;
        }

        _StampThreadsFrom(index);
        if (ShownThread is not null)
        {
            _RebuildVisibleTranscript();
        }

        ThreadsChanged?.Invoke();
    }

    // AC-394 put a notice here because the text entering the context could not be seen. Now each message is a row of
    // its own, with its sender and its project, so the assistant no longer has to retell it (§ 4 point 3).
    internal void NoteDeliveredMail(AgentInboxTurnNotice notice)
    {
        foreach (var message in notice.Messages)
        {
            var projectId = message.SenderProjectId
                ?? (message.SubjectPaneId is { } subject ? Threads?.ProjectOfPane(subject) : null);
            var senderTitle = message.SubjectPaneId is { } watched
                ? Threads?.PaneTitle(watched) ?? watched
                : Threads?.PaneTitle(message.FromPaneId);

            Transcript.Add(new TranscriptEntryViewModel(
                StatusKinds.Contains(message.Kind.Trim()) ? TranscriptEntryKind.Status : TranscriptEntryKind.Inbox,
                message.Body)
            {
                SenderPaneId = message.FromPaneId,
                SenderTitle = senderTitle,
                MessageKind = message.Kind,
                SubjectPaneId = message.SubjectPaneId,
                ProjectIds = projectId is null ? null : [projectId],
                ProjectSources = projectId is null ? null : new Dictionary<string, ProjectSource> { [projectId] = ProjectSource.Session },
            });
        }
    }
}
