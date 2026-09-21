namespace Cockpit.Plugin.Autopilot;

// The real `IAutopilotMergeExecutor` (AC-1338): drives the operator's own `git`/`gh` in the run worktree. The
// collection branch is only ever moved by `gh pr merge` or by a plain push git itself refuses when it is not a
// fast-forward — never `--force`; the one forced push is `--force-with-lease` on the run's own branch after rebase.
internal sealed class GitCliMergeExecutor : IAutopilotMergeExecutor
{
    // What survives of a build's output in the evidence — the tail is where dotnet prints the errors and the summary.
    private const int OutputTailLines = 30;

    public async Task<AutopilotMergeEvidence> DescribeAsync(string worktreePath, string collectionBranch, string buildCommand, TimeSpan buildTimeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new AutopilotMergeEvidence(string.Empty, string.Empty, null, string.Empty, "The run worktree no longer exists.");
        }

        // A fetch that fails is a measurement against a stale base, so it is reported, not shrugged off.
        var fetch = await GitCommandLine.RunAsync("git", ["fetch", "origin", collectionBranch], worktreePath, cancellationToken);
        var head = await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
        var diff = await GitCommandLine.RunAsync("git", ["diff", "--stat", $"origin/{collectionBranch}...HEAD"], worktreePath, cancellationToken);
        var (exitCode, tail) = await _BuildAsync(worktreePath, buildCommand, buildTimeout, cancellationToken);

        var error = !fetch.Ok ? $"fetch of origin/{collectionBranch} failed: {fetch.Error}" : !head.Ok ? head.Error : !diff.Ok ? diff.Error : null;
        return new AutopilotMergeEvidence(head.StdOut.Trim(), diff.StdOut.TrimEnd(), exitCode, tail, error);
    }

    public async Task<AutopilotMergeResult> MergeAsync(AutopilotMergeRequest request, CancellationToken cancellationToken = default)
    {
        var path = request.WorktreePath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return _Failed("The run worktree no longer exists.");
        }

        // Everything below acts on HEAD and names request.Branch; if a step left another branch checked out, the
        // rebase, the lease push and the fallback push would each touch a different ref. Refuse instead.
        var checkedOut = await GitCommandLine.RunAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], path, cancellationToken);
        if (!checkedOut.Ok || !string.Equals(checkedOut.StdOut.Trim(), request.Branch, StringComparison.Ordinal))
        {
            return _Failed($"The run worktree has “{checkedOut.StdOut.Trim()}” checked out, not the run branch “{request.Branch}”, so nothing was merged.");
        }

        // EpicWorkflow §6c: rebase right before the merge, not at the start — others' subs landed in the meantime.
        var fetch = await GitCommandLine.RunAsync("git", ["fetch", "origin", request.CollectionBranch], path, cancellationToken);
        if (!fetch.Ok)
        {
            return _Failed($"Could not fetch origin/{request.CollectionBranch}, so nothing was merged: {fetch.Error}");
        }

        var rebase = await GitCommandLine.RunAsync("git", ["rebase", $"origin/{request.CollectionBranch}"], path, cancellationToken);
        if (!rebase.Ok)
        {
            _ = await GitCommandLine.RunAsync("git", ["rebase", "--abort"], path, cancellationToken);
            return _Failed($"Rebase onto origin/{request.CollectionBranch} did not apply cleanly, so nothing was merged: {rebase.Error}");
        }

        // The run's own branch, so the PR shows the rebased commits; --force-with-lease refuses if someone else pushed to it.
        var push = await GitCommandLine.RunAsync("git", ["push", "--force-with-lease", "origin", request.Branch], path, cancellationToken);
        if (!push.Ok)
        {
            return _Failed($"Could not push the rebased branch “{request.Branch}”: {push.Error}");
        }

        var rebasedHead = (await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], path, cancellationToken)).StdOut.Trim();
        string route;
        if (request.PrUrl is { Length: > 0 } prUrl && (await GitCommandLine.RunAsync("gh", ["auth", "status"], path, cancellationToken)).Ok)
        {
            // The PR is the per-ticket trail, so it is merged as a PR — the same form the manual epic workflow uses —
            // and the proof is the PR's own state afterwards, not our merge call having returned zero.
            var merge = await GitCommandLine.RunAsync("gh", ["pr", "merge", prUrl, "--merge"], path, cancellationToken);
            var view = await GitCommandLine.RunAsync("gh", ["pr", "view", prUrl, "--json", "state,mergedAt"], path, cancellationToken);
            if (!view.StdOut.Contains("\"MERGED\"", StringComparison.Ordinal))
            {
                // The PR's own state decides, not the merge call's exit: a merge that timed out after landing
                // still reads MERGED here, and a merge that "succeeded" without landing does not.
                var why = merge.Ok ? view.StdOut.Trim() : merge.Error;
                return _Failed($"gh pr merge did not leave the pull request merged ({why}) — check {prUrl} before anything else touches {request.CollectionBranch}.");
            }

            route = $"merged the pull request {prUrl} with gh pr merge --merge";
        }
        else
        {
            // No gh (or no PR to merge): the plain push is the fast-forward — git refuses it when it is not one.
            var land = await GitCommandLine.RunAsync("git", ["push", "origin", $"HEAD:refs/heads/{request.CollectionBranch}"], path, cancellationToken);
            if (!land.Ok)
            {
                return _Failed($"The fast-forward push to {request.CollectionBranch} was refused, so nothing was merged: {land.Error}");
            }

            route = $"fast-forwarded {request.CollectionBranch} to {rebasedHead} with a plain push (gh unavailable or no pull request to merge)";
        }

        // §3 step 4: a merged branch left standing is litter. The merge above is already done, so a refusal here
        // does not undo it — but it is said in the route, because a branch that stays behind is meant to be seen.
        var delete = await GitCommandLine.RunAsync("git", ["push", "origin", "--delete", request.Branch], path, cancellationToken);
        route += delete.Ok
            ? $"; run branch {request.Branch} deleted from origin"
            : $"; run branch {request.Branch} NOT deleted from origin: {delete.Error}";

        // Bring the worktree to the collection branch's new tip (a fast-forward: the tip contains this branch's
        // work), so the build below runs on exactly the commit the branch now stands at.
        _ = await GitCommandLine.RunAsync("git", ["fetch", "origin", request.CollectionBranch], path, cancellationToken);
        var advance = await GitCommandLine.RunAsync("git", ["merge", "--ff-only", $"origin/{request.CollectionBranch}"], path, cancellationToken);
        if (!advance.Ok)
        {
            // No tip is reported: the worktree is not on the collection tip, and a sha here would be read as one.
            return new AutopilotMergeResult(true, route, null, null, string.Empty, $"Merged, but the worktree could not be moved to the collection tip for the build: {advance.Error}");
        }

        var tip = (await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], path, cancellationToken)).StdOut.Trim();
        var (exitCode, tail) = await _BuildAsync(path, request.BuildCommand, request.BuildTimeout, cancellationToken);
        return new AutopilotMergeResult(true, route, tip, exitCode, tail, null);
    }

    // Runs the configured build command (a plain whitespace-split command line) in the worktree. A null exit code
    // means the build produced no verdict — no command, a CLI that would not start, a timeout, a cancellation —
    // and the tail says which; only a process that ran to its end yields a code the ledger may record.
    private static async Task<(int? ExitCode, string Tail)> _BuildAsync(string worktreePath, string buildCommand, TimeSpan buildTimeout, CancellationToken cancellationToken)
    {
        var parts = buildCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return (null, "No build command is configured.");
        }

        var result = await GitCommandLine.RunAsync(parts[0], parts[1..], worktreePath, cancellationToken, buildTimeout);
        var output = string.IsNullOrWhiteSpace(result.StdOut) ? result.Error : result.StdOut;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join("\n", lines.TakeLast(OutputTailLines));
        if (result.Ok)
        {
            return (0, tail);
        }

        // GitCommandLine folds a silent non-zero exit into "exit N"; with stderr output the process still ran to
        // its end (a failed build, 1). "cancelled", "timed out" and a start failure never reached an exit code.
        if (result.Error.StartsWith("exit ", StringComparison.Ordinal) && int.TryParse(result.Error["exit ".Length..], out var code))
        {
            return (code, tail);
        }

        var ranToTheEnd = result.Error is not "cancelled" && !result.Error.EndsWith(" timed out", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(result.StdOut);
        return (ranToTheEnd ? 1 : null, string.IsNullOrWhiteSpace(tail) ? result.Error : tail);
    }

    private static AutopilotMergeResult _Failed(string error) => new(false, string.Empty, null, null, string.Empty, error);
}
