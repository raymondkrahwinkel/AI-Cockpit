using System.Diagnostics;

namespace Cockpit.Plugin.Docker.Compose;

/// <summary>
/// <see cref="IComposeCli"/> backed by the real <c>docker compose</c> CLI via <see cref="Process"/>. Uses
/// <see cref="ProcessStartInfo.ArgumentList"/> (argv, no shell), so nothing agent-supplied is ever interpreted by a
/// shell.
/// </summary>
internal sealed class ComposeCli : IComposeCli
{
    public async Task<ComposeResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("compose");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ComposeResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            // Don't leave the compose child (and its subprocesses) running when the caller cancels.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort kill.
            }

            // Observe the read tasks so they don't fault unobserved after the kill.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (Exception)
            {
                // Draining after a kill can fault; ignore.
            }

            throw;
        }
    }
}
