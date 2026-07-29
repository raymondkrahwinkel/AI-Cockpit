namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// What git itself reported changed in a run's worktree over one step (AC-255) — the raw observation, kept apart from
/// the wording and the spot-checks built on it so collecting and judging are testable separately.
/// </summary>
/// <param name="FilesChanged">Paths git listed as changed against the step's starting commit, relative to the worktree.</param>
/// <param name="UntrackedFiles">
/// New files sitting in the worktree that git does not track yet. Listed separately because a diff cannot show them:
/// a step that wrote a file without adding it has done real work the patch below is silent about.
/// </param>
/// <param name="Patch">The diff as git printed it, cut to what a brief can carry when <see cref="Truncated"/>.</param>
/// <param name="Truncated">
/// The diff was longer than the brief carries and was cut. Said out loud in the validation turn — a silently shortened
/// diff would read as the whole change and is exactly the kind of quiet degradation this gate exists to avoid.
/// </param>
internal sealed record AutopilotWorktreeChange(
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> UntrackedFiles,
    string Patch,
    bool Truncated)
{
    /// <summary>Whether git saw nothing at all — no tracked change and no new file.</summary>
    public bool IsEmpty => FilesChanged.Count == 0 && UntrackedFiles.Count == 0;
}
