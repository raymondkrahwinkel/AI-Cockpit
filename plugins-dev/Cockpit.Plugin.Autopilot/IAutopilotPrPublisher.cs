namespace Cockpit.Plugin.Autopilot;

// What a run worktree can do about a pull request (AC-216) — probed once at merge-ready (and at preflight, AC-215).
// `IsGitRun`: has a branch. `HasRemote`: has a git remote to push to. `GhAvailable`: `gh` is on PATH.
internal sealed record AutopilotPrProbe(bool IsGitRun, bool HasRemote, bool GhAvailable);

// The work to publish for a merge-ready code run (AC-216). `Title` doubles as the leftover-work safety commit
// message — no AI/agent mention. `Body` is the PR body (the run's goal and source link).
internal sealed record AutopilotPrRequest(string WorktreePath, string Branch, string Title, string Body);

// What became of work a step committed on a worktree of its own instead of on the run's branch (AC-1037).
// `Recovered`: cherry-picked onto the run branch, oldest first. `Stranded`: still only on the step's own branch,
// from the one recovery stopped on. `Error`: null only when the branch really was read and everything belongs.
internal sealed record AutopilotStrayCommits(IReadOnlyList<string> Recovered, IReadOnlyList<string> Stranded, string? Error)
{
    // The step's work is on the run's branch and the harness knows it — the only all-clear this type has.
    public static readonly AutopilotStrayCommits None = new([], [], null);

    // The check itself did not run. Deliberately not `None`: "could not look" and "found nothing" lead to opposite
    // conclusions about a step that reported success, and collapsing them is the failure shape of AC-1037 itself.
    public static AutopilotStrayCommits Unmeasured(string reason) => new([], [], reason);

    // Whether anything at all was found off the run's branch — false is the ordinary case.
    public bool Found => Recovered.Count > 0 || Stranded.Count > 0;

    // Whether the CEO has to be told something: work was moved, work is stuck, or nobody could look.
    public bool NeedsSaying => Found || Error is not null;
}

// The outcome of publishing — what actually landed, for the operator-facing outcome line. `Pushed`: reached the
// remote. `PrUrl`: null when none was opened. `Error`: null on success — recorded on the run, never thrown.
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

    /// <summary>
    /// Brings work a step committed in <paramref name="stepWorktreePath"/> — a worktree of its own, on a branch of its
    /// own — back onto <paramref name="runBranch"/> by cherry-picking it in <paramref name="runWorktreePath"/> (AC-1037).
    /// A cherry-pick that hits a conflict is aborted and reported as stranded rather than resolved or skipped, and like
    /// every other member here this never throws.
    /// </summary>
    Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(
        string runWorktreePath,
        string runBranch,
        string stepWorktreePath,
        CancellationToken cancellationToken = default);
}
