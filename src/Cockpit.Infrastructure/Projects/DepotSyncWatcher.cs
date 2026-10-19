using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Projects;

// One local project bound to a Depot-backed `ISharedProjectSource`, and the id it is known by there — what
// `DepotSyncWatcher` needs to ask that source whether anything changed since the last look.
public sealed record DepotBoundProject(string ProjectId, ISharedProjectSource Source, string SharedId);

// AC-894: sync was otherwise purely action-driven (Save, Publish) — this ticks every bound project's own
// `PrepareBindingAsync` and compares its `Checksum` against the last one seen. A changed checksum is only ever a
// signal; nothing here writes anything back or overwrites an unsaved local edit.
public sealed class DepotSyncWatcher : ISingletonService, IDisposable
{
    // Same order as WorktreeReconciler's disk sweep: a background check, not a chat the operator is waiting on.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // One slow or unreachable source must not hold up the others, the same guarantee `ProjectsViewModel`'s own
    // `_ListWithTimeoutAsync` already gives `LoadSharedProjectsAsync`.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<DepotSyncWatcher> _logger;
    private readonly TimeProvider _time;

    // The last checksum seen per project id, so a project checked for the first time never reports a change it has
    // nothing to compare against, and a project that goes quiet stays quiet. Concurrent: "Sync now" reaches this
    // from the UI thread while a tick can reach it from the threadpool, on the same or a different project's key.
    private readonly ConcurrentDictionary<string, string> _lastChecksum = new(StringComparer.Ordinal);

    // AC-1570: the projects Depot reported changed and nobody has acted on. A tick only ever adds to it; only "Sync now"
    // clears an entry, so the hint outlives the interval instead of vanishing unseen.
    private readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.Ordinal);

    // The projects whose last check failed, so a lasting failure is logged once, on the way in, not every tick.
    private readonly ConcurrentDictionary<string, byte> _failing = new(StringComparer.Ordinal);

    // AC-1503: the projects `OnFailing` already told someone about, kept for the whole session so a flapping check says it once.
    private readonly ConcurrentDictionary<string, byte> _told = new(StringComparer.Ordinal);

    // What each project was bound to when last seen, so an unbind or a rebind drops its baseline and its flag.
    private readonly ConcurrentDictionary<string, string> _boundTo = new(StringComparer.Ordinal);

    private ITimer? _timer;

    // AC-1380: an int, not a bool — the tick now runs on the threadpool, where two overlapping ticks could
    // otherwise both read this false before either sets it. "Sync now" deliberately never claims it (see
    // `SyncNowAsync`), so it still cannot serialise against a tick — only `_lastChecksum` guards that.
    private int _polling;
    private bool _disposed;

    public DepotSyncWatcher(ILogger<DepotSyncWatcher>? logger = null)
        : this(logger, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so a poll is provable without waiting fifteen minutes for it.
    internal DepotSyncWatcher(ILogger<DepotSyncWatcher>? logger, TimeProvider time)
    {
        _logger = logger ?? NullLogger<DepotSyncWatcher>.Instance;
        _time = time;
    }

    // The projects to poll, asked fresh every tick: which local projects are bound to a Depot source right now, and
    // through which one. Set by the cockpit, which owns the project list; nothing is polled until it is.
    public Func<Task<IReadOnlyList<DepotBoundProject>>>? BoundProjects { get; set; }

    // Told about every check that completed: project id, whether it is flagged changed (AC-1570: sticky until "Sync now"), and the logo bytes
    // `PrepareBindingAsync` re-downloaded regardless (AC-1054) — awaited, so "Sync now" only returns once
    // adoption is done. Set by the cockpit, which owns the badge and logo store that read it.
    public Func<string, bool, byte[]?, Task>? OnChecked { get; set; }

    // AC-1503: told once per project and session, the first time a check cannot get an answer (project id, reason),
    // so "not signed in" is said to the operator and not only logged. Set by the cockpit, which owns the toasts.
    public Action<string, string>? OnFailing { get; set; }

    // Starts polling the clock; the first pass runs at once so the baseline is laid at start. Idempotent.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _OnTick(), null, TimeSpan.Zero, Interval);
    }

    // One pass over every bound project. Public because the tests drive it directly rather than waiting 15 minutes
    // for a timer — the same seam `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A pass that outlasts the interval must not have a second one started on top of it: two checks racing to
        // update `_lastChecksum` is how a change is reported twice, or not at all.
        if (BoundProjects is null || Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var current = await BoundProjects().ConfigureAwait(true);
            await _ForgetStaleAsync(current).ConfigureAwait(true);
            foreach (var bound in current)
            {
                await _CheckAsync(bound, cancellationToken);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    // The "Sync now" button's own seam: one project, checked immediately, outside the timer entirely.
    public async Task SyncNowAsync(string projectId, CancellationToken cancellationToken = default)
    {
        if (BoundProjects is null)
        {
            return;
        }

        var current = await BoundProjects().ConfigureAwait(true);
        await _ForgetStaleAsync(current).ConfigureAwait(true);
        if (current.FirstOrDefault(candidate => candidate.ProjectId == projectId) is { } bound)
        {
            await _CheckAsync(bound, cancellationToken, clearFlag: true).ConfigureAwait(true);
        }
    }

    // A project that is no longer bound, or is bound to another shared project, starts over: no baseline, no flag, and
    // the catalog is told the flag is gone.
    private async Task _ForgetStaleAsync(IReadOnlyList<DepotBoundProject> current)
    {
        var stale = new List<string>();
        foreach (var (projectId, sharedId) in _boundTo)
        {
            if (!current.Any(bound => bound.ProjectId == projectId && bound.SharedId == sharedId))
            {
                stale.Add(projectId);
            }
        }

        foreach (var projectId in stale)
        {
            bool wasFlagged;
            lock (_lastChecksum)
            {
                _boundTo.TryRemove(projectId, out _);
                _lastChecksum.TryRemove(projectId, out _);
                _failing.TryRemove(projectId, out _);
                wasFlagged = _changed.TryRemove(projectId, out _);
            }

            if (wasFlagged && OnChecked is not null)
            {
                await OnChecked(projectId, false, null).ConfigureAwait(true);
            }
        }

        foreach (var bound in current)
        {
            _boundTo[bound.ProjectId] = bound.SharedId;
        }
    }

    private async Task _CheckAsync(DepotBoundProject bound, CancellationToken cancellationToken, bool clearFlag = false)
    {
        SharedProjectBindingResult result;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var bindingTask = bound.Source.PrepareBindingAsync(bound.SharedId, timeoutCts.Token);
            var completed = await Task.WhenAny(bindingTask, Task.Delay(SourceTimeout, cancellationToken)).ConfigureAwait(true);
            if (completed != bindingTask)
            {
                timeoutCts.Cancel();
                _NoteFailure(bound.ProjectId, $"no answer within {SourceTimeout.TotalSeconds:0} seconds.");
                return;
            }

            result = await bindingTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Not signed in, unreachable, the project no longer exists. None of that is news every tick, and
            // nothing has been remembered that did not happen — the next tick tries again.
            _logger.LogDebug(exception, "Checking {ProjectId} for a Depot change failed; the next check will try again.", bound.ProjectId);
            _NoteFailure(bound.ProjectId, exception.Message);
            return;
        }

        if (!result.Succeeded || result.Binding?.Checksum is not { Length: > 0 } checksum)
        {
            // Said rather than swallowed: "not signed in" is the reason a restored cockpit never syncs again, and
            // this used to return on it without a word anywhere.
            var reason = result.Error is { Length: > 0 } error ? error : "no checksum came back.";
            _logger.LogDebug("Checking {ProjectId} for a Depot change returned nothing usable: {Reason}", bound.ProjectId, reason);
            _NoteFailure(bound.ProjectId, reason);
            return;
        }

        _failing.TryRemove(bound.ProjectId, out _);

        // One step, so a tick and "Sync now" on the same project cannot both see the old checksum. The flag is only
        // cleared by a check that got an answer, and a change this very check sees puts it back.
        bool changed;
        bool reported;
        lock (_lastChecksum)
        {
            changed = _lastChecksum.TryGetValue(bound.ProjectId, out var previous)
                && !string.Equals(previous, checksum, StringComparison.Ordinal);
            _lastChecksum[bound.ProjectId] = checksum;
            if (clearFlag)
            {
                _changed.TryRemove(bound.ProjectId, out _);
            }

            reported = changed && _changed.TryAdd(bound.ProjectId, 0);
        }

        if (reported)
        {
            _logger.LogInformation("Depot reported a change to {ProjectId}.", bound.ProjectId);
        }

        if (OnChecked is not null)
        {
            await OnChecked(bound.ProjectId, _changed.ContainsKey(bound.ProjectId), result.Binding?.LogoBytes).ConfigureAwait(true);
        }
    }

    private void _NoteFailure(string projectId, string reason)
    {
        if (_failing.TryAdd(projectId, 0))
        {
            _logger.LogInformation("Checking {ProjectId} for a Depot change started failing: {Reason}", projectId, reason);
        }

        // Claimed only once someone listens: the first pass runs at start, before the app has hung its toast on this, and a
        // failure it found then must still be said by the next check.
        if (OnFailing is { } tell && _told.TryAdd(projectId, 0))
        {
            try
            {
                tell(projectId, reason);
            }
            catch (Exception exception)
            {
                // A toast that could not be shown must not cost the other projects their check.
                _logger.LogWarning(exception, "Telling that checking {ProjectId} for a Depot change failed did not work.", projectId);
            }
        }
    }

    private async void _OnTick()
    {
        try
        {
            await RunOnceAsync();
        }
        catch (Exception exception)
        {
            // A poll must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a watcher that never notices a change again.
            _logger.LogError(exception, "A Depot sync poll failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        BoundProjects = null;
        OnChecked = null;
        OnFailing = null;
        _timer?.Dispose();
        _timer = null;
    }
}
