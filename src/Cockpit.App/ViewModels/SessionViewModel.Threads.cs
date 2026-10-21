using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// What the threads need to know about the rest of the cockpit: a project's name, and which project a pane is in.
// Handed in by whoever hosts the assistant; a pane without it files everything under General.
public sealed record ThreadLookup(
    Func<string, string?> ProjectName,
    Func<string, string?> ProjectOfPane,
    Func<string, string?> PaneTitle)
{
    // The projects a prompt names, read before its turn; none when the host knows no projects.
    public Func<string, IReadOnlyList<InferredProject>> Infer { get; init; } = _ => [];
}

// One move of a row between threads, with everything needed to take it back. `Target` null is General.
public sealed record ThreadMove(
    TranscriptEntryViewModel Row,
    IReadOnlyList<RowProjects> Previous,
    string? Target,
    bool Added,
    IReadOnlyList<string> PreviousThreads);

// What one row said about its projects before a move.
public sealed record RowProjects(
    TranscriptEntryViewModel Row,
    IReadOnlyList<string>? Ids,
    IReadOnlyDictionary<string, ProjectSource>? Sources,
    IReadOnlyList<TranscriptProjectPart>? Parts,
    IReadOnlyList<string>? TurnProjectIds)
{
    public static RowProjects Of(TranscriptEntryViewModel row) =>
        new(row, row.ProjectIds, row.ProjectSources, row.ProjectParts, row.TurnProjectIds);

    // Ids last: that is the change the session refiles the row on.
    public void Restore()
    {
        Row.ProjectSources = Sources;
        Row.ProjectParts = Parts;
        Row.TurnProjectIds = TurnProjectIds;
        Row.ProjectIds = Ids;
    }
}

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
            OnPropertyChanged(nameof(ShowsThreadTools));
            _RaiseThreadTags();
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

    partial void OnShownThreadChanged(string? value)
    {
        _RebuildVisibleTranscript();
        _RaiseThreadTags();
    }

    private void _RaiseThreadTags()
    {
        foreach (var row in Transcript)
        {
            row.RaiseThreadTags();
        }
    }

    // Everything waiting on the operator, oldest first: the question cards and the Allow/Deny rows.
    public ObservableCollection<TranscriptEntryViewModel> OpenQuestions { get; } = [];

    public int OpenQuestionCount => OpenQuestions.Count;

    // A question in two projects counts once in the total and once in each of its threads (§ 9.5).
    public int OpenQuestionCountIn(string? thread) => OpenQuestions.Count(row => _InThread(row, thread));

    // A restart repaints the earlier conversation row by row; nothing in it is news to announce.
    public bool IsReplayingTranscript => _replayingRecordedTranscript;

    // Raised when a row's threads or the open questions change, for whatever counts per thread.
    public event Action? ThreadsChanged;

    private bool _InShownThread(TranscriptEntryViewModel entry) => _InThread(entry, ShownThread);

    private static bool _InThread(TranscriptEntryViewModel entry, string? thread) => thread switch
    {
        null => true,
        GeneralThread => entry.IsGeneral,
        _ => entry.IsInThread(thread),
    };

    private bool _IsShown(TranscriptEntryViewModel entry) =>
        entry.IsRowVisible && _InShownThread(entry) && !entry.IsStatusFolded;

    // Internal so the rule a prompt is filed by can be asserted without a running host.
    internal (IReadOnlyList<string>? Ids, IReadOnlyDictionary<string, ProjectSource>? Sources) ProjectsForPrompt(string text) =>
        _ComposerProjects(text);

    // A prompt put nowhere (All, or spoken) is filed by what it names: the session or pane, a ticket key, a project's
    // name. Both projects when it names two. What the operator chose and what the assistant tags later win over it.
    private (IReadOnlyList<string>? Ids, IReadOnlyDictionary<string, ProjectSource>? Sources) _InferredProjects(string text)
    {
        _inferredParts = null;
        if (Threads?.Infer(text) is not { Count: > 0 } inferred)
        {
            return (null, null);
        }

        _inferredParts = ProjectInference.Parts(text, inferred);
        return ([.. inferred.Select(hit => hit.ProjectId)], inferred.ToDictionary(hit => hit.ProjectId, hit => hit.Source));
    }

    // The parts of the prompt _InferredProjects last read, for the row it is about to make; null when there are none.
    private IReadOnlyList<TranscriptProjectPart>? _inferredParts;

    private IReadOnlyList<TranscriptProjectPart>? _TakeInferredParts()
    {
        var parts = _inferredParts;
        _inferredParts = null;
        return parts is { Count: > 0 } ? parts : null;
    }

    // --- Runs of status lines (§ 2: one foldable line) -------------------------------------------------------------
    // A run is consecutive status rows as the open thread shows them, so it is worked out per thread: in All the
    // statuses of two projects can be one run, while in each thread they are not.

    private void _FoldStatus(TranscriptEntryViewModel entry, TranscriptEntryViewModel? previousCandidate)
    {
        if (!entry.IsStatusRow || previousCandidate is not { IsStatusRow: true })
        {
            entry.StatusRunCount = 1;
            return;
        }

        var anchor = previousCandidate.StatusRunAnchor ?? previousCandidate;
        anchor.StatusRunCount++;
        entry.StatusRunAnchor = anchor;
        entry.IsStatusFolded = !anchor.IsStatusRunExpanded;
    }

    private void _FoldAppendedStatus(TranscriptEntryViewModel entry)
    {
        entry.StatusRunAnchor = null;
        entry.IsStatusFolded = false;
        if (!entry.IsRowVisible || !_InShownThread(entry))
        {
            return;
        }

        TranscriptEntryViewModel? previous = null;
        for (var index = _IndexOfLast(entry) - 1; index >= 0; index--)
        {
            if (Transcript[index] is var above && above.IsRowVisible && _InShownThread(above))
            {
                previous = above;
                break;
            }
        }

        _FoldStatus(entry, previous);
    }

    // --- The assistant's own filing (§ 9.4) --------------------------------------------------------------------------

    // ask_structured_question with projectIds: the card waits in each of their threads, or follows its turn without.
    public TranscriptEntryViewModel AddQuestion(string question, string inputJson, IReadOnlyList<string> projectIds)
    {
        var (known, _) = _KnownProjects(projectIds);
        var row = new TranscriptEntryViewModel(TranscriptEntryKind.Question, question)
        {
            InputJson = inputJson,
            QuestionPrompts = AskUserQuestionViewModel.Parse(inputJson),
            IsPendingBrokerAnswer = true,
            ProjectIds = known.Count == 0 ? null : known,
            ProjectSources = known.Count == 0 ? null : known.ToDictionary(id => id, _ => ProjectSource.AssistantTag),
        };

        Transcript.Add(row);
        return row;
    }

    // set_thread: the rest of the turn follows these projects, the prompt itself stays where the operator put it.
    public AssistantThreadResult SetTurnThreads(IReadOnlyList<string> projectIds)
    {
        if (Transcript.LastOrDefault(row => row.StartsTurn) is not { } opening)
        {
            return new AssistantThreadResult(false, "There is no prompt or message yet that this turn could belong to.");
        }

        var (known, note) = _KnownProjects(projectIds);
        opening.TurnProjectIds = known;
        return new AssistantThreadResult(true, note);
    }

    // tag_prompt: each quote is looked up in the operator's last prompt; the prompt is then in every project named, and
    // knows which span is whose. A quote that is not in it is no reason to refuse: the prompt still gets that project.
    public AssistantThreadResult TagLastPrompt(IReadOnlyList<(string ProjectId, string Quote)> parts)
    {
        if (Transcript.LastOrDefault(row => row.IsUserRow) is not { } prompt)
        {
            return new AssistantThreadResult(false, "The operator has not said anything yet in this conversation.");
        }

        var (known, note) = _KnownProjects([.. parts.Select(part => part.ProjectId)]);
        var spans = new List<TranscriptProjectPart>();
        var missing = new List<string>();
        foreach (var (projectId, quote) in parts.Where(part => known.Contains(part.ProjectId)))
        {
            var start = string.IsNullOrWhiteSpace(quote) ? -1 : prompt.Text.IndexOf(quote.Trim(), StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                missing.Add(quote);
                continue;
            }

            spans.Add(new TranscriptProjectPart(projectId, start, quote.Trim().Length));
        }

        prompt.ProjectSources = known.ToDictionary(id => id, _ => ProjectSource.AssistantTag);
        prompt.ProjectParts = spans.Count > 0 ? spans : null;
        prompt.ProjectIds = known;

        if (missing.Count > 0)
        {
            note = (note is null ? string.Empty : note + " ")
                + $"Not found in the prompt, so the whole prompt carries those projects: {string.Join(", ", missing.Select(quote => $"\"{quote}\""))}.";
        }

        return new AssistantThreadResult(true, note);
    }

    // An id no project has would file a row under a thread nobody can open; it is dropped and said back instead.
    private (IReadOnlyList<string> Known, string? Note) _KnownProjects(IReadOnlyList<string> projectIds)
    {
        var ids = projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var unknown = ids.Where(id => Threads is not null && Threads.ProjectName(id) is null).ToList();
        IReadOnlyList<string> known = [.. ids.Except(unknown)];
        return (known, unknown.Count == 0
            ? null
            : $"No project has the id {string.Join(", ", unknown)}; take the ids from list_projects. That part went to General.");
    }

    // --- Moving a row to another thread (§ 9.6)---------------------------------------------------------------------

    // The assistant's thread rail is what turns these on; a pane without threads has nothing to move a row to.
    public bool ShowsThreadTools => Threads is not null;

    // The projects a row can be moved to, as the rail lists them; set by whoever draws the rail.
    public IReadOnlyList<(string Id, string Name)> ThreadTargets { get; set; } = [];

    // A move or an add the operator made, for the toast that offers to take it back.
    public event Action<ThreadMove>? RowMoved;

    // Moves the row into one project (null: General), or adds the project to the ones it is in. The operator's choice
    // wins over anything derived, so every project of the row becomes Explicit; a prompt's split into parts and the
    // assistant's set_thread for its turn give way to it as well. Rows that only followed it move along by themselves.
    public ThreadMove MoveToThread(TranscriptEntryViewModel row, string? projectId, bool add, bool announce = true)
    {
        var rows = _RowsMovedWith(row);
        var move = new ThreadMove(row, [.. rows.Select(RowProjects.Of)], projectId, add && projectId is not null, row.ThreadProjectIds);

        foreach (var moved in rows)
        {
            IReadOnlyList<string> ids = projectId is null
                ? []
                : move.Added ? [.. moved.ThreadProjectIds.Append(projectId).Distinct(StringComparer.Ordinal)] : [projectId];
            moved.ProjectSources = ids.ToDictionary(id => id, _ => ProjectSource.Explicit);
            moved.ProjectParts = ids.Count > 1 ? moved.ProjectParts : null;
            moved.TurnProjectIds = null;
            moved.ProjectIds = ids;
        }

        if (announce)
        {
            RowMoved?.Invoke(move);
        }

        return move;
    }

    // Puts back the projects and their sources exactly as they were, so a derived row is derived again.
    public void UndoMove(ThreadMove move)
    {
        foreach (var previous in move.Previous)
        {
            previous.Restore();
        }
    }

    // A reply streams in as several rows and carries its actions on the last, so the grip there moves the reply: the
    // whole of it, or in a reply in blocks only the block it is in.
    private static IReadOnlyList<TranscriptEntryViewModel> _RowsMovedWith(TranscriptEntryViewModel row) =>
        row is { IsAssistantMarkdown: true, ReplyRows: { } reply }
            ? [.. reply.Where(part => ReferenceEquals(part.BlockProjects, row.BlockProjects))]
            : [row];

    // How many rows of the moved prompt's turn went with it: the ones that only followed it.
    public int RowsThatFollowed(TranscriptEntryViewModel row)
    {
        var index = _IndexOfLast(row);
        if (index < 0 || !row.StartsTurn)
        {
            return 0;
        }

        var count = 0;
        for (var i = index + 1; i < Transcript.Count && !Transcript[i].StartsTurn; i++)
        {
            if (Transcript[i] is { HasOwnProjects: false, BlockProjects: null, IsRowVisible: true })
            {
                count++;
            }
        }

        return count;
    }

    // The fold line's own click: the members of the run come out or go back in under it.
    internal void ToggleStatusRun(TranscriptEntryViewModel anchor)
    {
        anchor.IsStatusRunExpanded = !anchor.IsStatusRunExpanded;
        foreach (var member in Transcript.Where(row => ReferenceEquals(row.StatusRunAnchor, anchor)))
        {
            member.IsStatusFolded = !anchor.IsStatusRunExpanded;
            _SyncVisibleRow(member);
        }
    }

    // The projects a prompt typed or spoken now is put in: the open thread's, or none in All.
    // ponytail: the thread open when the turn starts, not when it was queued; carry it on QueuedPrompt if that bites.
    private (IReadOnlyList<string>? Ids, IReadOnlyDictionary<string, ProjectSource>? Sources) _ComposerProjects(string text) => ShownThread switch
    {
        null => _InferredProjects(text),
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
            row.TurnProjectsCache = row.StartsTurn ? null : _TurnProjects(index);
            row.ThreadProjectIds = row.ProjectIds ?? [];
            row.ThreadProjectSource = row.ProjectIds is { Count: > 0 } ids ? row.SourceFor(ids[0]) : ProjectSource.None;
            return;
        }

        var turn = _TurnProjects(index);
        row.TurnProjectsCache = turn;
        row.StartsProjectBlock = false;
        row.BlockProjects = null;

        // A reply in blocks per project (§ 9.4): a marker opens a block, and the rows of the reply after it stay in
        // that block until the next. An id no project has is General; text before the first marker follows the turn.
        if (row.IsAssistantMarkdown)
        {
            var markers = ProjectMarkers.In(row.Text);
            if (markers.Count > 0)
            {
                row.StartsProjectBlock = true;
                row.BlockProjects = [.. markers.Where(id => Threads is null || Threads.ProjectName(id) is not null)];
            }
            else if (row.IsReplyContinuation && index > 0 && Transcript[index - 1] is { BlockProjects: { } block })
            {
                row.BlockProjects = block;
            }
        }

        if (row.BlockProjects is { } blockProjects)
        {
            row.ThreadProjectIds = blockProjects;
            row.ThreadProjectSource = ProjectSource.AssistantTag;
            return;
        }

        row.ThreadProjectIds = turn;
        row.ThreadProjectSource = turn.Count == 0 ? ProjectSource.None : ProjectSource.Inherited;
    }

    // A streamed reply row is filed again once its text holds a marker, which can arrive several deltas in.
    private void _OnRowTextChanged(TranscriptEntryViewModel entry)
    {
        if (entry.IsAssistantMarkdown && entry.ProjectIds is null
            && (entry.StartsProjectBlock || entry.Text.Contains("<!--", StringComparison.Ordinal)))
        {
            _OnRowProjectsChanged(entry);
        }
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
        if (index > 0 && Transcript[index - 1] is { StartsTurn: false, TurnProjectsCache: { } aboveTurn })
        {
            return aboveTurn;
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
            _SayWhereItArrived(entry, isQuestion: true);
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

    // Something for the operator landed in a thread they are not looking at: one short line says where (§ 4.10). Not
    // in All, where it is on screen anyway; not for a status line; not while a restart repaints the conversation.
    private void _SayWhereItArrived(TranscriptEntryViewModel row, bool isQuestion)
    {
        if (!ReadResponsesAloud || !IsTheVoiceAssistant || IsReplayingTranscript || ShownThread is null || _InThread(row, ShownThread))
        {
            return;
        }

        var from = row.IsGeneral ? "General" : row.ThreadTags[0].Name;
        var line = AssistantSpokenFillers.ArrivedElsewhere(ReadAloudLanguage, isQuestion, from);
        if (line.Length > 0)
        {
            _ = EnqueueReadAloudAsync(line);
        }
    }

    // AC-394 put a notice here because the text entering the context could not be seen. Now each message is a row of
    // its own, with its sender and its project, so the assistant no longer has to retell it (§ 4 point 3).
    internal void NoteDeliveredMail(AgentInboxTurnNotice notice)
    {
        foreach (var message in notice.Messages)
        {
            var projectId = message.SenderProjectId
                ?? (message.SubjectPaneId is { } subject ? Threads?.ProjectOfPane(subject) : null);

            // A sender with no project of its own (a plain pane, a watch report on a pane that is gone) still says
            // what it is about; the first project its message names files it.
            var named = projectId is null ? Threads?.Infer(message.Body).FirstOrDefault() : null;
            projectId ??= named?.ProjectId;
            var senderTitle = message.SubjectPaneId is { } watched
                ? Threads?.PaneTitle(watched) ?? watched
                : Threads?.PaneTitle(message.FromPaneId);

            var row = new TranscriptEntryViewModel(
                StatusKinds.Contains(message.Kind.Trim()) ? TranscriptEntryKind.Status : TranscriptEntryKind.Inbox,
                message.Body)
            {
                SenderPaneId = message.FromPaneId,
                SenderTitle = senderTitle,
                MessageKind = message.Kind,
                SubjectPaneId = message.SubjectPaneId,
                ProjectIds = projectId is null ? null : [projectId],
                ProjectSources = projectId is null ? null : new Dictionary<string, ProjectSource> { [projectId] = named?.Source ?? ProjectSource.Session },
            };
            Transcript.Add(row);
            if (row.IsInboxRow)
            {
                _SayWhereItArrived(row, isQuestion: false);
            }
        }
    }
}
