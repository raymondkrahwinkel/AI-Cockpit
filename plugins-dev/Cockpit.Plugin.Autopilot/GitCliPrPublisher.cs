namespace Cockpit.Plugin.Autopilot;

// The real `IAutopilotPrPublisher` (AC-216): drives the operator's own `git` and `gh` CLIs in the
// run worktree to push the run branch and open a pull request. Provider/host-neutral — it uses whatever remote and auth
// the operator's git/gh already have, and hard-codes no credentials. Every process runs in the worktree directory with a
// bounded timeout (see `GitCommandLine`); every failure is swallowed into a result (probe → false, publish →
// an error string) so a git/gh fault never crashes a run. It composes no "Co-Authored-By" trailer and no AI/agent
// mention in any commit it makes.
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

        var pr = await GitCommandLine.RunAsync(
            "gh",
            ["pr", "create", "--head", request.Branch, "--title", request.Title, "--body", request.Body],
            path,
            cancellationToken);

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
}
