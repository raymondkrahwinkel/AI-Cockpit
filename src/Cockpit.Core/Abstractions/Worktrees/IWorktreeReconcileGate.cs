namespace Cockpit.Core.Abstractions.Worktrees;

/// <summary>
/// Lets a session-pane restore (AC-410) wait for the startup worktree reconcile before touching the registry.
/// <c>Program.cs</c> starts <see cref="IWorktreeManager.ReconcileAsync"/> fire-and-forget so it never delays the
/// window, but a racing restore could resume a pane mid-orphan-removal. Singleton, not static, for test swapping.
/// </summary>
public interface IWorktreeReconcileGate
{
    /// <summary>Records the reconcile task <c>Program.cs</c> started, so a later <see cref="WaitAsync"/> has something to wait on.</summary>
    void SignalStarted(Task reconcileTask);

    /// <summary>
    /// Waits for the task <see cref="SignalStarted"/> recorded. Completes immediately when nothing was ever
    /// signalled (a graph with no worktree manager, most unit tests).
    /// </summary>
    Task WaitAsync(CancellationToken cancellationToken = default);
}
