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
        await process.WaitForExitAsync(cancellationToken);

        return new ComposeResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
