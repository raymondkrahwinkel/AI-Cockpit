using System.Globalization;
using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

internal static class ScheduleTests
{
    internal static void Verify()
    {
        Next_ReturnsTheFirstOccurrenceStrictlyAfterNow("09:00", "2026-10-03T08:00:00Z", "2026-10-03T09:00:00Z");
        Next_ReturnsTheFirstOccurrenceStrictlyAfterNow("mon,fri 09:00", "2026-10-03T08:00:00Z", "2026-10-05T09:00:00Z");
        Next_ReturnsTheFirstOccurrenceStrictlyAfterNow("once 2026-10-04 09:00", "2026-10-03T08:00:00Z", "2026-10-04T09:00:00Z");
        Next_ReturnsTheFirstOccurrenceStrictlyAfterNow("every 2h", "2026-10-03T08:01:00Z", "2026-10-03T10:00:00Z");
        Next_UsesTheFlowTimeZoneAndSkipsAnInvalidWallClockTime();
    }

    private static void Next_ReturnsTheFirstOccurrenceStrictlyAfterNow(string schedule, string nowText, string expectedText)
    {
        var now = DateTimeOffset.Parse(nowText, CultureInfo.InvariantCulture);
        var expected = DateTimeOffset.Parse(expectedText, CultureInfo.InvariantCulture);

        Assert.Equal(expected, Schedule.Next(schedule, TimeZoneInfo.Utc, now));
    }

    private static void Next_UsesTheFlowTimeZoneAndSkipsAnInvalidWallClockTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        var beforeSpringForward = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

        var next = Schedule.Next("02:30", zone, beforeSpringForward);

        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), next);
    }
}
