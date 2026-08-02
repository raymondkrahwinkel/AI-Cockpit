using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Cockpit.Plugin.SessionReview;

// Reads the uncommitted changes of a working directory (AC-50) via `git diff HEAD`, plus the branch and repo
// root via `git rev-parse`, plus the untracked files via `git status` — all run in the directory, with
// `ArgumentList` (no shell). Fails soft: no git, not a repo, or no changes all yield an empty result rather
// than an error. Bounded by a per-call timeout.
// A file that has never been staged does not appear in `git diff HEAD` at all, so the panel's promise to show
// "what this session changed before it lands" used to skip exactly the files a session most often adds. Each one is
// read here and appended to the diff as the all-added block git itself would have written, which keeps one parsing
// path for the panel and leaves the copied text a valid diff.
internal sealed class GitDiffReader
{
    // A file this large is not something anyone reviews line by line; it is listed, not drawn.
    private const int MaxUntrackedBytes = 1024 * 1024;

    // The git arguments for the working-tree diff against the last commit. Internal so a test can assert them.
    // `core.quotePath=false` keeps non-ASCII paths readable instead of octal-escaped, and `--no-ext-diff`
    // stops a repository's own diff driver from replacing the unified output this panel has to parse.
    internal static readonly string[] DiffArguments = ["-c", "core.quotePath=false", "diff", "--no-ext-diff", "HEAD"];

    // The git arguments that list untracked files. Internal so a test can assert them.
    internal static readonly string[] StatusArguments = ["-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=all"];

    public async Task<GitDiffResult> ReadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return GitDiffResult.Unavailable;
        }

        // One call for both: the branch to name in the header, and the root the porcelain paths are relative to.
        var (revExit, revOut, _) = await _RunGitAsync(["rev-parse", "--abbrev-ref", "HEAD", "--show-toplevel"], workingDirectory, cancellationToken).ConfigureAwait(false);
        if (revExit != 0)
        {
            return GitDiffResult.Unavailable; // not a repo / no git
        }

        var revLines = revOut.Replace("\r\n", "\n").Split('\n');
        var branch = revLines.Length > 0 ? revLines[0].Trim() : string.Empty;
        var root = revLines.Length > 1 ? revLines[1].Trim() : workingDirectory;

        var (diffExit, diffOut, _) = await _RunGitAsync(DiffArguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (diffExit != 0)
        {
            return GitDiffResult.Unavailable;
        }

        var (statusExit, statusOut, _) = await _RunGitAsync(StatusArguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        var untracked = statusExit == 0 ? _UntrackedBlocks(statusOut, root) : string.Empty;

        return new GitDiffResult(true, branch, diffOut + untracked);
    }

    // The paths `git status --porcelain` reports as untracked. Porcelain output is always relative to the
    // repository root, whichever directory git ran in. Internal so a test can pin the parsing.
    internal static IReadOnlyList<string> UntrackedPaths(string statusOutput) =>
        [.. statusOutput.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim().Trim('"'))
            .Where(path => path.Length > 0)];

    // The diff block git would have written for a new file: every line added, against `/dev/null`. Internal so
    // a test can pin the shape without touching a disk.
    internal static string UntrackedBlock(string path, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1]; // the newline that ends the last line is not a line of its own
        }

        var block = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"diff --git a/{path} b/{path}\n")
            .Append("new file mode 100644\n")
            .Append("--- /dev/null\n")
            .Append(CultureInfo.InvariantCulture, $"+++ b/{path}\n")
            .Append(CultureInfo.InvariantCulture, $"@@ -0,0 +1,{lines.Length} @@\n");

        foreach (var line in lines)
        {
            block.Append('+').Append(line).Append('\n');
        }

        return block.ToString();
    }

    // The block for a file that is new but will not be drawn — binary, unreadable, or simply too large.
    internal static string UntrackedBinaryBlock(string path) =>
        $"diff --git a/{path} b/{path}\nnew file mode 100644\nBinary files /dev/null and b/{path} differ\n";

    private static string _UntrackedBlocks(string statusOutput, string root)
    {
        var blocks = new StringBuilder();
        foreach (var path in UntrackedPaths(statusOutput))
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                // A directory can appear here when git collapses one; --untracked-files=all should prevent it, but a
                // race between the status call and this read can still hand us one.
                if (!File.Exists(full) || new FileInfo(full).Length > MaxUntrackedBytes)
                {
                    blocks.Append(UntrackedBinaryBlock(path));
                    continue;
                }

                var bytes = File.ReadAllBytes(full);
                blocks.Append(Array.IndexOf(bytes, (byte)0) >= 0
                    ? UntrackedBinaryBlock(path)
                    : UntrackedBlock(path, new UTF8Encoding(false).GetString(bytes)));
            }
            catch (Exception)
            {
                blocks.Append(UntrackedBinaryBlock(path)); // locked, gone, or denied — list it rather than lose it
            }
        }

        return blocks.ToString();
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
