using System.Diagnostics;
using System.Text;

namespace Cockpit.Plugin.Autopilot;

// Runs one `git`/`gh` command in a worktree and hands back what it printed — the single place this plugin shells
// out, shared by `GitCliPrPublisher` and `GitCliEvidenceSource` so the process plumbing isn't written twice.
// Bounded timeout, never throws: a missing CLI, non-zero exit, or timeout all come back as a not-ok result.
internal static class GitCommandLine
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    // `Ok`: The process ran and exited zero.
    // `StdOut`: Everything it wrote to stdout, whether or not it succeeded.
    // `Error`: Why it failed — its stderr, or the exit code when stderr was silent.
    internal sealed record CommandResult(bool Ok, string StdOut, string Error);

    // `timeout` overrides the two-minute default for the one caller that legitimately runs longer — the merge
    // gate's build of the whole solution (AC-1338); every git/gh call keeps the default.
    public static async Task<CommandResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
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

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? CommandTimeout);

            try
            {
                await process.WaitForExitAsync(deadline.Token);
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
