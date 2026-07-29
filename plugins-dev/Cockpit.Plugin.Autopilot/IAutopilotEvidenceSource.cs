namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Observes what a step actually changed, independently of what the step agent says it did (AC-255) — the injectable
/// seam behind the coordinator's validation, so the git execution is swappable (a fake in tests, the real
/// <see cref="GitCliEvidenceSource"/> in the app). Mirrors <see cref="IAutopilotPrPublisher"/>, and for the same reason:
/// the harness already holds the run's worktree, so the account of a step's work does not have to come from the party
/// being checked.
/// <para>
/// A step's change is the difference between two moments, hence two calls: <see cref="MarkAsync"/> before its agents
/// start, <see cref="CollectAsync"/> once they report done. Both degrade to null instead of throwing — a run whose work
/// the harness cannot observe falls back to the CEO's own inspection rather than failing, which is what keeps the cheap
/// validation route opt-in on evidence instead of the default.
/// </para>
/// </summary>
internal interface IAutopilotEvidenceSource
{
    /// <summary>
    /// The worktree's current commit, to measure the step against once it finishes. Null when
    /// <paramref name="worktreePath"/> is not a usable git worktree — there is then nothing to observe, and the
    /// validation keeps the deep inspection.
    /// </summary>
    Task<string?> MarkAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// What changed in <paramref name="worktreePath"/> since <paramref name="sinceCommit"/> — committed work, work left
    /// uncommitted, and files git does not track yet. Null when nothing can be observed (not a git worktree, git
    /// refused), never an empty change standing in for a failure: "the step changed nothing" and "we could not look"
    /// lead to opposite instructions for the CEO and must not collapse into one.
    /// </summary>
    Task<AutopilotWorktreeChange?> CollectAsync(string worktreePath, string sinceCommit, CancellationToken cancellationToken = default);
}
