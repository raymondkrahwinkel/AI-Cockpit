namespace Cockpit.Core.Worktrees;

/// <summary>
/// Whether creating a worktree may touch the branch it forks from (AC-376). The operator asking for an isolated
/// session wants their own checkout carried forward with it; an agent isolating a subtask has no business writing to
/// a folder it merely named, so it gets the same fork base without the branch moving under anyone.
/// </summary>
public enum WorktreeSourceHandling
{
    /// <summary>Fast-forward the source branch where that is safe, so the operator's checkout comes along.</summary>
    BringUpToDate,

    /// <summary>Never write to the source checkout: where a fast-forward would have been, fork from the upstream tip instead.</summary>
    LeaveSourceAlone,
}
