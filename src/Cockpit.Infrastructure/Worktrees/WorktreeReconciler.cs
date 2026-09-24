using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Worktrees;

// AC-643: ticks the worktree crash net that until now only ran at startup. What an orphaned worktree deserves is
// still entirely `ReconcileAsync`'s decision (clean removed, work retained) — this only stops a cockpit left open
// for a day from hoarding the worktrees of agents that crashed hours ago until the next restart.
public sealed class WorktreeReconciler : ISingletonService, IDisposable
{
    // Disk hygiene, not monitoring: a quarter of an hour is far from a session that is mid-close and still short
    // enough that a crashed agent's worktree does not sit there for the rest of the day.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IWorktreeManager _worktrees;
    private readonly ILogger<WorktreeReconciler> _logger;
    private readonly TimeProvider _time;

    private ITimer? _timer;

    // AC-1380: an int, not a bool — the tick now runs on the threadpool, where two overlapping ticks could
    // otherwise both read this false before either sets it.
    private int _sweeping;
    private bool _disposed;

    public WorktreeReconciler(IWorktreeManager worktrees, ILogger<WorktreeReconciler>? logger = null)
        : this(worktrees, logger, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so a sweep is provable without waiting a quarter of an hour for it.
    internal WorktreeReconciler(IWorktreeManager worktrees, ILogger<WorktreeReconciler>? logger, TimeProvider time)
    {
        _worktrees = worktrees;
        _logger = logger ?? NullLogger<WorktreeReconciler>.Instance;
        _time = time;
    }

    // The sessions alive right now, asked fresh every tick: a worktree owned by anything outside this set is what
    // `ReconcileAsync` treats as orphaned. Set by the cockpit, which owns the session list; nothing sweeps until it is.
    public Func<IReadOnlyCollection<string>>? LiveSessionIds { get; set; }

    // Starts sweeping the clock. Idempotent. No sweep now: `Program.cs` already reconciled this start against the
    // restore roster, which is the wider set while restores are still landing.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _OnTick(), null, Interval, Interval);
    }

    // One sweep. Public because the tests drive it directly rather than waiting a quarter of an hour — the same seam
    // `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A sweep that outlasts the interval must not have a second one started on top of it: two of them releasing
        // the same orphan is one removing a worktree the other is still measuring.
        if (LiveSessionIds is null || Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // AC-654: the assistant owns every worktree it makes with `worktree_create` and is in no session list by
            // construction, so it is added here rather than at the wiring — a live set that forgets it reads its
            // worktrees as orphaned and sweeps them from under the agents working in them.
            await _worktrees.ReconcileAsync([AssistantIdentity.PaneId, .. LiveSessionIds()], cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
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
            // A sweep must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a crash net that never catches anything again.
            _logger.LogError(exception, "A worktree reconcile sweep failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        LiveSessionIds = null;
        _timer?.Dispose();
        _timer = null;
    }
}
