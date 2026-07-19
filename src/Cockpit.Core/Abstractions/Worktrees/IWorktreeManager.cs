using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Abstractions.Worktrees;

/// <summary>
/// Creates and removes the git worktrees that isolate cockpit sessions on their own branch (AC-85), so several
/// sessions — Claude, Codex, a delegated local model — can work the same repository at once without sharing, and
/// fighting over, one working tree. Host-first: a generic session-lifecycle capability, not a per-provider one.
/// </summary>
public interface IWorktreeManager
{
    /// <summary>
    /// Reports the git repository behind <paramref name="directory"/>, or <c>null</c> when it is not inside one (or
    /// has no commit to branch from) — the signal the New-session dialog uses to offer or grey out isolation,
    /// rather than failing at spawn time.
    /// </summary>
    Task<GitRepositoryInfo?> DetectRepositoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worktree for <paramref name="sessionId"/> on a new branch <paramref name="branch"/>, forked from
    /// the current HEAD of the repository behind <paramref name="directory"/>, and records it. Throws when
    /// <paramref name="directory"/> is not a repository or <paramref name="branch"/> already exists — a session is
    /// never quietly given a branch that is not its own.
    /// </summary>
    Task<WorktreeRecord> CreateAsync(string sessionId, string branch, string directory, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the worktree has neither uncommitted changes nor commits ahead of the base it was branched from —
    /// the test teardown uses to decide a worktree is removable rather than work to keep (cleanup-policy A).
    /// </summary>
    Task<bool> IsCleanAsync(WorktreeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the worktree and its registry entry. Without <paramref name="force"/> git itself refuses a worktree
    /// with uncommitted work, which is the safety net; <paramref name="force"/> is the operator's explicit override.
    /// </summary>
    Task RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default);
}
