using System.Globalization;
using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

public sealed class ScheduleTests
{
    [Theory]
    [InlineData("09:00", "2026-10-03T08:00:00Z", "2026-10-03T09:00:00Z")]
    [InlineData("mon,fri 09:00", "2026-10-03T08:00:00Z", "2026-10-05T09:00:00Z")]
    [InlineData("once 2026-10-04 09:00", "2026-10-03T08:00:00Z", "2026-10-04T09:00:00Z")]
    [InlineData("every 2h", "2026-10-03T08:01:00Z", "2026-10-03T10:00:00Z")]
    public void Next_ReturnsTheFirstOccurrenceStrictlyAfterNow(string schedule, string nowText, string expectedText)
    {
        var now = DateTimeOffset.Parse(nowText, CultureInfo.InvariantCulture);
        var expected = DateTimeOffset.Parse(expectedText, CultureInfo.InvariantCulture);

        Assert.Equal(expected, Schedule.Next(schedule, TimeZoneInfo.Utc, now));
    }

    [Fact]
    public void Next_UsesTheFlowTimeZoneAndSkipsAnInvalidWallClockTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        var beforeSpringForward = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

        var next = Schedule.Next("02:30", zone, beforeSpringForward);

        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), next);
    }
}
