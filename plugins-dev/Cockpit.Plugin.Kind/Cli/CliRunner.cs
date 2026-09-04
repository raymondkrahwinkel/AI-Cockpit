using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Plugin.Kind.Cli;

// Runs a CliCommand as a real process (AC-179): argv, both pipes drained while it runs, Environment layered on.
// Cli/ is a deliberate copy of the Kubernetes plugin's, not plugins-dev/_shared (AC-1079) — sharing the source
// would tie both plugins' version bumps to each other forever.
internal sealed class CliRunner : ICliRunner
{
    public async Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardInput = command.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // The executable is not there — the "not installed" case, which is the only way to learn it.
            return CliResult.NotStarted;
        }

        if (command.StandardInput is { } input)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
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
