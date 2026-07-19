namespace Cockpit.Core.Worktrees;

/// <summary>
/// What the cockpit needs to know about the git repository behind a chosen folder before it isolates a session in
/// a worktree (AC-85): where the repository root is, the commit a worktree would branch from, and the branch that
/// commit is on — <c>null</c> in a detached head, where there is no branch to name but the commit is still a base.
/// </summary>
public sealed record GitRepositoryInfo(string Root, string HeadCommit, string? CurrentBranch)
{
    /// <summary>True when HEAD points straight at a commit with no branch checked out; a worktree still branches from <see cref="HeadCommit"/>.</summary>
    public bool IsDetachedHead => CurrentBranch is null;
}
