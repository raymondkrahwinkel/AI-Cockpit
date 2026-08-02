namespace Cockpit.Plugin.Autopilot;

// What a run worktree can do about a pull request (AC-216) — probed once at merge-ready (and at preflight, AC-215).
//
// `IsGitRun`: The worktree is a git working tree with a branch (a git-repo run, not a plain folder).
// `HasRemote`: The repository has at least one git remote to push to.
// `GhAvailable`: The GitHub CLI (`gh`) is on PATH and can be used to open a pull request.
internal sealed record AutopilotPrProbe(bool IsGitRun, bool HasRemote, bool GhAvailable);

// The work to publish for a merge-ready code run (AC-216).
//
// `WorktreePath`: The run worktree the branch lives in — where git/gh run.
// `Branch`: The run branch to push and open the PR from.
// `Title`: The pull request title (and the message for any leftover-work safety commit) — no AI/agent mention.
// `Body`: The pull request body (the run's goal and source link).
internal sealed record AutopilotPrRequest(string WorktreePath, string Branch, string Title, string Body);

// The outcome of publishing — what actually landed, for the operator-facing outcome line.
//
// `Pushed`: The run branch reached the remote.
// `PrUrl`: The opened pull request's url, or null when none was opened (gh absent, or opening failed).
// `Error`: Why publishing did not fully succeed, or null on success — recorded on the run, never thrown.
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

    /// <summary>
    /// Stages and commits any leftover uncommitted work in <paramref name="worktreePath"/> under <paramref name="message"/>
    /// — the same leftover-work safety commit <see cref="PublishAsync"/> makes before it pushes (AC-434: a review-gate
    /// step forks its own throwaway copy of the run worktree to read, and a fork only carries committed history — this
    /// is what makes sure it is not reviewing a stale diff). A clean tree means nothing to commit, not an error. Never
    /// throws; returns false only when a real commit attempt failed.
    /// </summary>
    Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default);
}
