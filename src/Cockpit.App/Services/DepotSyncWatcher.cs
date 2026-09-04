using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// One local project bound to a Depot-backed `ISharedProjectSource`, and the id it is known by there — what
// `DepotSyncWatcher` needs to ask that source whether anything changed since the last look.
public sealed record DepotBoundProject(string ProjectId, ISharedProjectSource Source, string SharedId);

// AC-894: sync was otherwise purely action-driven (Save, Publish) — this ticks every bound project's own
// `PrepareBindingAsync` and compares its `Checksum` against the last one seen. A changed checksum is only ever a
// signal; nothing here writes anything back or overwrites an unsaved local edit.
public sealed class DepotSyncWatcher(
    ILogger<DepotSyncWatcher>? logger = null) : ISingletonService, IDisposable
{
    // Same order as WorktreeReconciler's disk sweep: a background check, not a chat the operator is waiting on.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // One slow or unreachable source must not hold up the others, the same guarantee `ProjectsViewModel`'s own
    // `_ListWithTimeoutAsync` already gives `LoadSharedProjectsAsync`.
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<DepotSyncWatcher> _logger = logger ?? NullLogger<DepotSyncWatcher>.Instance;

    // The last checksum seen per project id, so a project checked for the first time never reports a change it has
    // nothing to compare against, and a project that goes quiet stays quiet.
    private readonly Dictionary<string, string> _lastChecksum = new(StringComparer.Ordinal);

    private DispatcherTimer? _timer;
    private bool _polling;
    private bool _disposed;

    // The projects to poll, asked fresh every tick: which local projects are bound to a Depot source right now, and
    // through which one. Set by the cockpit, which owns the project list; nothing is polled until it is.
    public Func<IReadOnlyList<DepotBoundProject>>? BoundProjects { get; set; }

    // Told about every check that completed: project id, whether its checksum moved, and the logo bytes
    // `PrepareBindingAsync` re-downloaded regardless (AC-1054) — awaited, so "Sync now" only returns once
    // adoption is done. Set by the cockpit, which owns the badge and logo store that read it.
    public Func<string, bool, byte[]?, Task>? OnChecked { get; set; }

    // Starts polling the clock. Idempotent, and on the UI thread because that is where a DispatcherTimer has to be
    // created to ever tick at all (AC-368).
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

    // One pass over every bound project. Public because the tests drive it directly rather than waiting 15 minutes
    // for a timer — the same seam `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A pass that outlasts the interval must not have a second one started on top of it: two checks racing to
        // update `_lastChecksum` is how a change is reported twice, or not at all.
        if (_polling || BoundProjects is null)
        {
            return;
        }

        _polling = true;
        try
        {
            foreach (var bound in BoundProjects())
            {
                await _CheckAsync(bound, cancellationToken);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    // The "Sync now" button's own seam: one project, checked immediately, outside the timer entirely.
    public Task SyncNowAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var bound = BoundProjects?.Invoke().FirstOrDefault(candidate => candidate.ProjectId == projectId);
        return bound is null ? Task.CompletedTask : _CheckAsync(bound, cancellationToken);
    }

    private async Task _CheckAsync(DepotBoundProject bound, CancellationToken cancellationToken)
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
                return;
            }

            result = await bindingTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Not signed in, unreachable, the project no longer exists. None of that is news every tick, and
            // nothing has been remembered that did not happen — the next tick tries again.
            _logger.LogDebug(exception, "Checking {ProjectId} for a Depot change failed; the next check will try again.", bound.ProjectId);
            return;
        }

        if (!result.Succeeded || result.Binding?.Checksum is not { Length: > 0 } checksum)
        {
            // Said rather than swallowed: "not signed in" is the reason a restored cockpit never syncs again, and
            // this used to return on it without a word anywhere.
            _logger.LogDebug(
                "Checking {ProjectId} for a Depot change returned nothing usable: {Reason}",
                bound.ProjectId,
                result.Error is { Length: > 0 } reason ? reason : "no checksum came back.");

            return;
        }

        var changed = _lastChecksum.TryGetValue(bound.ProjectId, out var previous)
            && !string.Equals(previous, checksum, StringComparison.Ordinal);
        _lastChecksum[bound.ProjectId] = checksum;

        if (OnChecked is not null)
        {
            await OnChecked(bound.ProjectId, changed, result.Binding!.LogoBytes).ConfigureAwait(true);
        }
    }

    private async void _OnTick(object? sender, EventArgs e)
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

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
