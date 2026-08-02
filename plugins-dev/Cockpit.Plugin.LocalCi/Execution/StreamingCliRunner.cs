using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Cockpit.Plugin.LocalCi.Execution;

// `IStreamingCliRunner` backed by a real process. Same shape as `Runtime.CliRunner` —
// argv via `ProcessStartInfo.ArgumentList`, both pipes drained while the process runs — but the lines
// go to the caller as they arrive instead of into a buffer nobody sees until the end.
internal sealed class StreamingCliRunner : IStreamingCliRunner
{
    public async Task<StreamedRun> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, received) => _Forward(received, onLine);
        process.ErrorDataReceived += (_, received) => _Forward(received, onLine);

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return StreamedRun.NotStarted;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing is ever typed at this process. Closing its input means a tool that decides to ask a question
        // fails on the spot instead of waiting forever on a pipe nobody is writing to — which is precisely what
        // act does the first time it runs on a machine, when it offers to pick a runner image.
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _KillQuietly(process);
            throw;
        }

        // WaitForExitAsync returns once the process is gone, which is not the same moment its pipes are drained;
        // without this the last lines of a failing job — the ones worth reading — can be lost.
        process.WaitForExit();

        return new StreamedRun(Started: true, process.ExitCode);
    }

    private static void _Forward(DataReceivedEventArgs received, Action<string> onLine)
    {
        if (received.Data is { } line)
        {
            onLine(line);
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
            // Best-effort: it may have exited between the cancellation and this call.
        }
    }
}
