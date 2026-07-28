using System.Diagnostics;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

/// <summary>
/// The real process wrapper, against real processes. The fakes elsewhere prove what the detection does with each
/// answer; these prove the wrapper produces those answers rather than an exception or a wait with no end — which is
/// the whole reason the three Docker states can be told apart at all.
/// </summary>
public class CliRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExecutableThatDoesNotExist_ComesBackAsNotStarted()
    {
        var result = await new CliRunner().RunAsync("local-ci-no-such-executable", ["--version"], Generous);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExecutableThatExists_ReportsItsOutputAndExitCode()
    {
        var result = await new CliRunner().RunAsync("dotnet", ["--version"], Generous);

        Assert.True(result.Started);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ProcessThatOutlastsItsDeadline_IsTimedOutAndKilled()
    {
        var (fileName, arguments) = _Sleeper(seconds: 30);
        var stopwatch = Stopwatch.StartNew();

        var result = await new CliRunner().RunAsync(fileName, arguments, TimeSpan.FromSeconds(1));

        stopwatch.Stop();
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"waited {stopwatch.Elapsed} for a 1-second deadline");
    }

    [Fact]
    public async Task CancellingTheCaller_ThrowsRatherThanReportingATimeout()
    {
        // A caller that gave up is not the same as a tool that hung, and only one of the two is a detection result.
        var (fileName, arguments) = _Sleeper(seconds: 30);
        using var cancellation = new CancellationTokenSource();
        var running = new CliRunner().RunAsync(fileName, arguments, Generous, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    /// <summary>
    /// Something that stays alive and is on every machine. Windows uses ping rather than <c>timeout</c>, which
    /// refuses to run at all when stdin is redirected — and the runner redirects everything.
    /// </summary>
    private static (string FileName, string[] Arguments) _Sleeper(int seconds) =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
            : ("sh", ["-c", $"sleep {seconds}"]);
}
