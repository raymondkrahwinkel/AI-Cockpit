using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// One watched pane as a tick finds it. Null from `SessionWatcher.Probe` is the pane being gone, itself one of the
// events rather than an error. `NewRows` is the bounded set of rows added since the tick's row count — what
// `pattern` matches against; `LastRows` is the last few rows regardless, so every report carries content.

// AC-294: `HasTranscript` is whether this pane has a record that can be read back at all — a route, not content.
// A session that has one and has written nothing to it yet is exactly what `stuck` is for.
public sealed record WatchedPane(
    string Title,
    SessionStatus Status,
    bool NeedsAttention,
    bool HasTranscript,
    int TranscriptRows,
    IReadOnlyList<string> NewRows,
    IReadOnlyList<string> LastRows,
    // AC-1311: something of this session's own is still running even though Status does not hold on it — the
    // same fact `list_sessions` carries as `hasOutstandingWork`. Without it, "it stopped working" reads as
    // finished for a pane that is really just quiet while a backgrounded shell runs under it.
    bool HasOutstandingWork = false);

// The five things a watch can be armed for, spelled the way the assistant passes them.
public static class SessionWatchEvents
{
    public const string BusyToIdle = "busy-to-idle";

    public const string NeedsAttention = "needs-attention";

    public const string Gone = "gone";

    public const string Stuck = "stuck";

    public const string Pattern = "pattern";

    public static readonly IReadOnlyList<string> All = [BusyToIdle, NeedsAttention, Gone, Stuck, Pattern];
}

// AC-640: watches the panes the assistant armed it for and puts a message in its inbox when one finishes, gets
// stuck, stops producing output, or matches a pattern. Unlike `CiWatcher` (watches every checkout), nothing is
// watched until `watch_session` says so. No `IAttentionNotifier`: it is the assistant's own business, not a toast.
public sealed class SessionWatcher : ISessionWatcher, ISingletonService, IDisposable
{
    // Short enough that "it finished" is news while the operator is still asking, and cheap enough to afford at that
    // rate: a tick reads collections the caller already holds, and reads nothing at all when nothing is armed.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Who the message is from. Not a pane — the cockpit itself noticed this, not a neighbour.
    private const string SenderPaneId = "cockpit-session-watch";

    // How long without a new transcript row counts as stuck when the caller names no figure.
    private static readonly TimeSpan DefaultStuckAfter = TimeSpan.FromMinutes(15);

    // The most rows one tick will hand a pattern. A session that produced ten thousand rows since the last tick is
    // not a reason to allocate ten thousand strings, and the newest are the ones worth matching.
    private const int MaxNewRows = 200;

    private const int TailRows = 5;

    // ponytail: a burst of matching rows is reported up to this many times per tick, rather than filling the inbox
    // in one go. Raise it if a real pattern turns out to match faster than this and matters every time.
    private const int MaxMatchesPerTick = 5;

    private const int MaxRowLength = 200;

    private readonly IAgentMessageInbox _inbox;
    private readonly ILogger<SessionWatcher> _logger;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, Armed> _watches = new(StringComparer.Ordinal);

    private ITimer? _timer;
    private bool _looking;
    private bool _disposed;

    // One pane's state, as of the last tick that looked at it.
    private sealed class Armed
    {
        public required IReadOnlySet<string> Events { get; init; }

        public required TimeSpan StuckAfter { get; init; }

        public Regex? Pattern { get; init; }

        public string Title { get; set; } = string.Empty;

        public int Rows { get; set; }

        public DateTimeOffset LastGrowth { get; set; }

        public SessionStatus Status { get; set; }

        public bool NeedsAttention { get; set; }

        public bool ReportedStuck { get; set; }

        // Whether this pane has already been reported as finished or as waiting. What tells a pane that fell over
        // quietly from one that was closed after it said its piece — see the `gone` event.
        public bool Reported { get; set; }
    }

    public SessionWatcher(IAgentMessageInbox inbox, ILogger<SessionWatcher>? logger = null)
        : this(inbox, logger, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so `stuck` is provable without waiting fifteen real minutes for it.
    internal SessionWatcher(IAgentMessageInbox inbox, ILogger<SessionWatcher>? logger, TimeProvider time)
    {
        _inbox = inbox;
        _logger = logger ?? NullLogger<SessionWatcher>.Instance;
        _time = time;
    }

    // The live pane behind a pane id, at the row count the caller has already seen. Replaced by the tests, which
    // have no registry. Asynchronous because a TTY session's transcript is a file (AC-294) — see `ProbeOf`; an SDK
    // session's is in memory and its arm completes without ever yielding.
    public Func<string, int, Task<WatchedPane?>>? Probe { get; set; }

    // The clock the `stuck` threshold is measured on. A seam for the same reason `Probe` is one: a test that had to
    // wait fifteen real minutes to see the one event that does not read status would never be written.
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // Starts watching the clock. Idempotent.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _OnTick(), null, Interval, Interval);
    }

    // Arms a watch on one pane, replacing whatever was armed on it before. Refuses rather than throws: the caller is
    // a model whose next sentence is spoken to the operator.
    public async Task<AssistantWatchResult> WatchAsync(string paneId, IReadOnlyList<string>? events, int? afterMinutes, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(paneId))
        {
            return AssistantWatchResult.Refused("A watch needs a pane id; take one from list_sessions.");
        }

        var wanted = new HashSet<string>(events ?? [], StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return AssistantWatchResult.Refused($"Say what to watch for: {string.Join(", ", SessionWatchEvents.All)}.");
        }

        if (wanted.FirstOrDefault(name => !SessionWatchEvents.All.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            return AssistantWatchResult.Refused(
                $"'{unknown}' is not an event. The five are: {string.Join(", ", SessionWatchEvents.All)}.");
        }

        if (Probe is null)
        {
            return AssistantWatchResult.Refused("The session watcher is not running in this cockpit.");
        }

        if (await Probe(paneId, 0).ConfigureAwait(true) is not { } pane)
        {
            return AssistantWatchResult.Refused(
                $"There is no session on pane '{paneId}'. Take a pane id from list_sessions.");
        }

        // AC-294: not a question about the session kind any more — a TTY session's own CLI writes a transcript
        // the cockpit reads back (AC-609). Asked of the route and never of the content: a session that has a
        // record and has written nothing to it yet is exactly the one `stuck` is here for.
        var wantsTranscript = wanted.Contains(SessionWatchEvents.Stuck) || wanted.Contains(SessionWatchEvents.Pattern);
        if (wantsTranscript && !pane.HasTranscript)
        {
            return AssistantWatchResult.Refused(
                $"'{pane.Title}' keeps no transcript this cockpit can read — a plain terminal has no agent behind "
                + "it, and a provider may record nothing readable — so stuck and pattern cannot be watched on it. "
                + "busy-to-idle, needs-attention and gone still can.");
        }

        Regex? compiled = null;
        if (wanted.Contains(SessionWatchEvents.Pattern))
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return AssistantWatchResult.Refused("The pattern event needs a pattern to match against.");
            }

            try
            {
                // A caller-supplied expression run on a timer: the timeout is what stops one that backtracks from
                // taking a threadpool thread with it.
                compiled = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException exception)
            {
                return AssistantWatchResult.Refused($"That is not a regular expression: {exception.Message}");
            }
        }

        if (afterMinutes is <= 0)
        {
            return AssistantWatchResult.Refused("afterMinutes has to be a number of minutes above zero.");
        }

        _watches[paneId] = new Armed
        {
            Events = wanted,
            StuckAfter = afterMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : DefaultStuckAfter,
            Pattern = compiled,
            Title = pane.Title,
            Rows = pane.TranscriptRows,
            LastGrowth = Clock(),
            Status = pane.Status,
            NeedsAttention = pane.NeedsAttention,
        };

        return AssistantWatchResult.Watched(pane.Title);
    }

    // Disarms a pane. False when nothing was armed on it, which is worth saying rather than reporting a stop of
    // something that was never running.
    public bool Unwatch(string paneId) => _watches.Remove(paneId);

    // AC-1375/AC-1380: no hop to make any more — the gateway reached across to the UI thread this watcher used to
    // live on; now both sides are in Infrastructure, and `_watches` is this class's own state either way.
    Task<AssistantWatchResult> ISessionWatcher.WatchAsync(string paneId, IReadOnlyList<string>? events, int? afterMinutes, string? pattern) =>
        WatchAsync(paneId, events, afterMinutes, pattern);

    Task<bool> ISessionWatcher.UnwatchAsync(string paneId) => Task.FromResult(Unwatch(paneId));

    // One look at every armed pane. Public because the tests drive it directly rather than waiting on the timer —
    // the same seam `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync()
    {
        // A look that outlasts the interval must not have a second one started on top of it: two ticks comparing
        // against the same `watch.Rows` is how a stall is reported twice, or a growth spurt missed entirely. The
        // same guard, and for the same reason, as `CiWatcher.RunOnceAsync`'s.
        if (_looking || _watches.Count == 0 || Probe is null)
        {
            return;
        }

        _looking = true;
        try
        {
            foreach (var (paneId, watch) in _watches.ToList())
            {
                try
                {
                    await _LookAsync(paneId, watch).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Looking at watched pane {PaneId} failed; the next tick tries again.", paneId);
                }
            }
        }
        finally
        {
            _looking = false;
        }
    }

    private async Task _LookAsync(string paneId, Armed watch)
    {
        var pane = await Probe!(paneId, watch.Rows).ConfigureAwait(true);

        // Asked again after the probe, not before: a probe that reached into the cockpit may well be the thing that
        // called `unwatch_session`, and criterion 5 is that a disarmed pane produces no more reports from this tick.
        if (!_watches.ContainsKey(paneId))
        {
            return;
        }

        if (pane is null)
        {
            // A pane that is gone is unwatched whatever it was armed for — a watch on something that no longer
            // exists would otherwise sit there for the rest of the run.
            _watches.Remove(paneId);
            if (watch.Events.Contains(SessionWatchEvents.Gone) && !watch.Reported)
            {
                _Report(paneId, watch.Title, SessionWatchEvents.Gone,
                    "the pane is no longer there, and it never reported finishing or asking for anything. The watch "
                        + "has been dropped.",
                    []);
            }

            return;
        }

        watch.Title = pane.Title;

        if (watch.Events.Contains(SessionWatchEvents.BusyToIdle)
            && watch.Status is SessionStatus.Busy or SessionStatus.WorkingBackground
            && pane.Status is SessionStatus.Idle or SessionStatus.Done or SessionStatus.Failed)
        {
            watch.Reported = true;
            // AC-1311 criterion 2: a pane that stopped talking while something of its own is still running (a
            // backgrounded shell) is not "finished" — say so, or the report reads like SF-188 did.
            var outstanding = pane.HasOutstandingWork
                ? " Something of its own is still running under it — this is not finished."
                : string.Empty;
            _Report(paneId, pane.Title, SessionWatchEvents.BusyToIdle,
                $"it stopped working.{outstanding} The lines below say whether that is finished or a question "
                    + "waiting for an answer — read them before you report either.",
                pane.LastRows);
        }

        if (watch.Events.Contains(SessionWatchEvents.NeedsAttention) && pane.NeedsAttention && !watch.NeedsAttention)
        {
            watch.Reported = true;
            _Report(paneId, pane.Title, SessionWatchEvents.NeedsAttention,
                "it is stopped on something nobody has answered. It cannot call any tool while it waits, so it "
                    + "cannot tell you this itself.",
                pane.LastRows);
        }

        // Growth first, and off the row count alone: this is the one event that would still fire for a pane whose
        // status is stuck reporting Busy forever, which is exactly the failure it is here for.

        // AC-294: a shrink is not a silence. A TTY record rotated away or caught mid-write reads back short of
        // what we already saw, and calling that "written nothing" is a stall the watcher invented; a session that
        // never started sits flat instead, and that one is the report worth having.
        if (pane.TranscriptRows > watch.Rows)
        {
            watch.LastGrowth = Clock();
            watch.ReportedStuck = false;
        }
        else if (pane.TranscriptRows >= watch.Rows
            && watch.Events.Contains(SessionWatchEvents.Stuck)
            && !watch.ReportedStuck
            && Clock() - watch.LastGrowth >= watch.StuckAfter)
        {
            watch.ReportedStuck = true;
            _Report(paneId, pane.Title, SessionWatchEvents.Stuck,
                $"it has written nothing for {watch.StuckAfter.TotalMinutes:0} minutes. Counted in transcript rows, "
                    + "so this holds even if its status says otherwise.",
                pane.LastRows);
        }

        if (watch.Pattern is { } regex)
        {
            _Match(paneId, pane, regex);
        }

        // AC-294: a high-water mark, not the last reading. Writing a shrunken count back would make the next tick
        // see the lost record as flat and report the stall the arm above just declined to invent; a transcript
        // that really does start growing again passes the mark and carries on as before.
        watch.Rows = Math.Max(watch.Rows, pane.TranscriptRows);
        watch.Status = pane.Status;
        watch.NeedsAttention = pane.NeedsAttention;
    }

    // A fresh matching row is its own report every time: a second occurrence of a pattern is not the same fact as
    // the first, so there is nothing here to dedupe on the way the other four events dedupe on state.
    private void _Match(string paneId, WatchedPane pane, Regex regex)
    {
        var matches = 0;
        foreach (var row in pane.NewRows)
        {
            try
            {
                if (!regex.IsMatch(row))
                {
                    continue;
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                _logger.LogDebug(exception, "The pattern watched on {PaneId} timed out on a row; it was skipped.", paneId);
                continue;
            }

            _Report(paneId, pane.Title, SessionWatchEvents.Pattern, "a line matched what you asked to hear about.", [row]);
            if (++matches >= MaxMatchesPerTick)
            {
                return;
            }
        }
    }

    private void _Report(string paneId, string title, string @event, string what, IReadOnlyList<string> rows)
    {
        _logger.LogInformation("Watched session {Title} ({PaneId}): {Event}.", title, paneId, @event);

        var lines = rows.Count == 0
            ? string.Empty
            : "\n" + string.Join("\n", rows.Select(row => "> " + _Short(row)));

        _inbox.Deliver(
            SenderPaneId,
            AssistantIdentity.PaneId,
            "session",
            $"Watched session '{title}' ({paneId}) — {@event}: {what}{lines}\nNothing has been started about it.");
    }

    private static string _Short(string row)
    {
        var single = row.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxRowLength ? single : single[..MaxRowLength] + "…";
    }

    // `async void` deliberately, the shape an `ITimer` callback has to take: the catch below is inside it, so
    // nothing escapes to a threadpool thread with no one to catch it.
    private async void _OnTick()
    {
        try
        {
            await RunOnceAsync();
        }
        catch (Exception exception)
        {
            // A watcher must never be the reason the cockpit falls over, but a failure that stops the loop silently
            // is a watcher that reports nothing forever.
            _logger.LogError(exception, "A session watch tick failed; the next one will try again.");
        }
    }

    // The live pane behind a pane id, read off the session registry — safe from any thread, and the one route
    // that reads an SDK and a TTY session's transcript alike (AC-294, AC-1373): both are
    // `ISessionHandle.ReadTranscriptAsync`, with no need for this probe to tell the two kinds apart.
    public static Func<string, int, Task<WatchedPane?>> ProbeOf(ISessionRegistry registry) => async (paneId, since) =>
    {
        if (registry.Find(paneId) is not { } handle)
        {
            return null;
        }

        var needsAttention = handle.SessionStatus is SessionStatus.NeedsAttention || handle.HasPendingConsent;

        if (handle.IsTerminal)
        {
            return new WatchedPane(handle.Title, handle.SessionStatus, needsAttention, false, 0, [], [],
                await handle.HasOutstandingBackgroundShellsAsync().ConfigureAwait(false));
        }

        var slice = await handle.ReadTranscriptAsync(MaxNewRows).ConfigureAwait(false);
        var rows = slice.Entries.Select(entry => entry.Text).ToList();

        return new WatchedPane(
            handle.Title,
            handle.SessionStatus,
            needsAttention,
            true,
            slice.TotalEntries,
            // The read hands back the last `min(total, MaxNewRows)` rows; the ones newer than what the caller has
            // already seen are the tail of those. Bounded by the read itself, so a session that wrote ten thousand
            // rows since the last tick costs two hundred strings.
            [.. rows.Skip(Math.Max(0, rows.Count - Math.Max(0, slice.TotalEntries - since)))],
            [.. rows.TakeLast(TailRows)],
            await handle.HasOutstandingBackgroundShellsAsync().ConfigureAwait(false));
    };

    public void Dispose()
    {
        _disposed = true;
        Probe = null;
        _watches.Clear();
        _timer?.Dispose();
        _timer = null;
    }
}
