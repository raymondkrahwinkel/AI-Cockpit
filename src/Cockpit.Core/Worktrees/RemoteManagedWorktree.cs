namespace Cockpit.Core.Worktrees;

public sealed record RemoteManagedWorktree(string ProjectId, string ProjectName, string Branch, bool Exists,
    bool HasUncommittedChanges, int StrandableCommits, bool IsOwnerLive);
