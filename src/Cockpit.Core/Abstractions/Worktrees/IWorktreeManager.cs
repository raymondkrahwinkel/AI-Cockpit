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
    /// or fails after this point never hands that record to anyone, and a branch that moved without a word is the
    /// thing this feature exists to prevent. Fires for every creation, including one an agent asked for, and may
    /// arrive on any thread.
    /// </summary>
    event Action<WorktreeSourceRefresh>? SourceRefreshed;

    /// <summary>
    /// Reports the git repository behind <paramref name="directory"/>, or <c>null</c> when it is not inside one (or
    /// has no commit to branch from) — the signal the New-session dialog uses to offer or grey out isolation,
    /// rather than failing at spawn time.
    /// </summary>
    Task<GitRepositoryInfo?> DetectRepositoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worktree for <paramref name="sessionId"/> on a new branch <paramref name="branch"/>, forked from
    /// the repository behind <paramref name="directory"/>, and records it. The source branch is fetched and — only
    /// when it is clean, has nothing of its own and nothing on disk in the way — fast-forwarded first, so the
    /// session starts on the latest state of that branch rather than on whatever was last pulled (AC-349); where
    /// that cannot be done the fork is from the local HEAD and <see cref="WorktreeRecord.SourceRefresh"/> says so.
    /// Throws when <paramref name="directory"/> is not a repository or <paramref name="branch"/> already exists — a
    /// session is never quietly given a branch that is not its own.
    /// <para>
    /// <paramref name="handling"/> decides whether that update may move the source branch. It may for a session the
    /// operator started — their checkout comes along — and may not for one an agent asked for against a folder it
    /// merely named, which forks from the upstream tip instead (AC-376).
    /// </para>
    /// <para>
    /// <paramref name="isAgentCreated"/> marks a worktree an agent made for its own subtask through the
    /// <c>worktree_create</c> MCP tool (AC-520 fix 5), as opposed to the one a session runs in — the default,
    /// <see langword="false"/>, which the UI's own session-start/reattach path leaves alone. See
    /// <see cref="WorktreeRecord.IsAgentCreated"/> for what this changes about removing it.
    /// </para>
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
    /// path use, so branch naming lives in one place rather than each caller inventing its own. <paramref name="isAgentCreated"/>
    /// carries the same meaning as on <see cref="CreateAsync"/>.
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
    /// Whether the worktree still holds uncommitted changes or untracked files right now (a non-empty
    /// <c>git status --porcelain</c>) — the exact content a force-remove would discard; committed history stays on the
    /// branch. The agent-facing remove tool gates a dirty removal behind operator consent on this, not on
    /// <see cref="IsCleanAsync"/>: a worktree that only carries commits — which a force-remove keeps on the branch —
    /// is not prompted for. Untracked files count deliberately: a force-remove deletes them too, and they may be work
    /// the agent has not committed, so their loss is the operator's call, not a silent one.
    /// </summary>
    Task<bool> HasUncommittedChangesAsync(WorktreeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the worktree and its registry entry. Without <paramref name="force"/> git itself refuses a worktree
    /// with uncommitted work, which is the safety net; <paramref name="force"/> is the operator's explicit override.
    /// <para>
    /// A worktree whose folder is already gone is removed by dropping its registry entry alone, even when git refuses
    /// the path or cannot be run for it at all: there is nothing left on disk to lose, and the branch survives either
    /// way. Without that, an entry git no longer knows about could never be removed (AC-342). Every other refusal
    /// throws with what git said.
    /// </para>
    /// <para>
    /// A worktree whose <em>repository</em> is gone (AC-507) is removed the same way — dropping the entry is the only
    /// possible removal, because there is no repository left to ask git to do it — but that folder is never touched:
    /// it may still hold uncommitted work with nowhere else to be. Returns a message to surface when that happened,
    /// so "removed" is never mistaken for "discarded"; <c>null</c> on a plain removal with nothing left behind to
    /// mention.
    /// </para>
    /// </summary>
    Task<string?> RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-owns an existing worktree for a new session (AC-85 reattach): after a crash a worktree's owning session is
    /// gone, and starting a new session "here" hands the same worktree and branch to the new session instead of
    /// orphaning the work — the registry owner is updated and the worktree re-locked. Returns the updated record, or
    /// <c>null</c> when no registered worktree matches <paramref name="worktreePath"/>. The caller enforces that the
    /// old owner is gone (reattaching a live worktree would put two sessions on one tree).
    /// </summary>
    Task<WorktreeRecord?> ReattachAsync(string worktreePath, string newSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strips a worktree's registry record of its owning session (AC-520 fix 6) — the operator's explicit "release"
    /// on a row that only counts as live because of an open restore offer (AC-410) with nothing actually running
    /// behind it. Turns the record into an ordinary orphan: the next <see cref="ReconcileAsync"/> sweep picks it up
    /// like any other (clean removed, work retained for review), and the agent-facing remove guard admits it because
    /// its owner is no longer in <c>LiveSessionIds</c>. Nothing is removed here — only ownership is given up; Remove
    /// and Reattach on the row do the rest. A no-op when no registered worktree matches <paramref name="worktreePath"/>.
    /// </summary>
    Task ReleaseOwnershipAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tears down the worktrees a session owned when it closes (AC-85, cleanup-policy A): a provably clean one — no
    /// changes and no commit that exists only there — is removed along with its branch; one that holds work is kept
    /// and marked retained, shown for review and never auto-removed. Called on session close.
    /// </summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup against the sessions actually alive (AC-85): a worktree whose owning
    /// session is gone — a crash or a hard close that missed teardown — is released the same way (clean removed,
    /// work retained), and git's own admin entries for folders that vanished are pruned. This is the crash net.
    /// </summary>
    Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken = default);
}
