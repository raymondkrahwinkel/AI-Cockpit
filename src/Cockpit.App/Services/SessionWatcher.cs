using System.Text.RegularExpressions;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// One watched pane as a tick finds it. Null from `SessionWatcher.Probe` is the pane being gone, itself one of the
// events rather than an error. `NewRows` is the bounded set of rows added since the tick's row count — what
// `pattern` matches against; `LastRows` is the last few rows regardless, so every report carries content.
public sealed record WatchedPane(
    string Title,
    SessionStatus Status,
    bool NeedsAttention,
    bool HasTranscript,
    int TranscriptRows,
    IReadOnlyList<string> NewRows,
    IReadOnlyList<string> LastRows);

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
public sealed class SessionWatcher(IAgentMessageInbox inbox, ILogger<SessionWatcher>? logger = null)
    : ISingletonService, IDisposable
{
    // Short enough that "it finished" is news while the operator is still asking, and cheap enough to afford at that
    // rate: a tick reads collections the UI already holds, and reads nothing at all when nothing is armed.
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

    private readonly ILogger<SessionWatcher> _logger = logger ?? NullLogger<SessionWatcher>.Instance;

    private readonly Dictionary<string, Armed> _watches = new(StringComparer.Ordinal);

    private DispatcherTimer? _timer;
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

    // The live pane behind a pane id, at the row count the caller has already seen. Replaced by the tests, which
    // have no cockpit and no UI thread.
    public Func<string, int, WatchedPane?>? Probe { get; set; }

    // The clock the `stuck` threshold is measured on. A seam for the same reason `Probe` is one: a test that had to
    // wait fifteen real minutes to see the one event that does not read status would never be written.
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // Starts watching the clock. Idempotent, and built on the UI thread — that is where the session list is read and
    // where a `DispatcherTimer` has to be created to ever tick at all (AC-368).
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += _OnTick;
        _timer.Start();
    }

    // Arms a watch on one pane, replacing whatever was armed on it before. Refuses rather than throws: the caller is
    // a model whose next sentence is spoken to the operator.
    public AssistantWatchResult Watch(string paneId, IReadOnlyList<string>? events, int? afterMinutes, string? pattern)
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

        if (Probe(paneId, 0) is not { } pane)
        {
            return AssistantWatchResult.Refused(
                $"There is no session on pane '{paneId}'. Take a pane id from list_sessions.");
        }

        var wantsTranscript = wanted.Contains(SessionWatchEvents.Stuck) || wanted.Contains(SessionWatchEvents.Pattern);
        if (wantsTranscript && !pane.HasTranscript)
        {
            return AssistantWatchResult.Refused(
                $"'{pane.Title}' runs in a terminal and keeps no transcript in the cockpit, so stuck and pattern "
                + "cannot be watched on it. busy-to-idle, needs-attention and gone still can.");
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
                // taking the UI thread with it.
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

    // One look at every armed pane. Public because the tests drive it directly rather than waiting on the timer —
    // the same seam `CiWatcher.RunOnceAsync` opens.
    public void RunOnce()
    {
        if (_watches.Count == 0 || Probe is null)
        {
            return;
        }

        foreach (var (paneId, watch) in _watches.ToList())
        {
            try
            {
                _Look(paneId, watch);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Looking at watched pane {PaneId} failed; the next tick tries again.", paneId);
            }
        }
    }

    private void _Look(string paneId, Armed watch)
    {
        var pane = Probe!(paneId, watch.Rows);

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
            && pane.Status is SessionStatus.Idle or SessionStatus.Done)
        {
            watch.Reported = true;
            _Report(paneId, pane.Title, SessionWatchEvents.BusyToIdle,
                "it stopped working. The lines below say whether that is finished or a question waiting for an "
                    + "answer — read them before you report either.",
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
        if (pane.TranscriptRows > watch.Rows)
        {
            watch.LastGrowth = Clock();
            watch.ReportedStuck = false;
        }
        else if (watch.Events.Contains(SessionWatchEvents.Stuck)
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

        watch.Rows = pane.TranscriptRows;
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

        inbox.Deliver(
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

    private void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            RunOnce();
        }
        catch (Exception exception)
        {
            // A watcher must never be the reason the cockpit falls over, but a failure that stops the loop silently
            // is a watcher that reports nothing forever.
            _logger.LogError(exception, "A session watch tick failed; the next one will try again.");
        }
    }

    // The probe the cockpit runs on: the live pane behind a pane id, read off the collections the UI already holds.
    // Only an SDK session has a transcript — a TTY pane's is a file its CLI wrote, which is not something to read on
    // the thread that draws, and is why `stuck` and `pattern` are refused for one.
    public static Func<string, int, WatchedPane?> ProbeOf(CockpitViewModel cockpit) => (paneId, since) =>
        cockpit.FindSession(paneId) switch
        {
            SessionViewModel session => new WatchedPane(
                session.Title,
                session.SessionStatus,
                _NeedsAttention(session) || session.HasPendingPermission,
                true,
                session.Transcript.Count,
                [.. session.Transcript.Skip(Math.Max(since, session.Transcript.Count - MaxNewRows)).Select(row => row.TextWithImageSuffix)],
                [.. session.Transcript.Skip(Math.Max(0, session.Transcript.Count - TailRows)).Select(row => row.TextWithImageSuffix)]),

            { } pane => new WatchedPane(pane.Title, pane.SessionStatus, _NeedsAttention(pane), false, 0, [], []),

            _ => null,
        };

    private static bool _NeedsAttention(SessionPanelViewModel pane) => pane.RequestsAttention;

    public void Dispose()
    {
        _disposed = true;
        Probe = null;
        _watches.Clear();

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
