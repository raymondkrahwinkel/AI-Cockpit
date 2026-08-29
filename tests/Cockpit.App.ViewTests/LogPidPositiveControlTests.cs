using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1147's positive control: two real, separately started processes (never looked up by name — their
/// pids come straight from <see cref="Process.Start(ProcessStartInfo)"/>) write to the same log file.
/// Every line reaching that file carries its writer's pid, and both writers are represented.
/// It deliberately does not count lines: <c>FileLoggerProvider._AppendWithRetry</c> drops a line after five attempts (AC-1216).
/// </summary>
public class LogPidPositiveControlTests
{
    [Fact]
    public void TwoProcesses_WritingTheSameLog_AreDistinguishableByPid()
    {
        var probeDll = _LocateProbeOutput();
        Assert.NotNull(probeDll);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var logPath = Path.Combine(dir.FullName, "cockpit.log");

            using var processA = _StartProbe(probeDll!, logPath, "A");
            using var processB = _StartProbe(probeDll!, logPath, "B");

            Assert.True(processA.WaitForExit(30_000), "probe A did not exit in time");
            Assert.True(processB.WaitForExit(30_000), "probe B did not exit in time");
            Assert.Equal(0, processA.ExitCode);
            Assert.Equal(0, processB.ExitCode);

            var pidA = processA.Id;
            var pidB = processB.Id;
            Assert.NotEqual(pidA, pidB);

            var lines = File.ReadAllLines(logPath);
            var fromA = lines.Where(line => line.Contains("probe A line", StringComparison.Ordinal)).ToList();
            var fromB = lines.Where(line => line.Contains("probe B line", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(fromA);
            Assert.NotEmpty(fromB);

            // One process, one pid: every line a process wrote carries only that process's own pid.
            Assert.All(fromA, line => Assert.Equal(pidA, _ExtractPid(line)));
            Assert.All(fromB, line => Assert.Equal(pidB, _ExtractPid(line)));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static int _ExtractPid(string line)
    {
        var match = Regex.Match(line, @"\[pid (\d+)\]");
        Assert.True(match.Success, $"line carries no pid: {line}");
        return int.Parse(match.Groups[1].Value);
    }

    private static Process _StartProbe(string probeDll, string logPath, string label)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(probeDll);
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add(label);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet exec did not start a process");
    }

    // Walks up from the test output to the repo root and finds the probe's build output, the same technique
    // DiagramPluginLoadTests uses for its build-only ProjectReference.
    private static string? _LocateProbeOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "tests", "Cockpit.LogPidProbe", "bin");
            if (Directory.Exists(candidateRoot))
            {
                return Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.LogPidProbe.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            directory = directory.Parent;
        }

        return null;
    }
}
