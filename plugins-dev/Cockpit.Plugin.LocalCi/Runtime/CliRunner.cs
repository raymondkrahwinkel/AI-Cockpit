using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Plugin.LocalCi.Runtime;

/// <summary>
/// <see cref="ICliRunner"/> backed by a real process. Mirrors the Docker plugin's CLI wrapper — argv via
/// <see cref="ProcessStartInfo.ArgumentList"/> so nothing is interpreted by a shell, output read while the process
/// runs so a chatty tool cannot fill a pipe and deadlock — and adds the timeout this plugin needs: probing Docker on
/// Windows means talking to a named pipe, and a pipe whose engine is gone answers nothing at all rather than
/// answering an error. Without a deadline that probe never returns.
/// </summary>
internal sealed class CliRunner : ICliRunner
{
    public async Task<CliResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
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
        catch (Win32Exception)
        {
            // The executable is not there. This is the "Docker is not installed" branch, and the only way the
            // platform reports it — there is no probe that answers the question without trying.
            return CliResult.NotStarted;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return CliResult.Exited(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            _KillQuietly(process);
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (Exception)
            {
                // Draining after a kill can fault; the output is worthless either way.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CliResult.Timeout;
        }
    }

    private static void _KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best-effort: it may have exited between the timeout firing and this call.
        }
    }
}
