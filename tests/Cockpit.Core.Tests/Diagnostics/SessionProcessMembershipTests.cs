using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// AC-1096: the case the parent walk gets wrong. Measured on this machine on 30-08-2026 — a session's build
/// launcher exited and its 325 MB child dropped straight out of the figure while it was still running.
/// </summary>
public class SessionProcessMembershipTests
{
    [Fact]
    public void Measure_WhenTheProcessThatSpawnedItIsGone_StillCountsTheChildAndCallsItAbandoned()
    {
        var membership = new SessionProcessMembership();
        ProcessRow[] whileTheLauncherLived =
        [
            new(100, 1, TimeSpan.Zero, 50),
            new(200, 100, TimeSpan.Zero, 60),
            new(300, 200, TimeSpan.Zero, 325),
        ];

        membership.Measure(whileTheLauncherLived, 100);

        ProcessRow[] afterItExited =
        [
            new(100, 1, TimeSpan.Zero, 50),
            new(300, 200, TimeSpan.Zero, 325),
        ];

        var measured = membership.Measure(afterItExited, 100);

        Assert.Equal(375, measured.Usage.WorkingSetBytes);
        Assert.Equal(2, measured.Count);
        Assert.Equal(1, measured.AbandonedCount);

        // What the meter did before: from the session's process alone, the child is simply not there.
        Assert.Equal(50, ProcessTree.Sum(afterItExited, 100).WorkingSetBytes);
    }
}
