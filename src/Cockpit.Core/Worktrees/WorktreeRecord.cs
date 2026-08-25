namespace Cockpit.Core.Worktrees;

// A git worktree the cockpit created to isolate one session on its own branch (AC-85). The registry of these —
// not the folders on disk — is the source of truth for cleanup: a crash can leave a worktree behind without ever
// running the teardown that would have removed it, and this is the record a later start finds it again by.
public sealed record WorktreeRecord(
    string SessionId,
    string RepositoryRoot,
    string Path,
    string Branch,
    string BaseCommit,
    DateTimeOffset CreatedAt)
{
    // The branch this worktree forked from, used to measure "unmerged commits" against its *current* tip rather
    // than the frozen `BaseCommit` (AC-85) — so a worktree whose commits were since merged reads as clean instead
    // of showing "N commits ahead" forever. Null on old records or a detached HEAD; falls back to the default branch, then `BaseCommit`.
    public string? BaseBranch { get; init; }

    // Whether the worktree is git-locked, which it is from creation until teardown so a stray prune cannot pull it out from under a live session.
    public bool IsLocked { get; init; } = true;

    // Set when teardown kept the worktree because it held uncommitted work or unmerged commits: shown for review, never auto-removed (cleanup-policy A).
    public bool IsRetained { get; init; }

    // Whether an agent made this worktree itself via `worktree_create`, as opposed to the worktree a session runs
    // in, which stays protected against its own session (AC-520 fix 5). Named for the exception, not the default:
    // an old record with no such field deserializes to `false`, the safe "protected" reading.
    public bool IsAgentCreated { get; init; }

    // How the source branch was brought up to date before this worktree forked from it (AC-349) — the one thing on
    // this record that describes the moment of creation rather than the worktree itself. Deliberately not persisted:
    // it is what the operator is told once, at start, and a record read back from the registry carries null here.
    public WorktreeSourceRefresh? SourceRefresh { get; init; }
}
