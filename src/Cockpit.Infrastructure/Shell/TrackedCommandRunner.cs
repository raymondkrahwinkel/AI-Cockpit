using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.Infrastructure.Shell;

// AC-1094: same execution shape as ShellCommandRunner (AC-1066), but the process moves itself into its own cgroup
// before running anything (`SelfMoveIntoCgroupThenExec`), and that whole cgroup ends unconditionally when the run
// ends — reaching a node `Kill(entireProcessTree: true)` cannot once reparenting to pid 1 takes it out of the tree.
internal sealed class TrackedCommandRunner(ILogger<TrackedCommandRunner> logger) : ITrackedCommandRunner, ISingletonService
{
    // Moves the process into the cgroup at `$1` before anything else, then `exec`s the real command — same pid,
    // so it inherits membership instead of racing to acquire it. `shift` drops the procs path so `"$@"` is exactly
    // command + arguments, none of it ever re-parsed by the shell.
    private const string SelfMoveIntoCgroupThenExec = "echo $$ > \"$1\"; shift; exec \"$@\"";

    public async Task<TrackedRunResult> RunAsync(
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string runId,
        CancellationToken cancellationToken = default)
    {
        using var group = RunCgroup.Create(runId, logger);

        var startInfo = group.ProcsPath is { } procsPath
            ? _WrappedForCgroup(procsPath, command, arguments)
            : new ProcessStartInfo(command);

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.CreateNoWindow = true;

        if (group.ProcsPath is null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        process.Start();

        var readStandardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var readStandardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
        }

        // Unconditional, not just on timeout: a run that finished on its own can still leave a reused build server
        // running (AC-1094 criterion 8) — that is exactly the case a tree walk never reached either.
        group.KillAll();
        CommandRunnerProcess._KillTree(process);

        stopwatch.Stop();
        var standardOutput = await CommandRunnerProcess._DrainAsync(readStandardOutput).ConfigureAwait(false);
        var standardError = await CommandRunnerProcess._DrainAsync(readStandardError).ConfigureAwait(false);
        var exitCode = timedOut || !process.HasExited ? -1 : process.ExitCode;
        return new TrackedRunResult(exitCode, standardOutput, standardError, stopwatch.Elapsed, timedOut);
    }

    // `sh -c SCRIPT NAME procsPath command arg...`: inside the script `$0` is NAME (unused, just a readable argv[0]
    // for `ps`), `$1` is procsPath, and `shift` then leaves `"$@"` as exactly command + arguments — never
    // interpolated into the script string, so nothing in them is re-parsed as shell syntax.
    private static ProcessStartInfo _WrappedForCgroup(string procsPath, string command, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(SelfMoveIntoCgroupThenExec);
        startInfo.ArgumentList.Add("cockpit-run");
        startInfo.ArgumentList.Add(procsPath);
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    // Best-effort fallback for when the cgroup could not contain the run (non-Linux, no delegation) — the same
    // ppid-tree kill ShellCommandRunner uses, with the same known gap this class exists to close.
}
