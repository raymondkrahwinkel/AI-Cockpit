using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Abstractions.Worktrees;

/// <summary>
/// The persistent record of every worktree the cockpit created (AC-85), surviving a restart so a crash-orphaned
/// worktree can still be reconciled. The source of truth for what exists — read before the folders on disk, which
/// a killed process can leave in a state the registry never recorded.
/// </summary>
public interface IWorktreeRegistry
{
    Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a worktree, replacing any earlier entry for the same path so a retry cannot duplicate it.</summary>
    Task AddAsync(WorktreeRecord record, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry for <paramref name="worktreePath"/>; a no-op when none matches.</summary>
    Task RemoveAsync(string worktreePath, CancellationToken cancellationToken = default);
}
