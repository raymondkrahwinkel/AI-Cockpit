namespace Cockpit.Core.Worktrees;

/// <summary>
/// A worktree's live state for the management panel (AC-85): the record from the registry plus what git reports
/// about it right now — whether its folder still exists, whether it holds uncommitted changes, and how many commits
/// exist nowhere but here. The panel shows this so it is never a guess whether removing a worktree would lose work.
/// </summary>
/// <param name="StrandableCommits">
/// Commits that removing this worktree would strand: not in the base branch, not on any remote, and not present in
/// the base under a rewritten commit (AC-266). Pushed work counts as safe — the branch keeps it — so this is not the
/// same as "unmerged".
/// </param>
public sealed record WorktreeStatus(WorktreeRecord Record, bool Exists, bool HasUncommittedChanges, int StrandableCommits)
{
    /// <summary>
    /// True when the worktree has nothing to lose: it exists, holds no uncommitted changes, and carries no commit
    /// that exists only here. The one state safe to remove without asking the operator first.
    /// </summary>
    public bool IsClean => Exists && !HasUncommittedChanges && StrandableCommits == 0;
}
