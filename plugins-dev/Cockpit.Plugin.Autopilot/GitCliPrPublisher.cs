namespace Cockpit.Plugin.Autopilot;

// The real `IAutopilotPrPublisher` (AC-216): drives the operator's own `git`/`gh` CLIs in the run worktree to
// push the branch and open a pull request. Provider-neutral, hard-codes no credentials; every process runs
// bounded (see `GitCommandLine`) and every failure swallows into a result rather than crashing a run.
internal sealed class GitCliPrPublisher : IAutopilotPrPublisher
{
    public async Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new AutopilotPrProbe(false, false, false);
        }

        var isGitRun = (await GitCommandLine.RunAsync("git", ["rev-parse", "--is-inside-work-tree"], worktreePath, cancellationToken)).Ok;
        if (!isGitRun)
        {
            return new AutopilotPrProbe(false, false, false);
        }

        var remotes = await GitCommandLine.RunAsync("git", ["remote"], worktreePath, cancellationToken);
        var hasRemote = remotes.Ok && !string.IsNullOrWhiteSpace(remotes.StdOut);

        // gh is usable only when it is installed AND authenticated — an installed-but-logged-out gh cannot open a PR, so
        // treat it as unavailable and fall back to push-only rather than failing at "gh pr create".
        var ghAvailable = (await GitCommandLine.RunAsync("gh", ["auth", "status"], worktreePath, cancellationToken)).Ok;

        return new AutopilotPrProbe(isGitRun, hasRemote, ghAvailable);
    }

    public async Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default)
    {
        var path = request.WorktreePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new AutopilotPrPublishResult(false, null, "The run worktree no longer exists.");
        }

        // Safety commit: the step agents are asked to commit their own work, but stage and commit anything they left
        // uncommitted so the push carries the whole deliverable. The message carries no Co-Authored-By trailer and no
        // AI/agent mention (a hard project rule).
        if (!await EnsureCommittedAsync(path, request.Title, cancellationToken).ConfigureAwait(false))
        {
            return new AutopilotPrPublishResult(false, null, "Could not commit the remaining work.");
        }

        var push = await GitCommandLine.RunAsync("git", ["push", "-u", "origin", request.Branch], path, cancellationToken);
        if (!push.Ok)
        {
            return new AutopilotPrPublishResult(false, null, $"Could not push branch “{request.Branch}”: {push.Error}");
        }

        if (!createPullRequest)
        {
            return new AutopilotPrPublishResult(true, null, null);
        }

        // AC-1337: an explicit base targets the epic run's collection branch instead of gh's default-branch guess.
        // gh refuses a base that does not exist on the remote yet ("Base ref must be a branch") — an epic's first
        // sub, before any collection branch was ever pushed — so create it there first, from the remote's default.
        List<string> arguments = ["pr", "create", "--head", request.Branch, "--title", request.Title, "--body", request.Body];
        if (!string.IsNullOrWhiteSpace(request.Base))
        {
            await _EnsureRemoteBranchExistsAsync(path, request.Base, cancellationToken).ConfigureAwait(false);
            arguments.Add("--base");
            arguments.Add(request.Base);
        }

        var pr = await GitCommandLine.RunAsync("gh", arguments, path, cancellationToken);

        if (!pr.Ok)
        {
            // The branch is safely on the remote; only the PR step failed. Report it so the operator opens the PR by hand.
            return new AutopilotPrPublishResult(true, null, $"Pushed the branch, but could not open the pull request: {pr.Error}");
        }

        // gh prints the PR url on stdout.
        var url = pr.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        return new AutopilotPrPublishResult(true, url, null);
    }

    // AC-1337 review: a collection branch nobody has pushed to yet does not exist on the remote, and gh refuses a
    // PR whose base is not a branch there. A no-op when it already exists; best-effort otherwise — a push that
    // fails here still lets `gh pr create` run and report its own, clearer error.
    private static async Task _EnsureRemoteBranchExistsAsync(string worktreePath, string branch, CancellationToken cancellationToken)
    {
        var existing = await GitCommandLine.RunAsync("git", ["ls-remote", "--heads", "origin", branch], worktreePath, cancellationToken);
        if (!existing.Ok || !string.IsNullOrWhiteSpace(existing.StdOut))
        {
            return;
        }

        // Populates the remote-tracking refs `_RemoteDefaultBranchAsync` resolves against — a fresh worktree's
        // `origin/*` refs may already be stale by the time a run reaches merge-ready.
        _ = await GitCommandLine.RunAsync("git", ["fetch", "origin"], worktreePath, cancellationToken);

        if (await _RemoteDefaultBranchAsync(worktreePath, cancellationToken).ConfigureAwait(false) is { } defaultBranch)
        {
            await GitCommandLine.RunAsync("git", ["push", "origin", $"origin/{defaultBranch}:refs/heads/{branch}"], worktreePath, cancellationToken);
        }
    }

    // The remote's own idea of its default branch: `origin/HEAD`'s symbolic ref when set (only `clone` sets it — a
    // plain `fetch` does not), else the first of `main`/`master` that actually resolves.
    private static async Task<string?> _RemoteDefaultBranchAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var originHead = await GitCommandLine.RunAsync("git", ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], worktreePath, cancellationToken);
        var trimmed = originHead.StdOut.Trim();
        if (originHead.Ok && trimmed.StartsWith("origin/", StringComparison.Ordinal))
        {
            return trimmed["origin/".Length..];
        }

        string[] candidates = ["main", "master"];
        foreach (var candidate in candidates)
        {
            if ((await GitCommandLine.RunAsync("git", ["rev-parse", "--verify", "--quiet", $"origin/{candidate}"], worktreePath, cancellationToken)).Ok)
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return false;
        }

        await GitCommandLine.RunAsync("git", ["add", "-A"], worktreePath, cancellationToken);
        var status = await GitCommandLine.RunAsync("git", ["status", "--porcelain"], worktreePath, cancellationToken);
        if (!status.Ok || string.IsNullOrWhiteSpace(status.StdOut))
        {
            // Clean tree (or the status probe itself failed): nothing to commit either way — not an error.
            return true;
        }

        var commit = await GitCommandLine.RunAsync("git", ["commit", "-m", message], worktreePath, cancellationToken);
        return commit.Ok;
    }

    public async Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(
        string runWorktreePath,
        string runBranch,
        string stepWorktreePath,
        CancellationToken cancellationToken = default)
    {
        // Nothing to measure: the step ran in the run's own worktree, or there is no run branch to measure against.
        if (string.IsNullOrWhiteSpace(runBranch)
            || string.Equals(_Normalized(runWorktreePath), _Normalized(stepWorktreePath), StringComparison.OrdinalIgnoreCase))
        {
            return AutopilotStrayCommits.None;
        }

        // A worktree that is gone is not an all-clear either — a session that died took its branch's whereabouts with
        // it, which is the very thing that has to be reported rather than assumed clean.
        if (!Directory.Exists(runWorktreePath) || !Directory.Exists(stepWorktreePath))
        {
            return AutopilotStrayCommits.Unmeasured("one of the two worktrees is no longer there");
        }

        // Work the step left uncommitted in its own worktree is about to be thrown away with it, so commit it first —
        // otherwise "no stray commits" would be an all-clear over a lost change, which is the failure shape of the bug
        // this closes. The same safety commit on the run's side is what lets the cherry-pick below run at all.
        await EnsureCommittedAsync(stepWorktreePath, "Autopilot: work left uncommitted on a step branch", cancellationToken).ConfigureAwait(false);
        await EnsureCommittedAsync(runWorktreePath, "Autopilot: work in progress", cancellationToken).ConfigureAwait(false);

        // Both worktrees share one object store, so the run's branch is nameable from the step's side. --cherry-pick
        // drops commits whose patch the run branch already carries — a gate that reported a fix the shared fix step
        // then applied is not stray work, and cherry-picking it again would only fail as empty.
        var log = await GitCommandLine.RunAsync(
            "git",
            ["log", "--reverse", "--format=%H", "--cherry-pick", "--right-only", $"{runBranch}...HEAD"],
            stepWorktreePath,
            cancellationToken);
        if (!log.Ok)
        {
            // "Could not look" is not "found nothing": returning None here would hand the run an all-clear over a
            // measurement that never happened — the same shape as the bug this closes, one branch further along.
            return AutopilotStrayCommits.Unmeasured(log.Error);
        }

        var stray = log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var recovered = new List<string>();
        for (var index = 0; index < stray.Length; index++)
        {
            var pick = await GitCommandLine.RunAsync("git", ["cherry-pick", stray[index]], runWorktreePath, cancellationToken);
            if (pick.Ok)
            {
                recovered.Add(stray[index]);
                continue;
            }

            // Stop at the first refusal and put the run worktree back as it was. Pushing on — --skip, or resolving in
            // favour of one side — would land a fix the run's branch was never reviewed with, and the commits after
            // this one may well depend on it, so everything from here is reported stranded instead.
            await GitCommandLine.RunAsync("git", ["cherry-pick", "--abort"], runWorktreePath, cancellationToken);
            return new AutopilotStrayCommits(recovered, stray[index..], pick.Error);
        }

        return new AutopilotStrayCommits(recovered, [], null);
    }

    // Two spellings of one directory must not read as two worktrees: the host may hand back the path the provider
    // canonicalized rather than the one the run passed in.
    private static string _Normalized(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return path;
        }
    }
}
