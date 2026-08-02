namespace Cockpit.Core.Worktrees;

// What the cockpit needs to know about the git repository behind a chosen folder before it isolates a session in
// a worktree (AC-85): where the repository root is, the commit a worktree would branch from, and the branch that
// commit is on — `null` in a detached head, where there is no branch to name but the commit is still a base.
public sealed record GitRepositoryInfo(string Root, string HeadCommit, string? CurrentBranch)
{
    // True when HEAD points straight at a commit with no branch checked out; a worktree still branches from `HeadCommit`.
    public bool IsDetachedHead => CurrentBranch is null;
}
