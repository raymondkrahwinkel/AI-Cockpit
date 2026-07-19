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

        // core.longpaths so a worktree checkout — the app state root plus a deep repository path (a nested Angular
        // component tree is enough) — does not trip Windows' 260-character path limit with "Filename too long".
        // Git for Windows then writes through the \\?\ extended-length API regardless of the OS registry switch.
        // A harmless no-op off Windows and for git commands that create no files. Set per-invocation so it never
        // depends on the operator's global config.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.longpaths=true");

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
            // caller sees, not "git exited with 128" — but with the checkout progress ("Updating files: 42% …",
            // written to stderr and carriage-returned over itself) stripped out first, so a failed worktree add
            // surfaces the actual error instead of a hundred percent-lines.
            var said = StripProgress(result.StandardError);
            throw new InvalidOperationException(said.Length > 0 ? said : $"git exited with {result.ExitCode}.");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Drops git's transfer/checkout progress chatter ("Updating files:", "Receiving objects:", …) from
    /// <paramref name="standardError"/>, so an error surfaced to the operator is the diagnosis, not the progress bar
    /// that ran up to it. Splits on both line terminators because git overwrites progress in place with a carriage
    /// return. Falls back to the raw text if stripping would leave nothing, so a git that reports only via progress
    /// is never reduced to an empty message.
    /// </summary>
    internal static string StripProgress(string standardError)
    {
        var kept = standardError
            .Split('\r', '\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !_IsProgressLine(line))
            .ToList();

        return kept.Count > 0 ? string.Join(Environment.NewLine, kept) : standardError.Trim();
    }

    private static bool _IsProgressLine(string line) =>
        line.StartsWith("Updating files:", StringComparison.Ordinal)
        || line.StartsWith("Enumerating objects:", StringComparison.Ordinal)
        || line.StartsWith("Counting objects:", StringComparison.Ordinal)
        || line.StartsWith("Compressing objects:", StringComparison.Ordinal)
        || line.StartsWith("Receiving objects:", StringComparison.Ordinal)
        || line.StartsWith("Resolving deltas:", StringComparison.Ordinal);

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
