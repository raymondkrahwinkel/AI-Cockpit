using System.Diagnostics;
using System.Text;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotPrPublisher"/> (AC-216): drives the operator's own <c>git</c> and <c>gh</c> CLIs in the
/// run worktree to push the run branch and open a pull request. Provider/host-neutral — it uses whatever remote and auth
/// the operator's git/gh already have, and hard-codes no credentials. Every process runs in the worktree directory with a
/// bounded timeout; every failure is swallowed into a result (probe → false, publish → an error string) so a git/gh fault
/// never crashes a run. It composes no "Co-Authored-By" trailer and no AI/agent mention in any commit it makes.
/// </summary>
internal sealed class GitCliPrPublisher : IAutopilotPrPublisher
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new AutopilotPrProbe(false, false, false);
        }

        var isGitRun = (await _RunAsync("git", ["rev-parse", "--is-inside-work-tree"], worktreePath, cancellationToken)).Ok;
        if (!isGitRun)
        {
            return new AutopilotPrProbe(false, false, false);
        }

        var remotes = await _RunAsync("git", ["remote"], worktreePath, cancellationToken);
        var hasRemote = remotes.Ok && !string.IsNullOrWhiteSpace(remotes.StdOut);

        // gh is usable only when it is installed AND authenticated — an installed-but-logged-out gh cannot open a PR, so
        // treat it as unavailable and fall back to push-only rather than failing at "gh pr create".
        var ghAvailable = (await _RunAsync("gh", ["auth", "status"], worktreePath, cancellationToken)).Ok;

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
        // uncommitted so the push carries the whole deliverable. A clean tree means "nothing to commit" — not an error.
        // The message carries no Co-Authored-By trailer and no AI/agent mention (a hard project rule).
        await _RunAsync("git", ["add", "-A"], path, cancellationToken);
        var status = await _RunAsync("git", ["status", "--porcelain"], path, cancellationToken);
        if (status.Ok && !string.IsNullOrWhiteSpace(status.StdOut))
        {
            var commit = await _RunAsync("git", ["commit", "-m", request.Title], path, cancellationToken);
            if (!commit.Ok)
            {
                return new AutopilotPrPublishResult(false, null, $"Could not commit the remaining work: {commit.Error}");
            }
        }

        var push = await _RunAsync("git", ["push", "-u", "origin", request.Branch], path, cancellationToken);
        if (!push.Ok)
        {
            return new AutopilotPrPublishResult(false, null, $"Could not push branch “{request.Branch}”: {push.Error}");
        }

        if (!createPullRequest)
        {
            return new AutopilotPrPublishResult(true, null, null);
        }

        var pr = await _RunAsync(
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

    private sealed record CommandResult(bool Ok, string StdOut, string Error);

    private static async Task<CommandResult> _RunAsync(string file, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            if (!process.Start())
            {
                return new CommandResult(false, string.Empty, $"could not start {file}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new CommandResult(false, stdout.ToString(), cancellationToken.IsCancellationRequested ? "cancelled" : $"{file} timed out");
            }

            var error = stderr.ToString().Trim();
            return new CommandResult(process.ExitCode == 0, stdout.ToString(), string.IsNullOrEmpty(error) ? $"exit {process.ExitCode}" : error);
        }
        catch (Exception ex)
        {
            // A missing CLI (git/gh not installed) throws Win32Exception here — degrade to "not ok", never crash the run.
            return new CommandResult(false, string.Empty, ex.Message);
        }
    }
}
