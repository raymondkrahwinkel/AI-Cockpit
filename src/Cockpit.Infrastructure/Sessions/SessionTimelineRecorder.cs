using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1717: the host's one source of time per session. Follows `registry.All` like the StallNudger, without its filter
// on StartedByTheAssistant; the assistant is not in `All` and counts as no work. Lines are written in the order they
// happened. Status follows LiveStateChanged, on a TTY pane raised by the desktop handle from the pane's own status.
internal sealed class SessionTimelineRecorder : ISingletonService, IDisposable
{
    private readonly SessionTimelineLog _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<ISessionHandle, Followed> _followed = new(ReferenceEqualityComparer.Instance);

    // The last statusline ticket per pane, kept past the handle so a restored pane carries on with it.
    private readonly Dictionary<string, string> _statuslineTickets = new(StringComparer.Ordinal);
    private ISessionRegistry? _registry;
    private Task _written = Task.CompletedTask;

    public SessionTimelineRecorder(ILogger<SessionTimelineLog> logger)
        : this(new SessionTimelineLog(logger), TimeProvider.System)
    {
    }

    // Test seam: an arbitrary file and clock.
    internal SessionTimelineRecorder(string path, TimeProvider time)
        : this(new SessionTimelineLog(path, NullLogger.Instance), time)
    {
    }

    private SessionTimelineRecorder(SessionTimelineLog log, TimeProvider time)
    {
        _log = log;
        _time = time;
    }

    private sealed class Followed
    {
        public SessionStatus? Status { get; set; }

        public Action Unsubscribe { get; set; } = () => { };
    }

    // Closes what the last run left open, then follows every session in `registry`. Idempotent.
    public void Start(ISessionRegistry registry)
    {
        lock (_gate)
        {
            if (_registry is not null)
            {
                return;
            }

            _registry = registry;
            _written = _CloseLeftOpenAsync();
        }

        registry.Changed += _OnRegistryChanged;
        _Follow();
    }

    // Only the status text goes in; a pane outside `All` (the assistant) is not recorded.
    public void RecordStatusline(string paneId, string statusline)
    {
        lock (_gate)
        {
            if (_registry?.Find(paneId) is not { } handle)
            {
                return;
            }

            if (TicketId.At(statusline) is { } ticket)
            {
                _statuslineTickets[paneId] = ticket;
            }

            _Append(_Entry(SessionTimelineKind.Statusline, handle) with { Statusline = statusline });
        }
    }

    // Everything handed to the log so far has been written.
    internal Task FlushAsync()
    {
        lock (_gate)
        {
            return _written;
        }
    }

    private void _OnRegistryChanged(object? sender, EventArgs e) => _Follow();

    private void _Follow()
    {
        lock (_gate)
        {
            if (_registry is not { } registry)
            {
                return;
            }

            var live = registry.All.ToHashSet<ISessionHandle>(ReferenceEqualityComparer.Instance);
            foreach (var gone in _followed.Keys.Where(handle => !live.Contains(handle)).ToList())
            {
                _followed[gone].Unsubscribe();
                if (_IsOpen(_followed[gone].Status))
                {
                    _Append(_Entry(SessionTimelineKind.Closed, gone));
                }

                _followed.Remove(gone);
            }

            foreach (var handle in live.Where(handle => !_followed.ContainsKey(handle)))
            {
                var followed = new Followed();
                Action<SessionLiveState> onLiveState = state => _OnStatus(handle, followed, state.Status);
                Action<SessionOperatorAction> onOperator = action => _OnOperator(handle, action);
                handle.LiveStateChanged += onLiveState;
                handle.OperatorActed += onOperator;
                followed.Unsubscribe = () =>
                {
                    handle.LiveStateChanged -= onLiveState;
                    handle.OperatorActed -= onOperator;
                };
                _followed[handle] = followed;
                _OnStatus(handle, followed, handle.SessionStatus);
            }
        }
    }

    private void _OnStatus(ISessionHandle handle, Followed followed, SessionStatus status)
    {
        lock (_gate)
        {
            if (followed.Status == status || !_followed.ContainsKey(handle))
            {
                return;
            }

            followed.Status = status;
            _Append(_Entry(SessionTimelineKind.Status, handle) with { Status = status });
        }
    }

    private void _OnOperator(ISessionHandle handle, SessionOperatorAction action)
    {
        lock (_gate)
        {
            _Append(_Entry(SessionTimelineKind.Operator, handle) with { Action = action });
        }
    }

    // The ticket at this moment: the last statusline that named one, else the session's name, else its branch.
    private SessionTimelineEntry _Entry(SessionTimelineKind kind, ISessionHandle handle) => new(
        kind,
        _time.GetUtcNow(),
        handle.PaneId,
        handle.ProjectId,
        handle.RunId,
        _statuslineTickets.GetValueOrDefault(handle.PaneId)
            ?? TicketId.At(handle.Title)
            ?? TicketId.At(handle.WorktreeBranch?[(handle.WorktreeBranch.LastIndexOf('/') + 1)..]));

    // Chained under `_gate`, so lines land in the order they were stamped.
    private void _Append(SessionTimelineEntry entry)
    {
        var previous = _written;
        _written = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await _log.RecordAsync(entry).ConfigureAwait(false);
        });
    }

    // A pane the last run left busy or waiting gets its interval closed at the last moment that run wrote: its stop
    // marker after a clean shutdown, its last line after a crash.
    private async Task _CloseLeftOpenAsync()
    {
        var recent = await _log.ReadRecentAsync(int.MaxValue).ConfigureAwait(false);
        if (recent.Count == 0)
        {
            return;
        }

        var stoppedAt = recent[0].At;
        var lastStatus = new Dictionary<string, SessionTimelineEntry>(StringComparer.Ordinal);
        var lastTicket = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in recent)
        {
            if (entry.PaneId is not { } paneId)
            {
                continue;
            }

            lastTicket.TryAdd(paneId, entry.Ticket);
            if (entry.Kind is SessionTimelineKind.Status or SessionTimelineKind.Closed)
            {
                lastStatus.TryAdd(paneId, entry);
            }
        }

        foreach (var open in lastStatus.Values.Where(entry => entry.Kind == SessionTimelineKind.Status && _IsOpen(entry.Status)))
        {
            var closed = open with
            {
                Kind = SessionTimelineKind.Closed,
                At = stoppedAt,
                Status = null,
                Ticket = lastTicket.GetValueOrDefault(open.PaneId ?? string.Empty),
            };
            await _log.RecordAsync(closed).ConfigureAwait(false);
        }
    }

    private static bool _IsOpen(SessionStatus? status) =>
        status is SessionStatus.Busy or SessionStatus.WorkingBackground or SessionStatus.NeedsAttention;

    public void Dispose()
    {
        if (_registry is { } registry)
        {
            registry.Changed -= _OnRegistryChanged;
        }

        Task written;
        lock (_gate)
        {
            foreach (var followed in _followed.Values)
            {
                followed.Unsubscribe();
            }

            _followed.Clear();
            _registry = null;
            _Append(new SessionTimelineEntry(SessionTimelineKind.Stopped, _time.GetUtcNow(), null, null, null, null));
            written = _written;
        }

        // A stop that cannot write in time loses only its marker; the next start falls back to the last line.
        written.Wait(TimeSpan.FromSeconds(5));
    }
}

internal enum SessionTimelineKind
{
    Status,
    Operator,
    Statusline,

    // The interval a pane left open ends here: it went away, or the cockpit stopped with it open.
    Closed,

    // The cockpit stopped; no pane.
    Stopped,
}

// One line of `session-timeline.jsonl`. A message or an answer never comes in; the statusline is the only text.
internal sealed record SessionTimelineEntry(
    SessionTimelineKind Kind,
    DateTimeOffset At,
    string? PaneId,
    string? ProjectId,
    string? RunId,
    string? Ticket,
    SessionStatus? Status = null,
    SessionOperatorAction? Action = null,
    string? Statusline = null);

// Rolls like UsageHistoryLog and reads only the file it is pointed at; CockpitTimelineReader (AC-1720) reads both.
internal sealed class SessionTimelineLog : JsonlAuditLog<SessionTimelineEntry>
{
    private const int MaxStatuslineLength = 200;

    private readonly ILogger _logger;

    public SessionTimelineLog(ILogger<SessionTimelineLog> logger)
        : this(AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionTimeline), logger)
    {
    }

    public SessionTimelineLog(string path, ILogger logger)
        : base(path, logger)
    {
        _logger = logger;
    }

    protected override string LogName => "session timeline";

    protected override SessionTimelineEntry PrepareForWrite(SessionTimelineEntry entry) =>
        entry.Statusline is { } statusline ? entry with { Statusline = TrimText(statusline, MaxStatuslineLength) } : entry;

    // AC-1719: under the lock retention shares; a roll folds the days it would drop into day aggregates first.
    public new async Task RecordAsync(SessionTimelineEntry entry, CancellationToken cancellationToken = default)
    {
        await SessionDayRetention.TrailsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (new FileInfo(FilePath) is { Exists: true, Length: >= UsageHistoryLog.MaxSizeBytes })
                {
                    await SessionDayRetention.RollAsync<SessionTimelineEntry>(FilePath, line => line.At, _logger).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not roll over the session timeline at {Path}.", FilePath);
            }

            await base.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SessionDayRetention.TrailsLock.Release();
        }
    }
}
