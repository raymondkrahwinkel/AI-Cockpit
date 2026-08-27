using Cockpit.App.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

public class RenderClockRecoveryDecisionTests
{
    // The exception Avalonia actually throws from MediaContext.FireInvokeOnRenderCallbacks.
    private static InvalidOperationException _CutOff() => new("Infinite layout loop detected");

    [Fact]
    public void TheFirstCutOffSinceStartup_IsAnswered()
    {
        // The elapsed reading Program's stopwatch really produces: it starts before the handler is wired up, so it
        // is never zero by the time a cut-off arrives.
        var uptime = TimeSpan.FromMilliseconds(1);

        Assert.True(RenderClockRecovery.ShouldRecover(_CutOff(), uptime - RenderClockRecovery.NeverRecovered));
    }

    [Fact]
    public void ASecondCutOffWithinTheInterval_IsNotAnswered()
    {
        var sinceLast = RenderClockRecovery.MinimumInterval - TimeSpan.FromMilliseconds(1);

        Assert.False(RenderClockRecovery.ShouldRecover(_CutOff(), sinceLast));
    }

    [Fact]
    public void AnUnrelatedException_IsNotAnswered()
        => Assert.False(RenderClockRecovery.ShouldRecover(
            new InvalidOperationException("something else entirely"), RenderClockRecovery.MinimumInterval));
}
