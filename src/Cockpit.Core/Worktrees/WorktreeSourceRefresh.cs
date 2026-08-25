namespace Cockpit.Core.Worktrees;

// What bringing the source branch up to date came to when a worktree was created (AC-349), and what to tell the
// operator about it. `Notice` is null unless the branch was moved or the fork is older than upstream (AC-376).
// `ForkCommit` is read back from the repository rather than inferred from command success, so an update cut short mid-move is still reported accurately.
public sealed record WorktreeSourceRefresh(
    WorktreeSourceOutcome Outcome,
    int BehindCount,
    string? Upstream,
    string? Notice,
    string? ForkCommit = null)
{
    // An outcome with nothing to report: the fork base is current, or there was never an upstream to compare it with.
    public static WorktreeSourceRefresh Quiet(WorktreeSourceOutcome outcome) => new(outcome, 0, null, null);
}
