namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Whether an epic's sub is already delivered (AC-346) — the "merge-ready ≠ merged" half of the epic-runner: a sub
/// that finished its own run only reached a merge-ready PR, not <c>origin/main</c>. Split out of
/// <see cref="AutopilotEpicRunner"/> as its own interface so a test can fake "already merged"/"not yet" per issue
/// without shelling out to git — the real check (<see cref="GitEpicSubMergeChecker"/>) is a thin, separately testable
/// wrapper around <see cref="GitCommandLine"/>.
/// </summary>
internal interface IEpicSubMergeChecker
{
    /// <summary>Whether <paramref name="issueId"/>'s work is already in <c>origin/main</c>.</summary>
    Task<bool> IsMergedAsync(string issueId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real <see cref="IEpicSubMergeChecker"/> (AC-346): a sub counts as merged when <c>origin/main</c>'s history
/// carries a commit whose first line starts with its ticket number — this project's own commit format (<c>{TICKET}</c>
/// on the first line, see this repo's own <c>git log</c>). Checked against <c>origin/main</c>, never a local branch or
/// worktree (the ticket's own wording): a run's local worktree can carry a sub's commits before they are actually
/// merged, and trusting that would let the epic-runner "see" a step as done before the human has merged its PR.
/// <c>git fetch</c> first so a merge someone else just clicked through is not missed by a stale local ref — best-effort,
/// since an offline box or a repo with no configured remote should still fall back to whatever <c>origin/main</c>
/// already points at locally rather than refuse the whole check.
/// </summary>
internal sealed class GitEpicSubMergeChecker(string repositoryDirectory) : IEpicSubMergeChecker
{
    public async Task<bool> IsMergedAsync(string issueId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueId) || !Directory.Exists(repositoryDirectory))
        {
            return false;
        }

        _ = await GitCommandLine.RunAsync("git", ["fetch", "origin", "main"], repositoryDirectory, cancellationToken);

        var result = await GitCommandLine.RunAsync(
            "git",
            ["log", "origin/main", $"--grep=^{issueId}", "--oneline"],
            repositoryDirectory,
            cancellationToken);

        return result.Ok && result.StdOut.Trim().Length > 0;
    }
}
