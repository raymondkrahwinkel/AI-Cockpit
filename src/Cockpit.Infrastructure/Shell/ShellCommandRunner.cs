using System.Diagnostics;
using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.Infrastructure.Shell;

// Runs one shell command as a child process (AC-1066) — same execution shape as VerifyCommandRunner (AC-86):
// ProcessStartInfo.ArgumentList, never a shell string, both pipes drained concurrently, Kill(entireProcessTree:
// true) on a time-out. Fail-soft: the caller gets a ShellCommandResult to report, never an exception.
internal sealed class ShellCommandRunner : IShellCommandRunner, ISingletonService
{
    public async Task<ShellCommandResult> RunAsync(
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        process.Start();

        // Read both pipes to completion up front: they close on the child's exit (or its kill), so awaiting them
        // captures the full output — including whatever a timed-out child managed to print before it was killed.
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
            CommandRunnerProcess._KillTree(process);
        }

        stopwatch.Stop();
        var standardOutput = await CommandRunnerProcess._DrainAsync(readStandardOutput).ConfigureAwait(false);
        var standardError = await CommandRunnerProcess._DrainAsync(readStandardError).ConfigureAwait(false);
        // Reading ExitCode before the process has actually exited throws; the external-cancel path kills without a
        // synchronous exit, so treat "not exited" as the same failure sentinel a timeout uses.
        var exitCode = timedOut || !process.HasExited ? -1 : process.ExitCode;
        return new ShellCommandResult(exitCode, standardOutput, standardError, stopwatch.Elapsed, timedOut);
    }

}
