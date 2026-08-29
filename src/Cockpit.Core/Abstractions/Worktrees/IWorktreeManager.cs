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
    /// Raised once the source branch has been dealt with, before the worktree itself is made (AC-349) — the moment
    /// the operator's own checkout may have moved. Deliberately not the returned record: a start that is cancelled
    /// or fails after this point never hands it to anyone, yet a moved branch must still be reported.
    /// </summary>
    event Action<WorktreeSourceRefresh>? SourceRefreshed;

    /// <summary>
    /// Reports the git repository behind <paramref name="directory"/>, or <c>null</c> when it is not inside one (or
    /// has no commit to branch from) — the signal the New-session dialog uses to offer or grey out isolation,
    /// rather than failing at spawn time.
    /// </summary>
    Task<GitRepositoryInfo?> DetectRepositoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worktree for <paramref name="sessionId"/> on new branch <paramref name="branch"/>: fast-forwards
    /// the source when clean (AC-349), else forks local HEAD. <paramref name="handling"/> gates that move to
    /// operator sessions (AC-376); <paramref name="isAgentCreated"/> flags an agent's subtask worktree (AC-520 fix 5).
    /// </summary>
    Task<WorktreeRecord> CreateAsync(
        string sessionId,
        string branch,
        string directory,
        WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate,
        bool isAgentCreated = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worktree for a session, generating a collision-free branch name from <paramref name="sessionLabel"/>
    /// and <paramref name="sessionId"/> (AC-85) — the convenience both the SDK/headless start path and the TTY launch
    /// path use, so branch naming lives in one place. <paramref name="isAgentCreated"/> carries the same meaning as on <see cref="CreateAsync"/>.
    /// </summary>
    Task<WorktreeRecord> CreateForSessionAsync(
        string sessionId,
        string? sessionLabel,
        string directory,
        WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate,
        bool isAgentCreated = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The live state of every registered worktree for the management panel (AC-85): each registry record plus what
    /// git reports about it now — folder-exists, uncommitted-changes, commits-that-exist-only-here — so the panel
    /// shows clean vs. dirty and a destructive remove can be gated behind consent rather than losing work silently.
    /// </summary>
    Task<IReadOnlyList<WorktreeStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the worktree holds neither uncommitted changes nor a commit that exists nowhere else — not in its base
    /// branch, not on a remote, and not in the base under a rewritten commit (AC-266). The test teardown uses this to
    /// decide a worktree is removable rather than work to keep (cleanup-policy A).
    /// </summary>
    Task<bool> IsCleanAsync(WorktreeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the worktree holds uncommitted changes or untracked files (non-empty <c>git status --porcelain</c>) —
    /// the content a force-remove would discard. Gates the agent remove tool's dirty-removal consent, not
    /// <see cref="IsCleanAsync"/>: commits-only isn't prompted (force-remove keeps them); untracked files are.
    /// </summary>
    Task<bool> HasUncommittedChangesAsync(WorktreeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the worktree and its registry entry. Without <paramref name="force"/> git refuses a dirty worktree
    /// (safety net); <paramref name="force"/> overrides. A worktree — or its repository (AC-507) — already gone is
    /// dropped from the registry alone, folder untouched (AC-342); returns a message when so, else <c>null</c>.
    /// </summary>
    Task<string?> RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-owns an existing worktree for a new session (AC-85 reattach): after a crash, starting a new session "here"
    /// hands the same worktree and branch to it instead of orphaning the work. Returns the updated record, or
    /// <c>null</c> when none matches <paramref name="worktreePath"/>; caller confirms the old owner is gone first.
    /// </summary>
    Task<WorktreeRecord?> ReattachAsync(string worktreePath, string newSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips a worktree's registry record of its owning session (AC-520 fix 6) — the operator's explicit "release" on
    /// a row only live because of an open restore offer (AC-410), nothing actually running. Becomes an ordinary orphan
    /// for the next <see cref="ReconcileAsync"/> sweep; agent remove admits it once owner leaves <c>LiveSessionIds</c>.
    /// </summary>
    Task ReleaseOwnershipAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears down the worktrees a session owned when it closes (AC-85, cleanup-policy A): a provably clean one — no
    /// changes and no commit that exists only there — is removed along with its branch; one that holds work is kept
    /// and marked retained, shown for review and never auto-removed. Called on session close.
    /// </summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes empty Docker Compose networks belonging to a closed session's worktrees. Networks with containers are
    /// left alone.
    /// </summary>
    Task CleanupDockerNetworksAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup against the sessions actually alive (AC-85): a worktree whose owning
    /// session is gone — a crash or a hard close that missed teardown — is released the same way (clean removed,
    /// work retained), and git's own admin entries for folders that vanished are pruned. This is the crash net.
    /// </summary>
    Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken = default);
}
