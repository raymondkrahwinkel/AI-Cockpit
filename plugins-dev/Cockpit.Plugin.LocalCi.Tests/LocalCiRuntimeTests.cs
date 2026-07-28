using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalCiRuntimeTests
{
    private const string DockerNotRunning =
        "error during connect: Get \"http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.51/version\": open " +
        "//./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.";

    [Fact]
    public async Task Docker_ExecutableMissing_IsNotInstalled()
    {
        var status = await _StatusOf(new FakeCliRunner().Returns("docker", CliResult.NotStarted));

        Assert.Equal(DockerRuntimeState.NotInstalled, status.Docker.State);
        Assert.Contains("Docker was not found", status.Docker.Message);
        Assert.False(status.Docker.IsReady);
    }

    [Fact]
    public async Task Docker_CliRunsButEngineRefuses_IsEngineNotRunning()
    {
        var status = await _StatusOf(new FakeCliRunner()
            .Returns("docker", CliResult.Exited(1, string.Empty, DockerNotRunning)));

        Assert.Equal(DockerRuntimeState.EngineNotRunning, status.Docker.State);
        Assert.Contains("did not answer", status.Docker.Message);
    }

    [Fact]
    public async Task Docker_ProbeTimesOut_IsEngineNotRunning()
    {
        // A named pipe whose engine has gone does not answer "no" — it does not answer. That has to read as the
        // engine being down, not as a missing install.
        var status = await _StatusOf(new FakeCliRunner().Returns("docker", CliResult.Timeout));

        Assert.Equal(DockerRuntimeState.EngineNotRunning, status.Docker.State);
    }

    [Fact]
    public async Task Docker_LinuxContainers_IsUsableAndReady()
    {
        var status = await _StatusOf(new FakeCliRunner()
            .Returns("docker", CliResult.Exited(0, "linux 29.5.3\n", string.Empty)));

        Assert.Equal(DockerRuntimeState.Usable, status.Docker.State);
        Assert.True(status.Docker.IsReady);
        Assert.Equal("29.5.3", status.Docker.ServerVersion);
        Assert.Equal("Docker 29.5.3 is running with Linux containers.", status.Docker.Message);
    }

    [Fact]
    public async Task Docker_WindowsContainers_IsUsableButNotReady()
    {
        var status = await _StatusOf(new FakeCliRunner()
            .Returns("docker", CliResult.Exited(0, "windows 29.5.3\n", string.Empty)));

        Assert.Equal(DockerRuntimeState.Usable, status.Docker.State);
        Assert.False(status.Docker.IsReady);
        Assert.Contains("switch Docker to Linux containers", status.Docker.Message);
    }

    [Fact]
    public async Task Docker_EngineSaysNothingAboutContainers_IsNotReportedAsReady()
    {
        var status = await _StatusOf(new FakeCliRunner().Returns("docker", CliResult.Exited(0, "\n", string.Empty)));

        Assert.False(status.Docker.IsReady);
        Assert.Contains("did not say which kind of containers", status.Docker.Message);
    }

    [Fact]
    public async Task Act_OnPath_ReportsItsVersion()
    {
        var status = await _StatusOf(new FakeCliRunner()
            .Returns("act", CliResult.Exited(0, "act version 0.2.89\n", string.Empty)));

        Assert.True(status.Act.IsInstalled);
        Assert.Equal("0.2.89", status.Act.Version);
    }

    [Fact]
    public async Task Act_Missing_SaysHowToInstallIt()
    {
        var status = await _StatusOf(new FakeCliRunner().Returns("act", CliResult.NotStarted));

        Assert.False(status.Act.IsInstalled);
        Assert.Contains("winget install nektos.act", status.Act.Message);
    }

    [Fact]
    public async Task CanRunJobs_NeedsBothHalves()
    {
        var ready = await _StatusOf(new FakeCliRunner()
            .Returns("docker", CliResult.Exited(0, "linux 29.5.3", string.Empty))
            .Returns("act", CliResult.Exited(0, "act version 0.2.89", string.Empty)));
        Assert.True(ready.CanRunJobs);

        var withoutAct = await _StatusOf(new FakeCliRunner()
            .Returns("docker", CliResult.Exited(0, "linux 29.5.3", string.Empty)));
        Assert.False(withoutAct.CanRunJobs);
    }

    [Fact]
    public async Task EveryProbeGetsAFiniteTimeout()
    {
        // The acceptance criterion the settings dialog depends on: no probe may be allowed to wait forever.
        var runner = new FakeCliRunner();
        using var runtime = new LocalCiRuntime(runner);

        await runtime.GetStatusAsync();

        // Deliberately not asserted against LocalCiRuntime.ProbeTimeout: a test that compares the constant to itself
        // passes just as happily when the constant becomes Timeout.InfiniteTimeSpan, which is the one value that
        // breaks the criterion. Bound it instead — long enough for a cold engine, short enough not to be a hang.
        Assert.NotEmpty(runner.Calls);
        Assert.All(runner.Calls, call => Assert.InRange(call.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task StatusIsProbedOnceAndAgainAfterInvalidate()
    {
        var runner = new FakeCliRunner().Returns("docker", CliResult.Exited(0, "linux 29.5.3", string.Empty));
        using var runtime = new LocalCiRuntime(runner);

        await runtime.GetStatusAsync();
        await runtime.GetStatusAsync();
        var afterCache = runner.Calls.Count;

        runtime.Invalidate();
        await runtime.GetStatusAsync();

        Assert.Equal(2, afterCache);
        Assert.Equal(4, runner.Calls.Count);
    }

    private static async Task<LocalCiRuntimeStatus> _StatusOf(FakeCliRunner runner)
    {
        using var runtime = new LocalCiRuntime(runner);
        return await runtime.GetStatusAsync();
    }
}
