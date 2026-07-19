namespace Cockpit.Core.Worktrees;

/// <summary>
/// A worktree's live state for the management panel (AC-85): the record from the registry plus what git reports
/// about it right now — whether its folder still exists, whether it holds uncommitted changes, and how many commits
/// it is ahead of the base it was branched from. The panel shows this so it is never a guess whether removing a
/// worktree would lose work.
/// </summary>
public sealed record WorktreeStatus(WorktreeRecord Record, bool Exists, bool HasUncommittedChanges, int CommitsAhead)
{
    /// <summary>
    /// True when the worktree has nothing to lose: it exists, holds no uncommitted changes, and carries no commits
    /// beyond its base. The one state safe to remove without asking the operator first.
    /// </summary>
    public bool IsClean => Exists && !HasUncommittedChanges && CommitsAhead == 0;
}
