namespace Cockpit.Core.Worktrees;

// A worktree's live state for the management panel (AC-85): the record plus what git reports right now, so it is
// never a guess whether removing a worktree would lose work. `StrandableCommits`: commits that removal would strand
// — not in the base branch, not on any remote, not present under a rewritten commit (AC-266); pushed work is safe.
public sealed record WorktreeStatus(WorktreeRecord Record, bool Exists, bool HasUncommittedChanges, int StrandableCommits)
{
    // True when the folder is on disk but git no longer knows it as a working tree — e.g. a removal that couldn't
    // delete the folder, or a prune after manual cleanup. Nothing can be measured, so it is not `HasUncommittedChanges` either.
    public bool WorkingCopyMissing { get; init; }

    // True when there is no working copy left to lose, so a sweep may remove the registry entry without asking.
    // Deliberately not folded into `IsClean`: "clean" is a measurement, and there is nothing here to measure.
    public bool NothingToKeep => !Exists || WorkingCopyMissing;

    // True when the worktree has nothing to lose: it exists as a working copy, holds no uncommitted changes, and
    // carries no commit that exists only here. The one state safe to remove without asking the operator first.
    public bool IsClean => !NothingToKeep && !HasUncommittedChanges && StrandableCommits == 0;
}
