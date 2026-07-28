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
    /// True when the folder is still on disk but the checkout inside it is not — git does not know the path as a
    /// working tree any more. A removal that emptied the folder and could not delete it, or a prune that reclaimed
    /// git's administration after the checkout was cleared by hand, leaves exactly this behind: a row about a
    /// worktree that is not one. Nothing can be measured about it, so it is not <see cref="HasUncommittedChanges"/>
    /// either — that would be a claim about work nobody can point at.
    /// </summary>
    public bool WorkingCopyMissing { get; init; }

    /// <summary>
    /// True when there is no working copy left to lose: the folder is gone, or what survives it is no longer a
    /// working tree. Removing one of these takes nothing but the registry entry — the branch stays, and so does
    /// anything still on disk — so a sweep may take it without asking. Deliberately not folded into
    /// <see cref="IsClean"/>: "clean" is a measurement, and there is nothing here to measure.
    /// </summary>
    public bool NothingToKeep => !Exists || WorkingCopyMissing;

    /// <summary>
    /// True when the worktree has nothing to lose: it exists as a working copy, holds no uncommitted changes, and
    /// carries no commit that exists only here. The one state safe to remove without asking the operator first.
    /// </summary>
    public bool IsClean => !NothingToKeep && !HasUncommittedChanges && StrandableCommits == 0;
}
