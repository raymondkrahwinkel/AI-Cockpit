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

    /// <summary>
    /// Creates a worktree for a session, generating a collision-free branch name from <paramref name="sessionLabel"/>
    /// and <paramref name="sessionId"/> (AC-85) — the convenience both the SDK/headless start path and the TTY launch
    /// path use, so branch naming lives in one place rather than each caller inventing its own.
    /// </summary>
    Task<WorktreeRecord> CreateForSessionAsync(string sessionId, string? sessionLabel, string directory, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The live state of every registered worktree for the management panel (AC-85): each registry record plus what
    /// git reports about it now — folder-exists, uncommitted-changes, commits-ahead — so the panel shows clean vs.
    /// dirty and a destructive remove can be gated behind consent rather than losing work silently.
    /// </summary>
    Task<IReadOnlyList<WorktreeStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Re-owns an existing worktree for a new session (AC-85 reattach): after a crash a worktree's owning session is
    /// gone, and starting a new session "here" hands the same worktree and branch to the new session instead of
    /// orphaning the work — the registry owner is updated and the worktree re-locked. Returns the updated record, or
    /// <c>null</c> when no registered worktree matches <paramref name="worktreePath"/>. The caller enforces that the
    /// old owner is gone (reattaching a live worktree would put two sessions on one tree).
    /// </summary>
    Task<WorktreeRecord?> ReattachAsync(string worktreePath, string newSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears down the worktrees a session owned when it closes (AC-85, cleanup-policy A): a provably clean one — no
    /// changes and no commits ahead of its base — is removed along with its branch; one that holds work is kept and
    /// marked retained, shown for review and never auto-removed. Called on session close.
    /// </summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup against the sessions actually alive (AC-85): a worktree whose owning
    /// session is gone — a crash or a hard close that missed teardown — is released the same way (clean removed,
    /// work retained), and git's own admin entries for folders that vanished are pruned. This is the crash net.
    /// </summary>
    Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken = default);
}
