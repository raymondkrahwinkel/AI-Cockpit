using Cockpit.Plugin.Productivity.Contracts;

namespace Cockpit.Plugin.Productivity.Tests;

// AC-1816 layer 2 (data integrity): a refresh only runs when it cannot overwrite what the operator is doing.
public sealed class RefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    [Fact]
    public void IsDue_RefreshesAVisibleIdleScreenAfterTheInterval_AndNeverWhileEditingHiddenOrBusy()
    {
        var stale = Now - TimeSpan.FromSeconds(61);

        Assert.True(RefreshPolicy.IsDue(true, false, false, stale, Now, Minute));
        Assert.True(RefreshPolicy.IsDue(true, false, false, null, Now, Minute));
        Assert.False(RefreshPolicy.IsDue(true, false, false, Now - TimeSpan.FromSeconds(59), Now, Minute));
        Assert.False(RefreshPolicy.IsDue(true, true, false, stale, Now, Minute));
        Assert.False(RefreshPolicy.IsDue(false, false, false, stale, Now, Minute));
        Assert.False(RefreshPolicy.IsDue(true, false, true, stale, Now, Minute));
    }

    [Fact]
    public void IntervalAndAge_FallBackToSixtySecondsOutsideTheRange_AndReadTheRealTimeSinceTheLastRefresh()
    {
        Assert.Equal(Minute, RefreshPolicy.Interval(5));
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshPolicy.Interval(30));
        Assert.Equal("nog niet bijgewerkt", RefreshPolicy.Age(null, Now));
        Assert.Equal("zojuist bijgewerkt", RefreshPolicy.Age(Now - TimeSpan.FromSeconds(3), Now));
        Assert.Equal("bijgewerkt 42 s geleden", RefreshPolicy.Age(Now - TimeSpan.FromSeconds(42), Now));
        Assert.Equal("bijgewerkt 5 min geleden", RefreshPolicy.Age(Now - TimeSpan.FromMinutes(5), Now));
    }
}
