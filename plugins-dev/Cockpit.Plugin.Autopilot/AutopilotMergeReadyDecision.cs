namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// What a merge-ready run does about its pull request (AC-216) — the template-driven outcome, decided from the run's
/// PR expectation and what the environment can actually do (a git run, a remote to push to, the <c>gh</c> CLI to open a
/// PR with). Ordered from "nothing to do" to "everything is in place".
/// </summary>
internal enum AutopilotPrDelivery
{
    /// <summary>An administrative run (the template did not ask for a PR): the run settles merge-ready with no PR and no error for the missing one.</summary>
    NotExpected,

    /// <summary>A code run that ran in a plain folder (no git repository, so no run branch): a PR was expected but cannot be delivered — the work is left where it is.</summary>
    NoGitRun,

    /// <summary>A code run on a git branch with no remote to push to: a PR cannot be delivered — the branch (and its worktree) is left for the operator to publish by hand.</summary>
    NoRemote,

    /// <summary>A code run on a git branch with a remote but no <c>gh</c> CLI: the branch can be pushed, but the operator opens the pull request themselves.</summary>
    PushOnly,

    /// <summary>A code run on a git branch with a remote and <c>gh</c>: the finalizer pushes the branch and opens the pull request.</summary>
    CanCreatePr,
}

/// <summary>
/// The pure decision (and its operator-facing message) for a merge-ready run's pull request (AC-216) — kept static and
/// side-effect-free so the outcome/fallback is exhaustively unit-testable without a live run, a git repo or the network,
/// and so the exact same rule decides the pre-run preflight (AC-215) and the post-run finalization. It only decides
/// <em>what</em> to do; the coordinator's <see cref="IAutopilotPrPublisher"/> does it.
/// </summary>
internal static class AutopilotMergeReadyDecision
{
    /// <summary>
    /// Decides the delivery for a run. <paramref name="deliversPullRequest"/> is the template signal (a code run);
    /// <paramref name="isGitRun"/> is whether the run has a git branch at all (a git-repo run, not a plain folder);
    /// <paramref name="hasRemote"/> and <paramref name="ghAvailable"/> are what the environment probed. A run that expects
    /// no PR is always <see cref="AutopilotPrDelivery.NotExpected"/>, whatever the environment — so an administrative run
    /// never reports a missing-PR fault. The rest degrade fail-soft: no git run &gt; no remote &gt; no gh &gt; ready.
    /// </summary>
    public static AutopilotPrDelivery Decide(bool deliversPullRequest, bool isGitRun, bool hasRemote, bool ghAvailable)
    {
        if (!deliversPullRequest)
        {
            return AutopilotPrDelivery.NotExpected;
        }

        if (!isGitRun)
        {
            return AutopilotPrDelivery.NoGitRun;
        }

        if (!hasRemote)
        {
            return AutopilotPrDelivery.NoRemote;
        }

        return ghAvailable ? AutopilotPrDelivery.CanCreatePr : AutopilotPrDelivery.PushOnly;
    }

    /// <summary>
    /// The operator-facing line for a <em>preflight</em> warning (AC-215), told before the run starts so a code run that
    /// cannot deliver its PR is flagged up front rather than discovered at the end. Null when there is nothing to warn
    /// about — the PR can be created, or none was expected. <paramref name="worktreePath"/> is not known yet at preflight,
    /// so the message names only what is missing.
    /// </summary>
    public static string? PreflightWarning(AutopilotPrDelivery delivery) => delivery switch
    {
        AutopilotPrDelivery.NoGitRun => "This run works in a plain folder (not a git repository), so it cannot open the pull request the template expects — it will run, but you will get no PR.",
        AutopilotPrDelivery.NoRemote => "This repository has no git remote, so Autopilot cannot push the run branch or open a pull request — it will run and leave the work on its branch for you to publish.",
        AutopilotPrDelivery.PushOnly => "The GitHub CLI (gh) is not available, so Autopilot will push the run branch but cannot open the pull request for you — you will open it yourself when the run is done.",
        _ => null,
    };

    /// <summary>
    /// The operator-facing line describing the <em>final</em> outcome (AC-216), shown on the run once it settled
    /// merge-ready — never a silent "done" for a code run that could not produce its PR. <paramref name="branch"/> and
    /// <paramref name="worktreePath"/> tell the operator where the work is so it does not evaporate;
    /// <paramref name="prUrl"/> is the PR that was opened (for <see cref="AutopilotPrDelivery.CanCreatePr"/>).
    /// </summary>
    public static string Outcome(AutopilotPrDelivery delivery, string? branch, string? worktreePath, string? prUrl)
    {
        var where = _Where(branch, worktreePath);
        return delivery switch
        {
            AutopilotPrDelivery.NotExpected => "Run settled merge-ready.",
            AutopilotPrDelivery.NoGitRun => "Run settled merge-ready, but it worked in a plain folder (not a git repository), so no pull request could be created. Review the changes in the run's working directory.",
            AutopilotPrDelivery.NoRemote => $"Run settled merge-ready, but the repository has no git remote, so no pull request could be created. The work is on {where} — push it and open a PR yourself.",
            AutopilotPrDelivery.PushOnly => string.IsNullOrWhiteSpace(prUrl)
                ? $"Run settled merge-ready and pushed {where}. The GitHub CLI (gh) is not available, so open the pull request yourself."
                : $"Run settled merge-ready and pushed {where}: {prUrl}",
            AutopilotPrDelivery.CanCreatePr => string.IsNullOrWhiteSpace(prUrl)
                ? $"Run settled merge-ready and pushed {where}, but opening the pull request failed — open it yourself."
                : $"Run settled merge-ready — pull request opened: {prUrl}",
            _ => "Run settled merge-ready.",
        };
    }

    private static string _Where(string? branch, string? worktreePath)
    {
        var hasBranch = !string.IsNullOrWhiteSpace(branch);
        var hasPath = !string.IsNullOrWhiteSpace(worktreePath);
        return (hasBranch, hasPath) switch
        {
            (true, true) => $"branch “{branch}” ({worktreePath})",
            (true, false) => $"branch “{branch}”",
            (false, true) => $"the run worktree ({worktreePath})",
            _ => "the run branch",
        };
    }
}
