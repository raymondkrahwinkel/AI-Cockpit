using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// AC-643: ticks the worktree crash net that until now only ran at startup. What an orphaned worktree deserves is
// still entirely `ReconcileAsync`'s decision (clean removed, work retained) — this only stops a cockpit left open
// for a day from hoarding the worktrees of agents that crashed hours ago until the next restart.
public sealed class WorktreeReconciler(
    IWorktreeManager worktrees,
    ILogger<WorktreeReconciler>? logger = null) : ISingletonService, IDisposable
{
    // Disk hygiene, not monitoring: a quarter of an hour is far from a session that is mid-close and still short
    // enough that a crashed agent's worktree does not sit there for the rest of the day.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ILogger<WorktreeReconciler> _logger = logger ?? NullLogger<WorktreeReconciler>.Instance;

    private DispatcherTimer? _timer;
    private bool _sweeping;
    private bool _disposed;

    // The sessions alive right now, asked fresh every tick: a worktree owned by anything outside this set is what
    // `ReconcileAsync` treats as orphaned. Set by the cockpit, which owns the session list; nothing sweeps until it is.
    public Func<IReadOnlyCollection<string>>? LiveSessionIds { get; set; }

    // Starts sweeping the clock. Idempotent, and on the UI thread because that is where the session list is read and
    // where a DispatcherTimer has to be created to ever tick at all (AC-368). No sweep now: `Program.cs` already
    // reconciled this start against the restore roster, which is the wider set while restores are still landing.
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

    // One sweep. Public because the tests drive it directly rather than waiting a quarter of an hour — the same seam
    // `CiWatcher.RunOnceAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A sweep that outlasts the interval must not have a second one started on top of it: two of them releasing
        // the same orphan is one removing a worktree the other is still measuring.
        if (_sweeping || LiveSessionIds is null)
        {
            return;
        }

        _sweeping = true;
        try
        {
            await worktrees.ReconcileAsync(LiveSessionIds(), cancellationToken);
        }
        finally
        {
            _sweeping = false;
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
            // A sweep must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a crash net that never catches anything again.
            _logger.LogError(exception, "A worktree reconcile sweep failed; the next one will try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        LiveSessionIds = null;

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}
