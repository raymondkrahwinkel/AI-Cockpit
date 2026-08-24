using System.ComponentModel;
using System.Diagnostics;

namespace Cockpit.Plugin.Kubernetes.Helm;

// `IHelmRunner` backed by a real process. Same shape as LocalCi's `Runtime/CliRunner` — argv, both pipes read while
// the process runs so a chatty helm command cannot fill a pipe and deadlock — plus `HelmCommand.Environment`
// layered onto the inherited environment so its locked-down vars win without wiping PATH/HOME.
internal sealed class HelmRunner : IHelmRunner
{
    public async Task<HelmResult> RunAsync(HelmCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
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
            // The executable is not there — the "helm is not installed" case, which is the only way to learn it.
            return HelmResult.NotStarted;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return HelmResult.Exited(process.ExitCode, await stdout, await stderr);
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
            return HelmResult.Timeout;
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
