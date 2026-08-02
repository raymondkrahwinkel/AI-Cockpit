using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;

namespace Cockpit.Infrastructure.Worktrees;

// See `IWorktreeReconcileGate`. Holds the reconcile task in a volatile field: `Program.cs` assigns it once, synchronously, before the window can open, and every reader afterwards only ever awaits it.
internal sealed class WorktreeReconcileGate(ILogger<WorktreeReconcileGate> logger) : IWorktreeReconcileGate, ISingletonService
{
    private volatile Task _reconcileTask = Task.CompletedTask;

    public void SignalStarted(Task reconcileTask) => _reconcileTask = reconcileTask;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _reconcileTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The reconcile itself already logs and swallows a per-orphan failure without faulting the task as a
            // whole; a fault reaching here would be unexpected. Either way a restore waiting on this gate only
            // needs to know the reconcile is over, not whether it fully succeeded.
            logger.LogWarning(exception, "The startup worktree reconcile did not finish cleanly; restoring session panes anyway.");
        }
    }
}
