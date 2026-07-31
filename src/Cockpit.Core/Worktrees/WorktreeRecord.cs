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
    /// <summary>
    /// The branch the worktree was forked from (e.g. "main") when known — the reference the cleanup check measures
    /// "unmerged commits" against, using that branch's <em>current</em> tip rather than the frozen <see cref="BaseCommit"/>.
    /// This is what lets a worktree whose commits have since been merged read as clean and clean up, instead of showing
    /// "N commits ahead" forever because the fork point never moves (AC-85). Null on records written before this was
    /// tracked, or when HEAD was detached at creation; the status check then falls back to detecting the repository's
    /// default branch, and finally to <see cref="BaseCommit"/>.
    /// </summary>
    public string? BaseBranch { get; init; }

    /// <summary>Whether the worktree is git-locked, which it is from creation until teardown so a stray prune cannot pull it out from under a live session.</summary>
    public bool IsLocked { get; init; } = true;

    /// <summary>Set when teardown kept the worktree because it held uncommitted work or unmerged commits: shown for review, never auto-removed (cleanup-policy A).</summary>
    public bool IsRetained { get; init; }

    /// <summary>
    /// Whether an agent made this worktree itself, through the <c>worktree_create</c> MCP tool, for its own subtask —
    /// as opposed to the worktree a session runs in, which the UI creates at session start or reattach (AC-520 fix 5).
    /// Nobody runs "in" the first kind, so its own owning session may remove it even while that session is live; the
    /// second kind stays protected against its own session too, because that IS the working directory the session is
    /// using. Named for the case that gets the exception, not the default: an old record has no such field and
    /// deserializes to <see langword="false"/>, which must read as "session-own, protected" — the safe reading — not
    /// silently disable the guard it did not exist to loosen.
    /// </summary>
    public bool IsAgentCreated { get; init; }

    /// <summary>
    /// How the source branch was brought up to date before this worktree forked from it (AC-349) — the one thing on
    /// this record that describes the moment of creation rather than the worktree itself. Deliberately not persisted:
    /// it is what the operator is told once, at start, and a record read back from the registry carries null here.
    /// </summary>
    public WorktreeSourceRefresh? SourceRefresh { get; init; }
}
