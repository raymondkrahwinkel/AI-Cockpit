using System.Diagnostics;

namespace Cockpit.Plugin.SessionReview;

/// <summary>
/// Reads the uncommitted changes of a working directory (AC-50) via <c>git diff HEAD</c> plus the current branch via
/// <c>git rev-parse</c> — both run in the directory, with <c>ArgumentList</c> (no shell). Fails soft: no git, not a
/// repo, or no changes all yield an empty result rather than an error. Bounded by a per-call timeout.
/// </summary>
internal sealed class GitDiffReader
{
    /// <summary>The git arguments for the working-tree diff against the last commit. Internal so a test can assert them.</summary>
    internal static readonly string[] DiffArguments = ["diff", "HEAD"];

    public async Task<GitDiffResult> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return GitDiffResult.Unavailable;
        }

        var (branchExit, branchOut, _) = await _RunGitAsync(["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, cancellationToken).ConfigureAwait(false);
        if (branchExit != 0)
        {
            return GitDiffResult.Unavailable; // not a repo / no git
        }

        var (diffExit, diffOut, _) = await _RunGitAsync(DiffArguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (diffExit != 0)
        {
            return GitDiffResult.Unavailable;
        }

        return new GitDiffResult(true, branchOut.Trim(), diffOut);
    }

    /// <summary>Classifies a unified-diff line for colouring. Internal so a test can pin the mapping.</summary>
    internal static DiffLineKind ClassifyLine(string line)
    {
        if (line.StartsWith("diff --git", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("new file", StringComparison.Ordinal)
            || line.StartsWith("deleted file", StringComparison.Ordinal)
            || line.StartsWith("rename ", StringComparison.Ordinal)
            || line.StartsWith("similarity ", StringComparison.Ordinal))
        {
            return DiffLineKind.FileHeader;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.Hunk;
        }

        // A single leading + or - is an added/removed line; the +++/--- file headers were already caught above.
        return line.StartsWith('+') ? DiffLineKind.Added
            : line.StartsWith('-') ? DiffLineKind.Removed
            : DiffLineKind.Context;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> _RunGitAsync(string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
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
        catch (Exception)
        {
            return (-1, string.Empty, string.Empty); // git not installed — fail soft
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Drain both streams concurrently — reading one to end before the other can deadlock on a full pipe buffer,
            // and a large diff can fill it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
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
                // Best effort.
            }

            return (-1, string.Empty, string.Empty);
        }
    }
}
