using System.Diagnostics;

namespace Cockpit.Plugin.Docker.Compose;

/// <summary>
/// <see cref="IDockerCli"/> backed by the real <c>docker</c> CLI via <see cref="Process"/>. Uses
/// <see cref="ProcessStartInfo.ArgumentList"/> (argv, no shell), so nothing agent-supplied is ever interpreted by a
/// shell. Mirrors <see cref="ComposeCli"/>, including the kill-the-tree-on-cancel handling.
/// </summary>
internal sealed class DockerCli : IDockerCli
{
    public async Task<DockerCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
            return new DockerCliResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort kill.
            }

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
