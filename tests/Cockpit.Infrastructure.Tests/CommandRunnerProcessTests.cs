using System.Diagnostics;

namespace Cockpit.Infrastructure.Tests;

public sealed class CommandRunnerProcessTests
{
    [PosixFact("Covers _KillTree's Linux-only procfs descendant walk (CommandRunnerProcess.cs); on Windows Kill(entireProcessTree: true) reaches the tree itself and _KillTree is covered by the runners' timeout tests.")]
    public async Task KillTree_KillsAStartedChildProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cockpit-command-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var childPidPath = Path.Combine(directory, "child-pid");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("sh")
                {
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add($"sleep 30 & echo $! > '{childPidPath}'; wait");
            process.Start();

            await _WaitForFileAsync(childPidPath);
            var childPid = int.Parse((await File.ReadAllTextAsync(childPidPath)).Trim());

            CommandRunnerProcess._KillTree(process);
            await process.WaitForExitAsync();

            Assert.False(Directory.Exists($"/proc/{childPid}"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DrainAsync_ReturnsOutputPastThePipeBuffer()
    {
        var (command, arguments) = PlatformCommands.WritesToStandardOutput(200000);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var output = await CommandRunnerProcess._DrainAsync(process.StandardOutput.ReadToEndAsync());
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(output.Length >= 200000);
    }

    private static async Task _WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline, "The started child did not report its PID.");
            await Task.Delay(10);
        }
    }
}
