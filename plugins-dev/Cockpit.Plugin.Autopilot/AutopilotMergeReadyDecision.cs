namespace Cockpit.Plugin.Autopilot;

// What a merge-ready run does about its pull request (AC-216) — the template-driven outcome, decided from the run's
// PR expectation and what the environment can actually do (a git run, a remote to push to, the `gh` CLI to open a
// PR with). Ordered from "nothing to do" to "everything is in place".
internal enum AutopilotPrDelivery
{
    // An administrative run (the template did not ask for a PR): the run settles merge-ready with no PR and no error for the missing one.
    NotExpected,

    // A code run that ran in a plain folder (no git repository, so no run branch): a PR was expected but cannot be delivered — the work is left where it is.
    NoGitRun,

    // A code run on a git branch with no remote to push to: a PR cannot be delivered — the branch (and its worktree) is left for the operator to publish by hand.
    NoRemote,

    // A code run on a git branch with a remote but no `gh` CLI: the branch can be pushed, but the operator opens the pull request themselves.
    PushOnly,

    // A code run on a git branch with a remote and `gh`: the finalizer pushes the branch and opens the pull request.
    CanCreatePr,
}

// Kept static and side-effect-free so the outcome is exhaustively unit-testable without a live run, git repo, or
// network — the same rule decides both the pre-run preflight (AC-215) and post-run finalization (AC-216).
internal static class AutopilotMergeReadyDecision
{
    // A run that expects no PR is always `NotExpected`, whatever the environment, so an administrative run
    // never reports a missing-PR fault. The rest degrade fail-soft: no git run &gt; no remote &gt; no gh &gt; ready.
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

    // Preflight warning (AC-215), told before the run starts so a code run that cannot deliver its PR is flagged
    // up front. Null when nothing to warn about. `worktreePath` isn't known yet, so the message names only what's missing.
    public static string? PreflightWarning(AutopilotPrDelivery delivery) => delivery switch
    {
        AutopilotPrDelivery.NoGitRun => "This run works in a plain folder (not a git repository), so it cannot open the pull request the template expects — it will run, but you will get no PR.",
        AutopilotPrDelivery.NoRemote => "This repository has no git remote, so Autopilot cannot push the run branch or open a pull request — it will run and leave the work on its branch for you to publish.",
        AutopilotPrDelivery.PushOnly => "The GitHub CLI (gh) is not available, so Autopilot will push the run branch but cannot open the pull request for you — you will open it yourself when the run is done.",
        _ => null,
    };

    // Final outcome line (AC-216) — never a silent "done" for a code run that could not produce its PR.
    // `branch`/`worktreePath` tell the operator where the work is so it doesn't evaporate.
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
