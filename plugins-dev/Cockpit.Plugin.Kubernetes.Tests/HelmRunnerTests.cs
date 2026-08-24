using System.Diagnostics;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The real process wrapper, against real processes (AC-1061 phase 5, AC 5 & 10) — mirrors
// Cockpit.Plugin.LocalCi.Tests/CliRunnerTests. stderr must never flip a zero exit code to a failure, and a command's
// locked-down `Environment` must reach the child process even when the ambient process environment disagrees.
public class HelmRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExecutableThatDoesNotExist_ComesBackAsNotStarted()
    {
        var command = new HelmCommand("helm-no-such-executable", ["version"], new Dictionary<string, string>());

        var result = await new HelmRunner().RunAsync(command, Generous);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExitZeroWithTextOnStderr_IsStillASuccess()
    {
        // Helm 4.2.2 writes deprecation warnings and NOTES.txt to stderr on a clean exit — a wrapper that treats
        // "stderr not empty" as failure breaks on the first one.
        var (fileName, arguments) = _ShellCommand("echo deprecation-warning 1>&2");

        var result = await new HelmRunner().RunAsync(new HelmCommand(fileName, arguments, new Dictionary<string, string>()), Generous);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("deprecation-warning", result.Stderr);
    }

    [Fact]
    public async Task LockedEnvironment_OverridesTheAmbientProcessEnvironment()
    {
        Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", "attacker-context");
        try
        {
            var (fileName, arguments) = _ShellCommand(_PrintEnvVarCommand("HELM_KUBECONTEXT"));
            var command = new HelmCommand(fileName, arguments, new Dictionary<string, string> { ["HELM_KUBECONTEXT"] = "eveworkbench-prod" });

            var result = await new HelmRunner().RunAsync(command, Generous);

            Assert.True(result.Succeeded);
            Assert.Contains("eveworkbench-prod", result.Stdout);
            Assert.DoesNotContain("attacker-context", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", null);
        }
    }

    [Fact]
    public async Task ProcessThatOutlastsItsDeadline_IsTimedOutAndKilled()
    {
        var (fileName, arguments) = _Sleeper(seconds: 30);
        var command = new HelmCommand(fileName, arguments, new Dictionary<string, string>());
        var stopwatch = Stopwatch.StartNew();

        var result = await new HelmRunner().RunAsync(command, TimeSpan.FromSeconds(1));

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

    // Something that stays alive and is on every machine. Windows uses ping rather than `timeout`, which refuses to
    // run at all when stdin is redirected — and the runner redirects everything.
    private static (string FileName, string[] Arguments) _Sleeper(int seconds) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
            : ("sh", ["-c", $"sleep {seconds}"]);
}
