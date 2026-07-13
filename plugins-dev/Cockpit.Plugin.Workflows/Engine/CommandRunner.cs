using System.Diagnostics;
using System.Text;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Runs a shell command and hands its output on. A command that fails is a step that fails — an exit code nobody
/// looks at is how a flow ends up reporting green while nothing happened.
/// </summary>
internal sealed class CommandRunner : IStepRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public string TypeId => "cockpit.command";

    public async Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken)
    {
        var command = node.Parameters.GetValueOrDefault("Command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("This step has no command to run. Open it and write one.");
        }

        // A command can use what the step before it produced: "grep -c error {output}", say.
        command = StepData.Resolve(command, input).Text;

        var workingDirectory = node.Parameters.GetValueOrDefault("Working directory");
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException($"There is no directory '{workingDirectory}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
        };

        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The command could not be started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        if (process.ExitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"The command exited with {process.ExitCode}: {reason.Trim()}");
        }

        // What it printed becomes the data the next step gets — which is the whole reason a command step is worth
        // having rather than just being a way to run something.
        var output = new StringBuilder(stdout).Append(stderr).ToString().Trim();
        return new StepOutcome([WorkflowItem.Of("output", output)], output.Length == 0 ? "(no output)" : output);
    }
}
