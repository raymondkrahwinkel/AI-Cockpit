using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Assistant threads: a thread is open or closed. A closed one leaves the rail and keeps every row, in All and in the
// log; new activity for its project opens it again. General never closes, and neither does a thread with a question
// or an approval waiting in it — closing it would hide the very thing the operator still has to answer.
public partial class SessionViewModel
{
    private readonly HashSet<string> _closedThreads = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ClosedThreads => _closedThreads;

    // Raised when the operator or the assistant opened or closed a thread, for whoever keeps the set across a restart.
    public event Action? ClosedThreadsChanged;

    // The set as it was saved, before any row is drawn: no event, since nothing changed that needs saving.
    public void LoadClosedThreads(IEnumerable<string> projectIds)
    {
        _closedThreads.Clear();
        _closedThreads.UnionWith(projectIds);
        ThreadsChanged?.Invoke();
    }

    public bool IsThreadClosed(string projectId) => _closedThreads.Contains(projectId);

    // Why a thread cannot close right now, or null when it can.
    public string? WhyThreadCannotClose(string? projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return "General is where everything without a project lands; it does not close.";
        }

        var waiting = OpenQuestionCountIn(projectId);
        return waiting switch
        {
            0 => null,
            1 => "A question or an approval is still waiting in this thread; it closes once that is answered.",
            _ => $"{waiting} questions or approvals are still waiting in this thread; it closes once they are answered.",
        };
    }

    public AssistantThreadResult CloseThread(string projectId)
    {
        if (WhyThreadCannotClose(projectId) is { } refusal)
        {
            return new AssistantThreadResult(false, refusal);
        }

        if (!_closedThreads.Add(projectId))
        {
            return new AssistantThreadResult(true, "That thread was closed already.");
        }

        _RaiseClosedThreads();
        return new AssistantThreadResult(true, null);
    }

    public AssistantThreadResult ReopenThread(string projectId)
    {
        if (!_closedThreads.Remove(projectId))
        {
            return new AssistantThreadResult(true, "That thread was open already.");
        }

        _RaiseClosedThreads();
        return new AssistantThreadResult(true, null);
    }

    private void _RaiseClosedThreads()
    {
        ThreadsChanged?.Invoke();
        ClosedThreadsChanged?.Invoke();
    }

    // A prompt, a message from a session, a question or an approval for a closed project brings its thread back: the
    // operator closed it because it had gone quiet, and it has not any more. A restart repainting the log does not.
    private void _ReopenOnActivity(TranscriptEntryViewModel entry)
    {
        if (_closedThreads.Count == 0 || IsReplayingTranscript
            || !(entry.StartsTurn || entry.IsOpenQuestion))
        {
            return;
        }

        var reopened = false;
        foreach (var projectId in entry.ThreadProjectIds)
        {
            reopened |= _closedThreads.Remove(projectId);
        }

        if (reopened)
        {
            _RaiseClosedThreads();
        }
    }

    // What list_threads tells the assistant: every thread a project has here, open or closed, with what speaks for
    // closing it or against.
    public IReadOnlyList<AssistantThreadInfo> SummarizeThreads()
    {
        var projects = Transcript.SelectMany(row => row.ThreadProjectIds)
            .Concat(_closedThreads)
            .Concat(Threads?.LiveProjects() ?? [])
            .Distinct(StringComparer.Ordinal);

        return [.. projects
            .Select(projectId =>
            {
                var rows = Transcript.Where(row => row.IsInThread(projectId)).ToList();
                return new AssistantThreadInfo(
                    projectId,
                    ProjectName(projectId) ?? projectId,
                    _closedThreads.Contains(projectId),
                    rows.Count,
                    rows.Count == 0 ? null : rows.Max(row => row.Timestamp),
                    Threads?.SessionsOf(projectId) ?? [],
                    OpenQuestionCountIn(projectId));
            })
            .OrderBy(summary => summary.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // --- The bundled suggestion to close (one row, however many threads) -------------------------------------------

    // suggest_closing_threads: one light row asks the operator about all of them at once. A row still waiting takes
    // the new ones in rather than a second row appearing; a thread that cannot close, or still has a session running,
    // is left out and said back. The operator's click is the decision, so no consent card follows it.
    public AssistantThreadResult SuggestClosingThreads(IReadOnlyList<string> projectIds)
    {
        var (known, note) = _KnownProjects(projectIds);
        var left = new List<string>();
        var candidates = known.Where(projectId =>
        {
            var why = _closedThreads.Contains(projectId) ? "already closed"
                : WhyThreadCannotClose(projectId) is not null ? "something is still waiting there"
                : (Threads?.SessionsOf(projectId).Count ?? 0) > 0 ? "a session is still running"
                : null;
            if (why is not null)
            {
                left.Add($"{ProjectName(projectId) ?? projectId} ({why})");
            }

            return why is null;
        }).ToList();

        if (left.Count > 0)
        {
            note = (note is null ? string.Empty : note + " ") + $"Left out: {string.Join(", ", left)}.";
        }

        if (candidates.Count == 0)
        {
            return new AssistantThreadResult(false, note ?? "No thread to suggest closing.");
        }

        if (Transcript.LastOrDefault(row => row is { IsSuggestionRow: true, IsSuggestionOpen: true }) is { } open)
        {
            var merged = open.ThreadProjectIds.Union(candidates, StringComparer.Ordinal).ToList();
            open.ProjectSources = merged.ToDictionary(id => id, _ => ProjectSource.AssistantTag);
            open.ProjectIds = merged;
            return new AssistantThreadResult(true, (note is null ? string.Empty : note + " ") + "Added to the suggestion already waiting.");
        }

        Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Suggestion, "Close threads?")
        {
            ProjectIds = candidates,
            ProjectSources = candidates.ToDictionary(id => id, _ => ProjectSource.AssistantTag),
        });

        return new AssistantThreadResult(true, note);
    }

    // The suggestion row's own buttons: close every thread on it that still can, or keep them all open.
    internal void ResolveSuggestion(TranscriptEntryViewModel suggestion, bool close)
    {
        if (!suggestion.IsSuggestionOpen)
        {
            return;
        }

        var closed = close
            ? suggestion.ThreadProjectIds.Where(projectId => CloseThread(projectId).Ok).ToList()
            : [];
        suggestion.PermissionDecision = closed.Count > 0
            ? $"Closed {string.Join(", ", closed.Select(projectId => ProjectName(projectId) ?? projectId))}"
            : "Kept open";
    }
}
