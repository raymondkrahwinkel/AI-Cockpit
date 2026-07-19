namespace Cockpit.Core.Worktrees;

/// <summary>
/// A git worktree the cockpit created to isolate one session on its own branch (AC-85). The registry of these —
/// not the folders on disk — is the source of truth for cleanup: a crash can leave a worktree behind without ever
/// running the teardown that would have removed it, and this is the record a later start finds it again by.
/// </summary>
public sealed record WorktreeRecord(
    string SessionId,
    string RepositoryRoot,
    string Path,
    string Branch,
    string BaseCommit,
    DateTimeOffset CreatedAt)
{
    /// <summary>Whether the worktree is git-locked, which it is from creation until teardown so a stray prune cannot pull it out from under a live session.</summary>
    public bool IsLocked { get; init; } = true;

    /// <summary>Set when teardown kept the worktree because it held uncommitted work or unmerged commits: shown for review, never auto-removed (cleanup-policy A).</summary>
    public bool IsRetained { get; init; }
}
