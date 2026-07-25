namespace Cockpit.Plugin.Autopilot;

/// <summary>What a run worktree can do about a pull request (AC-216) — probed once at merge-ready (and at preflight, AC-215).</summary>
/// <param name="IsGitRun">The worktree is a git working tree with a branch (a git-repo run, not a plain folder).</param>
/// <param name="HasRemote">The repository has at least one git remote to push to.</param>
/// <param name="GhAvailable">The GitHub CLI (<c>gh</c>) is on PATH and can be used to open a pull request.</param>
internal sealed record AutopilotPrProbe(bool IsGitRun, bool HasRemote, bool GhAvailable);

/// <summary>The work to publish for a merge-ready code run (AC-216).</summary>
/// <param name="WorktreePath">The run worktree the branch lives in — where git/gh run.</param>
/// <param name="Branch">The run branch to push and open the PR from.</param>
/// <param name="Title">The pull request title (and the message for any leftover-work safety commit) — no AI/agent mention.</param>
/// <param name="Body">The pull request body (the run's goal and source link).</param>
internal sealed record AutopilotPrRequest(string WorktreePath, string Branch, string Title, string Body);

/// <summary>The outcome of publishing — what actually landed, for the operator-facing outcome line.</summary>
/// <param name="Pushed">The run branch reached the remote.</param>
/// <param name="PrUrl">The opened pull request's url, or null when none was opened (gh absent, or opening failed).</param>
/// <param name="Error">Why publishing did not fully succeed, or null on success — recorded on the run, never thrown.</param>
internal sealed record AutopilotPrPublishResult(bool Pushed, string? PrUrl, string? Error);

/// <summary>
/// Pushes a merge-ready code run's branch and opens its pull request (AC-216) — the injectable seam behind the
/// coordinator's finalization, so the git/gh execution is swappable (a fake in tests, the real <see cref="GitCliPrPublisher"/>
/// in the app). Provider/host-neutral: it drives the operator's own <c>git</c>/<c>gh</c> with their own auth, and hard-codes
/// no credentials. Never throws — a failure comes back as an <see cref="AutopilotPrPublishResult.Error"/> the run shows,
/// because a publish fault must not crash a run that already did its work.
/// </summary>
internal interface IAutopilotPrPublisher
{
    /// <summary>Probes what <paramref name="worktreePath"/> can do about a PR (git run, remote, gh). Never throws — an unprobeable path degrades to all-false.</summary>
    Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits any leftover work, pushes the branch, and — when <paramref name="createPullRequest"/> — opens the PR.
    /// Never throws; the result carries what landed and any error.
    /// </summary>
    Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default);
}
