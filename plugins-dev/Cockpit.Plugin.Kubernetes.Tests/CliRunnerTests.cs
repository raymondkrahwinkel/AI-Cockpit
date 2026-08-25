using System.Diagnostics;
using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The generic process wrapper both helm and kind commands run through (AC-179) — extracted from HelmRunner so a
// second CLI never needs its own 89-line copy. Same cases as HelmRunnerTests, generalized off HelmCommand/HelmResult.
public class CliRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExecutableThatDoesNotExist_ComesBackAsNotStarted()
    {
        var command = new CliCommand("no-such-executable-anywhere", ["version"], new Dictionary<string, string>());

        var result = await new CliRunner().RunAsync(command, Generous);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExitZeroWithTextOnStderr_IsStillASuccess()
    {
        var (fileName, arguments) = _ShellCommand("echo deprecation-warning 1>&2");

        var result = await new CliRunner().RunAsync(new CliCommand(fileName, arguments, new Dictionary<string, string>()), Generous);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("deprecation-warning", result.Stderr);
    }

    [Fact]
    public async Task LockedEnvironment_OverridesTheAmbientProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("CLIRUNNER_TEST_VAR", "attacker-context");
        try
        {
            var (fileName, arguments) = _ShellCommand(_PrintEnvVarCommand("CLIRUNNER_TEST_VAR"));
            var command = new CliCommand(fileName, arguments, new Dictionary<string, string> { ["CLIRUNNER_TEST_VAR"] = "locked-value" });

            var result = await new CliRunner().RunAsync(command, Generous);

            Assert.True(result.Succeeded);
            Assert.Contains("locked-value", result.Stdout);
            Assert.DoesNotContain("attacker-context", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLIRUNNER_TEST_VAR", null);
        }
    }

    [Fact]
    public async Task ProcessThatOutlastsItsDeadline_IsTimedOutAndKilled()
    {
        var (fileName, arguments) = _Sleeper(seconds: 30);
        var command = new CliCommand(fileName, arguments, new Dictionary<string, string>());
        var stopwatch = Stopwatch.StartNew();

        var result = await new CliRunner().RunAsync(command, TimeSpan.FromSeconds(1));

        stopwatch.Stop();
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"waited {stopwatch.Elapsed} for a 1-second deadline");
    }

    private static (string FileName, string[] Arguments) _ShellCommand(string script) =>
        OperatingSystem.IsWindows() ? ("cmd", ["/c", script]) : ("sh", ["-c", script]);

    private static string _PrintEnvVarCommand(string name) =>
        OperatingSystem.IsWindows() ? $"echo %{name}%" : $"echo ${name}";

    private static (string FileName, string[] Arguments) _Sleeper(int seconds) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
            : ("sh", ["-c", $"sleep {seconds}"]);
}
