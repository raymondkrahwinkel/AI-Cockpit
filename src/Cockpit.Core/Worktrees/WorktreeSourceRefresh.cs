namespace Cockpit.Core.Worktrees;

/// <summary>
/// What bringing the source branch up to date came to when a worktree was created (AC-349), and what to tell the
/// operator about it. <see cref="Notice"/> is null when nothing happened worth mentioning — the branch was already
/// on its upstream tip, or there is no upstream to be behind of. It carries a sentence in the two cases that are
/// worth a word: the branch was moved (their own checkout is not where they left it), or the session forked from
/// something older than the upstream and they should know why.
/// </summary>
/// <param name="Outcome">Which of the states the update attempt ended in.</param>
/// <param name="BehindCount">How many commits the source branch was behind its upstream, as last known; 0 when there was nothing to be behind of.</param>
/// <param name="Upstream">The upstream ref the source was measured against (e.g. "origin/main"), or null when the branch tracks nothing.</param>
/// <param name="Notice">The operator-facing sentence, or null when there is nothing worth saying.</param>
/// <param name="ForkCommit">
/// The commit the worktree should fork from, when that is not the HEAD read before any of this ran — either because
/// the source branch was moved there, or because it was left alone and the worktree starts from its upstream instead
/// (AC-376). <see cref="Outcome"/> says which of the two happened. Null when the fork base is simply that HEAD, and
/// read back from the repository rather than inferred from whether a command reported success, so an update cut
/// short after git had already moved the branch is still reported as what it is.
/// </param>
public sealed record WorktreeSourceRefresh(
    WorktreeSourceOutcome Outcome,
    int BehindCount,
    string? Upstream,
    string? Notice,
    string? ForkCommit = null)
{
    /// <summary>An outcome with nothing to report: the fork base is current, or there was never an upstream to compare it with.</summary>
    public static WorktreeSourceRefresh Quiet(WorktreeSourceOutcome outcome) => new(outcome, 0, null, null);
}
