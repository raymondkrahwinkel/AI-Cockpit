using Cockpit.App.Services;
using FluentAssertions;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// <see cref="AppRestartService"/> (#53): both steps are injected as plain delegates (the internal test
/// constructor), so these prove the exact launch-then-shutdown call sequence without spawning a real process
/// or exiting the test host — the real production delegates (<c>Process.Start</c> + <c>App.RequestQuit</c>)
/// are only exercised by hand.
/// </summary>
public class AppRestartServiceTests
{
    [Fact]
    public void Restart_LaunchesTheNewInstanceBeforeShuttingDownTheCurrentOne()
    {
        var callOrder = new List<string>();
        var service = new AppRestartService(
            launchNewInstance: () => callOrder.Add("launch"),
            shutDownCurrentInstance: () => callOrder.Add("shutdown"));

        service.Restart();

        callOrder.Should().Equal("launch", "shutdown");
    }

    [Fact]
    public void Restart_CallsBothStepsExactlyOnce()
    {
        var launchCalls = 0;
        var shutdownCalls = 0;
        var service = new AppRestartService(
            launchNewInstance: () => launchCalls++,
            shutDownCurrentInstance: () => shutdownCalls++);

        service.Restart();

        launchCalls.Should().Be(1);
        shutdownCalls.Should().Be(1);
    }

    [Fact]
    public void Restart_StillShutsDownWhenLaunchingTheNewInstanceIsANoOp()
    {
        // Mirrors the real launch step bailing out silently when Environment.ProcessPath is unavailable —
        // restart should still shut the current instance down rather than leaving the operator stuck with
        // nothing having happened after clicking "Restart now".
        var shutdownCalls = 0;
        var service = new AppRestartService(
            launchNewInstance: () => { },
            shutDownCurrentInstance: () => shutdownCalls++);

        service.Restart();

        shutdownCalls.Should().Be(1);
    }
}
