using System.Diagnostics;

namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// Runs git for the worktree manager (AC-85). A thin wrapper over the git CLI rather than a library binding: the
/// same binary the operator's own shell uses, so git's refusals — "a branch named 'x' already exists", "contains
/// modified or untracked files" — are the ones the cockpit surfaces, and there is no second copy of git's rules
/// to keep in step with it.
/// </summary>
internal static class GitCli
{
    // Not a network timeout — a worktree add copies a full checkout, which is slow but bounded. It is a hang guard:
    // a git waiting on a credential prompt or a wedged index lock would otherwise stall session start forever. The
    // kill is by tree because git shells out (a credential helper, a submodule clone), and killing only the parent
    // leaves those running.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public static async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
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

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not run 'git' — is it installed and on PATH? ({exception.Message})", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        // Both streams are drained concurrently. A git that fills the stderr pipe while nothing reads it blocks on
        // the write and never exits, so reading stdout to the end before touching stderr can deadlock on a chatty
        // command. Starting both reads first and waiting on exit after gates correctly on end-of-stream.
        var readStandardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var readStandardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var standardOutput = await readStandardOutput.ConfigureAwait(false);
            var standardError = await readStandardError.ConfigureAwait(false);

            return new GitResult(process.ExitCode, standardOutput, standardError);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _Kill(process);
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} did not finish within {Timeout.TotalSeconds:F0}s and was stopped.");
        }
        catch (OperationCanceledException)
        {
            _Kill(process);
            throw;
        }
    }

    /// <summary>Runs git and returns its trimmed output, throwing what git said on a non-zero exit.</summary>
    public static async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // git says why in words a person can act on ("a branch named 'x' already exists"); that is what the
            // caller sees, not "git exited with 128".
            var said = result.StandardError.Trim();
            throw new InvalidOperationException(said.Length > 0 ? said : $"git exited with {result.ExitCode}.");
        }

        return result.StandardOutput.Trim();
    }

    private static void _Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or unsignalable. The caller is about to see the cancellation either way; a failed kill
            // is not worth masking that with.
        }
    }
}
