namespace Cockpit.Plugin.Autopilot;

// What the merge gate measured about a run branch before asking anyone for a go (AC-1338). `DiffStat` is the
// three-dot `git diff --stat origin/<collection>...HEAD` — where undoing someone else's work shows as a deletion.
// `BuildExitCode` null: no command to run. `Error` is null only when head and diff were both read.
internal sealed record AutopilotMergeEvidence(string HeadSha, string DiffStat, int? BuildExitCode, string BuildOutputTail, string? Error)
{
    public bool BuildPassed => BuildExitCode == 0;
}

// Everything the executor needs to land one sub (AC-1338): the run worktree and its branch, the collection branch
// to rebase onto and merge into, the PR the publisher opened (null when none was), and the build to run afterwards.
internal sealed record AutopilotMergeRequest(string WorktreePath, string Branch, string CollectionBranch, string? PrUrl, string BuildCommand);

// What landing a sub came to (AC-1338). `Merged` is true only once the collection branch on the remote actually
// carries the work. `Route` names how: the PR merge, or the fast-forward push it fell back to without gh.
// `TipSha` is the collection branch's new tip — what the build below ran on. `BuildExitCode` null: not built.
internal sealed record AutopilotMergeResult(bool Merged, string Route, string? TipSha, int? BuildExitCode, string BuildOutputTail, string? Error);

/// <summary>
/// Lands an epic run's sub on its collection branch (AC-1338) — the injectable seam behind the coordinator's merge
/// gate, so the git/gh execution is swappable (a fake in tests, the real <see cref="GitCliMergeExecutor"/> in the app).
/// Never throws: a failure comes back in the result, because a merge fault must not crash a run that already did its work.
/// </summary>
internal interface IAutopilotMergeExecutor
{
    /// <summary>
    /// Measures the run branch against <paramref name="collectionBranch"/> — head sha, three-dot diff-stat, and a build of
    /// the worktree with <paramref name="buildCommand"/> — for the evidence package. Never throws.
    /// </summary>
    Task<AutopilotMergeEvidence> DescribeAsync(string worktreePath, string collectionBranch, string buildCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebases the run branch onto the collection branch's remote tip, lands it there, brings the worktree to the new tip
    /// and builds it. Nothing is ever force-pushed to the collection branch, and nothing here throws.
    /// </summary>
    Task<AutopilotMergeResult> MergeAsync(AutopilotMergeRequest request, CancellationToken cancellationToken = default);
}
