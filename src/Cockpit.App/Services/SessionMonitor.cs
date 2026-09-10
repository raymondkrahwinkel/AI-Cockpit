using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Assistant;
using Cockpit.Core.Delegation;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// One session as a monitor tick finds it, read off the collections the UI already holds. Deliberately without the
// transcript: a tick looks at every pane, and reading every TTY record every 30 seconds would cost megabytes of
// file I/O for the ticks that find nothing. The rows come from `SessionMonitor.Tail`, asked per reported pane.
public sealed record MonitoredSession(
    string PaneId,
    string Title,
    SessionStatus Status,
    int AbandonedProcessCount,
    // AC-1313: rows this session has written so far, or null for a pane whose rows cannot be counted without
    // reading a file (a TTY record). Silence is growth in this number and never a status, which is what makes it
    // keep working on a session whose status is wrong; a count that cannot be had means no silence signal at all.
    int? TranscriptRows = null,
    // Something of this session's own is still running — a backgrounded shell, a `start_run`. A session waiting on
    // its own test run is not hanging, and this is the half of "legitimately waiting" the host measures itself.
    bool HasOutstandingWork = false,
    // The operator parked this pane. Not a higher threshold: no signal at all, for as long as it is set.
    bool Muted = false,
    // The operator's own silence threshold for this one pane, winning over the global one.
    TimeSpan? SilenceAfter = null);

// AC-1312: the safety net under `watch_session` — every live session, every tick, nothing armed, for the four
// states the host already knows without a threshold or a filter. One tick sends one message however many sessions
// are in it: a message wakes the assistant (`InboxWakeScheduler`), and ten findings must not be ten turns.
public sealed class SessionMonitor(
    IAgentMessageInbox inbox,
    INotificationSettingsStore settingsStore,
    ILogger<SessionMonitor>? logger = null) : ISingletonService, IDisposable
{
    // Same rate and the same reason as `SessionWatcher`: a tick reads collections the UI already holds, so it is
    // cheap enough to afford, and a crashed turn is news while the operator is still at the desk.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Who the message is from. Not a pane and never will be — the cockpit itself noticed this, not a neighbour.
    private const string SenderPaneId = "cockpit-session-monitor";
    private const int MaxMessageLength = 2_000;
    private const string TruncationMarker = " … (more sessions omitted)";
    private const int MaxRowLength = 200;

    // The window the per-window ceiling counts over. Fixed rather than configurable: the number is the knob.
    private static readonly TimeSpan CapWindow = TimeSpan.FromHours(1);

    private const string Crashed = "crashed";
    private const string NeedsAttention = "needs-attention";
    private const string AbandonedProcesses = "abandoned-processes";
    private const string Gone = "gone";
    private const string Silent = "silent";
    private const string TaskFailed = "task-failed";
    private const string TaskSlow = "task-slow";

    // Which `watch_session` event says the same thing as each signal. The boundary against the manual watch runs
    // along the events and not along the sessions, so a pane armed for one of these still gets the others here.
    // AC-1311 put `Failed` inside `busy-to-idle`, which is why a crashed turn maps there and not to nothing.
    private static readonly IReadOnlyDictionary<string, string> WatchEquivalent = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Crashed] = SessionWatchEvents.BusyToIdle,
        [NeedsAttention] = SessionWatchEvents.NeedsAttention,
        [Gone] = SessionWatchEvents.Gone,
        // AC-1313: `stuck` is the same measurement with the watch's own threshold, so an armed pane hears it there.
        [Silent] = SessionWatchEvents.Stuck,
    };

    private readonly ILogger<SessionMonitor> _logger = logger ?? NullLogger<SessionMonitor>.Instance;

    // What has already been said about each pane, so a state that lasts stays quiet — `CiWatcher._reported`, one
    // layer along. An entry is dropped the moment the state behind it ends, which is what makes the next crash on
    // the same session news again. Host-side, so an assistant restarted on an empty conversation re-reports nothing.
    private readonly Dictionary<string, HashSet<string>> _reported = new(StringComparer.Ordinal);

    // The panes the last tick saw, so one that disappeared can be told from one that was never there — and, by the
    // status it held when it went, a pane that vanished mid-turn from one closed after it said its piece.
    private readonly Dictionary<string, MonitoredSession> _lastSeen = new(StringComparer.Ordinal);

    // When each message went out, for the per-window ceiling.
    private readonly Queue<DateTimeOffset> _sent = new();

    // AC-1313: the row count each pane last had and when it last changed. Silence is measured off this and never
    // off a status — `SessionWatcher`'s `stuck` one layer along, for the same reason it counts rows there.
    private readonly Dictionary<string, (int Rows, DateTimeOffset Since)> _quiet = new(StringComparer.Ordinal);

    private DispatcherTimer? _timer;
    private bool _looking;
    private bool _disposed;

    // Every live session, asked fresh every tick. Set by the cockpit, which owns the session list; nothing is
    // looked at until it is.
    public Func<IReadOnlyList<MonitoredSession>>? Watching { get; set; }

    // AC-1313: the delegated tasks this cockpit is running — background work with no pane, no statusline and no
    // row in any session list, which is why it needs its own read. Nothing is looked at until the cockpit sets it.
    public Func<IReadOnlyList<DelegatedTaskView>>? Tasks { get; set; }

    // The last few transcript rows of one pane, so the message can be acted on without a second read-back. Asked
    // only for a pane that is actually being reported, which is what keeps a quiet tick free.
    public Func<string, Task<IReadOnlyList<string>>> Tail { get; set; } = _ => Task.FromResult<IReadOnlyList<string>>([]);

    // Whether the assistant armed a `watch_session` on this pane for this event itself. One lookup into
    // `SessionWatcher`, not a second set of books.
    public Func<string, string, bool> Armed { get; set; } = (_, _) => false;

    // The clock the per-window ceiling is measured on, a seam for the same reason `SessionWatcher.Clock` is one.
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // Starts watching the clock. Idempotent, and built on the UI thread — that is where the session list is read and
    // where a DispatcherTimer has to be created to ever tick at all (AC-368).
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += _OnTick;
        _timer.Start();

        _ = RunOnceAsync();
    }

    // One look at every live session. Public because the tests drive it directly rather than waiting on the timer —
    // the same seam `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A look that outlasts the interval must not have a second one started on top of it: two ticks writing back
        // what has been reported is how a crash is announced twice, or not at all.
        if (_looking || Watching is null)
        {
            return;
        }

        _looking = true;
        try
        {
            // AC-1313: read before the findings rather than only when there are some — the silence threshold is
            // what decides whether a quiet pane is a finding at all, so it has to be known first.
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            var live = _Live();
            var subjects = _Subjects(live, Tasks?.Invoke() ?? [], settings.MonitorSilenceThreshold);
            var findings = _Findings(subjects);

            if (findings.Count > 0)
            {
                if (!_Allowed(settings))
                {
                    // Nothing is written back, so this is a delay and not a loss: the next tick under the ceiling
                    // finds the same states and says them then.
                    _logger.LogInformation("Holding {Count} session findings: the monitor is at its message ceiling.", findings.Count);
                    return;
                }

                await _ReportAsync(findings).ConfigureAwait(true);
            }

            _Commit(live, subjects, findings);
        }
        finally
        {
            _looking = false;
        }
    }

    private List<MonitoredSession> _Live()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. Watching!().Where(session => seen.Add(session.PaneId))];
    }

    // Everything each pane and each delegated task would say this tick, before what has already been said comes
    // off it. Pure on purpose: nothing here is written back, so a tick held at the ceiling can be repeated in full.
    private List<Subject> _Subjects(
        IReadOnlyList<MonitoredSession> live,
        IReadOnlyList<DelegatedTaskView> tasks,
        TimeSpan silenceAfter)
    {
        // AC-1313: a session waiting on background work it handed out is waiting and not hanging. Measured off the
        // task list rather than off anything the session has to remember to declare, so it holds when it forgets.
        var delegating = tasks
            .Where(task => task.Status is DelegatedTaskStatus.Queued or DelegatedTaskStatus.Running)
            .Select(task => task.OwnerPaneId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        // A muted pane is skipped whole: the operator parked it, which is not the same as giving it more room.
        var subjects = live.Where(session => !session.Muted)
            .Select(session => new Subject(session.PaneId, $"Session '{session.Title}'", session.PaneId,
                [.. _SignalsOf(session, silenceAfter, delegating)]))
            .ToList();

        var present = live.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        foreach (var (paneId, last) in _lastSeen.Where(entry => !present.Contains(entry.Key)))
        {
            // A pane that was quiet when it went was closed, not lost. Reporting those would make the monitor say
            // something every time the operator tidies up, which is the fastest way to have it ignored.
            if (last.Muted || last.Status is SessionStatus.Idle or SessionStatus.Done)
            {
                continue;
            }

            subjects.Add(new Subject(paneId, $"Session '{last.Title}'", paneId,
                [(Gone, $"the pane is no longer there and it never reported finishing — it was {last.Status} when it went.")]));
        }

        subjects.AddRange(tasks.Select(task => new Subject(
            task.TaskId,
            $"Delegated task '{task.Label ?? task.TaskType ?? task.ProfileLabel}'",
            // No pane: nothing to read a transcript back from, and no `watch_session` that could cover it either.
            PaneId: null,
            [.. _SignalsOfTask(task, silenceAfter)])));

        return subjects;
    }

    // What is left once the already-said and the armed-elsewhere come off, in one list so one message carries it.
    private List<Finding> _Findings(IReadOnlyList<Subject> subjects)
    {
        var findings = new List<Finding>();

        foreach (var subject in subjects)
        {
            var already = _reported.GetValueOrDefault(subject.Key);
            foreach (var (signal, what) in subject.Signals)
            {
                if (already?.Contains(signal) == true || _IsArmed(subject.PaneId, signal))
                {
                    continue;
                }

                findings.Add(new Finding(subject, signal, what));
            }
        }

        return findings;
    }

    private IEnumerable<(string Signal, string What)> _SignalsOf(
        MonitoredSession session,
        TimeSpan silenceAfter,
        IReadOnlySet<string> delegating)
    {
        if (session.Status is SessionStatus.Failed)
        {
            yield return (Crashed, "its turn ended in an error, so it is not finished and nothing is running on it.");
        }

        if (session.Status is SessionStatus.NeedsAttention)
        {
            yield return (NeedsAttention, "it is stopped on something nobody has answered. It cannot call any tool "
                + "while it waits, so it cannot tell you this itself.");
        }

        if (session.AbandonedProcessCount > 0)
        {
            yield return (AbandonedProcesses, $"{session.AbandonedProcessCount} of its processes are still running "
                + "with whatever started them gone (AC-1096), so they will not be cleaned up with it.");
        }

        if (_SilentFor(session, silenceAfter, delegating) is { } quiet)
        {
            // A finished session deliberately has no arm of its own: one sitting Done and quiet is exactly the case
            // the operator has to point out today, so it is worth one message — and the dedupe makes it exactly one.
            yield return (Silent, $"it has written nothing for {_Spell(quiet)} while its status says {session.Status}.");
        }
    }

    // AC-1313: how long this pane has written nothing, or null when it is not silent in a way worth reporting.
    private TimeSpan? _SilentFor(MonitoredSession session, TimeSpan silenceAfter, IReadOnlySet<string> delegating)
    {
        // Three ways of not being silent, cheapest first: rows that cannot be counted at all, work of its own still
        // running under it, and background work it handed out and is now waiting on.
        if (session.TranscriptRows is not { } rows
            || session.HasOutstandingWork
            || delegating.Contains(session.PaneId))
        {
            return null;
        }

        // A shrink is not a silence either (AC-294): a record that reads back short of what we saw has changed, and
        // a session that never started sits flat instead — that one is the report worth having.
        if (!_quiet.TryGetValue(session.PaneId, out var seen) || seen.Rows != rows)
        {
            return null;
        }

        var quiet = Clock() - seen.Since;
        return quiet >= (session.SilenceAfter ?? silenceAfter) ? quiet : null;
    }

    // A delegated task has no transcript and no process tree to read: `Failed` is a state the host already holds,
    // and `Running` past the threshold is the same silence one layer along. Both name the pane waiting on it.
    private IEnumerable<(string Signal, string What)> _SignalsOfTask(DelegatedTaskView task, TimeSpan silenceAfter)
    {
        var owner = task.OwnerPaneId is { } paneId ? $"pane {paneId} is waiting on it" : "the operator started it";

        if (task.Status is DelegatedTaskStatus.Failed)
        {
            var why = string.IsNullOrWhiteSpace(task.Error) ? "." : $": {task.Error}";
            yield return (TaskFailed, $"it failed on {task.ProfileLabel} and {owner}{why}");
        }

        if (task.Status is DelegatedTaskStatus.Running
            && Clock() - (task.StartedAt ?? task.CreatedAt) is { } running
            && running >= silenceAfter)
        {
            yield return (TaskSlow, $"it has been running on {task.ProfileLabel} for {_Spell(running)} and {owner}. "
                + "It has no pane, so nothing else is going to say this.");
        }
    }

    private bool _IsArmed(string? paneId, string signal) =>
        paneId is not null && WatchEquivalent.TryGetValue(signal, out var @event) && Armed(paneId, @event);

    private static string _Spell(TimeSpan quiet) =>
        quiet.TotalMinutes >= 1 ? $"{(int)quiet.TotalMinutes} minutes" : "less than a minute";

    // One message per tick however many sessions are in it. The tick is the bundling unit and there is no waiting
    // window: findings spread over three ticks are three messages, on purpose — a window that held them back would
    // be a second delay on top of the interval.
    private async Task _ReportAsync(IReadOnlyList<Finding> findings)
    {
        var blocks = new List<string>();
        foreach (var group in findings.GroupBy(finding => finding.Subject.Key, StringComparer.Ordinal))
        {
            var subject = group.First().Subject;
            var what = string.Join(" Also, ", group.Select(finding => finding.What));
            IReadOnlyList<string> rows = subject.PaneId is { } paneId ? await _TailOf(paneId).ConfigureAwait(true) : [];
            var lines = rows.Count == 0 ? string.Empty : "\n" + string.Join("\n", rows.Select(row => "> " + _Short(row)));

            blocks.Add($"{subject.Label} ({subject.Key}) — {what}{lines}");
            _logger.LogInformation("Monitored {Label} ({Key}): {Signals}.",
                subject.Label, subject.Key, string.Join(", ", group.Select(finding => finding.Signal)));
        }

        // One finding reads as one session and nothing else: a bundle header over a single session would make the
        // ordinary case look like an incident.
        var body = blocks.Count == 1
            ? $"{blocks[0]}\nNothing has been started about it."
            : $"{blocks.Count} sessions need looking at.\n\n{string.Join("\n\n", blocks)}\n\nNothing has been started about any of them.";

        _sent.Enqueue(Clock());
        inbox.Deliver(SenderPaneId, AssistantIdentity.PaneId, "session", _Bound(body));
    }

    private async Task<IReadOnlyList<string>> _TailOf(string paneId)
    {
        try
        {
            return await Tail(paneId).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // A pane that closed between the tick and the read, or a transcript that would not open. The report is
            // still worth sending without its rows.
            _logger.LogDebug(exception, "Reading the last rows of {PaneId} failed; reporting it without them.", paneId);
            return [];
        }
    }

    // Written back only once the message is out, so a tick held at the ceiling repeats rather than forgets. A signal
    // whose state has ended is dropped here, which is what makes the same state news again the next time it happens.
    private void _Commit(
        IReadOnlyList<MonitoredSession> live,
        IReadOnlyList<Subject> subjects,
        IReadOnlyList<Finding> findings)
    {
        var reportedNow = findings.ToLookup(finding => finding.Subject.Key, finding => finding.Signal, StringComparer.Ordinal);

        foreach (var subject in subjects)
        {
            var active = subject.Signals.Select(signal => signal.Signal).ToHashSet(StringComparer.Ordinal);
            var already = _reported.GetValueOrDefault(subject.Key) ?? new HashSet<string>(StringComparer.Ordinal);
            already.UnionWith(reportedNow[subject.Key]);
            already.IntersectWith(active);

            if (already.Count == 0)
            {
                _reported.Remove(subject.Key);
            }
            else
            {
                _reported[subject.Key] = already;
            }
        }

        // Forgotten outright: a pane that is gone cannot come back under the same id and a task that left the list
        // is over, and keeping either would have the monitor report it again on every tick for the rest of the run.
        var keep = live.Select(session => session.PaneId).Concat(subjects.Select(subject => subject.Key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in _reported.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            _reported.Remove(key);
        }

        var present = live.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        foreach (var paneId in _lastSeen.Keys.Where(paneId => !present.Contains(paneId)).ToList())
        {
            _lastSeen.Remove(paneId);
            _quiet.Remove(paneId);
        }

        var now = Clock();
        foreach (var session in live)
        {
            _lastSeen[session.PaneId] = session;

            // Stamped only when the count moves, so the moment kept here is when this pane last wrote something.
            if (session.TranscriptRows is { } rows
                && (!_quiet.TryGetValue(session.PaneId, out var seen) || seen.Rows != rows))
            {
                _quiet[session.PaneId] = (rows, now);
            }
        }
    }

    private bool _Allowed(NotificationSettings settings)
    {
        var now = Clock();
        while (_sent.Count > 0 && now - _sent.Peek() >= CapWindow)
        {
            _sent.Dequeue();
        }

        return _sent.Count < Math.Max(1, settings.MonitorMessagesPerHour);
    }

    private static string _Bound(string message) =>
        message.Length <= MaxMessageLength ? message : message[..(MaxMessageLength - TruncationMarker.Length)] + TruncationMarker;

    private static string _Short(string row)
    {
        var single = row.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxRowLength ? single : single[..MaxRowLength] + "…";
    }

    private async void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            await RunOnceAsync();
        }
        catch (Exception exception)
        {
            // A watcher must never be the reason the cockpit falls over, but a failure that stops the loop silently
            // is a safety net that reports nothing forever.
            _logger.LogError(exception, "A session monitor tick failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Watching = null;
        Tasks = null;
        _reported.Clear();
        _lastSeen.Clear();
        _quiet.Clear();

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }

    // One thing a tick has an opinion about: a live pane, a pane that has gone, or a delegated task.
    private sealed record Subject(string Key, string Label, string? PaneId, IReadOnlyList<(string Signal, string What)> Signals);

    private sealed record Finding(Subject Subject, string Signal, string What);
}
