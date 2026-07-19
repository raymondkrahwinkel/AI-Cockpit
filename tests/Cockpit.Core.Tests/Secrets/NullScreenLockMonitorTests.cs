using Cockpit.Core.Secrets;
using FluentAssertions;

namespace Cockpit.Core.Tests.Secrets;

/// <summary>
/// The fail-safe monitor (AC-5): the object a platform with no supported lock facility gets. It has to be a working
/// object that never fires and never throws, so the coordinator above it simply never locks — the feature is absent,
/// not broken.
/// </summary>
public class NullScreenLockMonitorTests
{
    [Fact]
    public async Task ItNeverFires_AndStartAndDisposeAreSafe()
    {
        using var monitor = new NullScreenLockMonitor();
        var fired = false;
        monitor.Locked += (_, _) => fired = true;
        monitor.Unlocked += (_, _) => fired = true;

        await monitor.StartAsync();
        monitor.Dispose();

        fired.Should().BeFalse("the null monitor observes nothing, so it can raise nothing");
    }
}
