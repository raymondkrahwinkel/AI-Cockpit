using System.ComponentModel;
using System.Diagnostics;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Verify;

namespace Cockpit.Infrastructure.Verify;

/// <summary>
/// Runs a verify runner's registered command as a child process (AC-86). The command and its arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/> — never a shell string — so nothing the operator wrote is re-parsed,
/// and both output pipes are drained concurrently so a chatty child cannot deadlock by filling one while we wait on
/// the other. A run that outlives the timeout has its whole process tree killed; everything is fail-soft, so the
/// tool gets a <see cref="VerifyRunResult"/> to report rather than an exception to handle.
/// </summary>
internal sealed class VerifyCommandRunner : IVerifyCommandRunner, ISingletonService
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    public async Task<VerifyRunResult> RunAsync(VerifyRunner runner, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(runner.Command)
        {
            WorkingDirectory = runner.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in runner.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RunTimeout);

        var stopwatch = Stopwatch.StartNew();
        process.Start();

        // Read both pipes to completion up front: they close on the child's exit (or its kill), so awaiting them
        // captures the full output — including whatever a timed-out child managed to print before it was killed.
        var readStandardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var readStandardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            _KillTree(process);
        }

        stopwatch.Stop();
        var standardOutput = await _DrainAsync(readStandardOutput).ConfigureAwait(false);
        var standardError = await _DrainAsync(readStandardError).ConfigureAwait(false);
        // Reading ExitCode before the process has actually exited throws; the external-cancel path kills without a
        // synchronous exit, so treat "not exited" as the same failure sentinel a timeout uses.
        var exitCode = timedOut || !process.HasExited ? -1 : process.ExitCode;
        return new VerifyRunResult(exitCode, standardOutput, standardError, stopwatch.Elapsed, timedOut);
    }

    private static void _KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The child raced us to exit, or the OS refused the kill; either way the run is already over.
        }
    }

    // The pipes close on the child's exit or kill, which normally completes these reads at once. But a kill that the
    // OS refuses (a detached grandchild, a zombie) would leave them open forever and hang the whole verify call on the
    // already-granted consent — so give the drain a short grace and then give up rather than wait unbounded.
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(5);

    private static async Task<string> _DrainAsync(Task<string> read)
    {
        try
        {
            return await read.WaitAsync(ReadGrace).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            // A killed child can fault its own pipe read, and a kill the OS refused can leave it open past the grace;
            // either way the exit code and timing still describe the run.
            return string.Empty;
        }
    }
}
