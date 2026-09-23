using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

// When a scheduled flow is due (#69, #AC-1359). The rule that matters most is the last one: a schedule nobody can
// read must never fire. The alternative — treating an unparseable "When" as "always" — is a flow that runs every
// half minute forever, and the operator's only clue is that their machine got hot.
public class ScheduleTests
{
    // `Previous` always returns a UTC-anchored instant, so the comparison is done in UTC too — comparing raw
    // Year/Month/Hour on two `DateTimeOffset` values with different offsets would compare the wrong thing.
    private static bool _DueInZone(string when, string now, TimeZoneInfo? zone)
    {
        var n = DateTimeOffset.Parse(now).UtcDateTime;
        var slot = zone is null ? null : Schedule.Previous(when, zone, DateTimeOffset.Parse(now))?.UtcDateTime;
        return slot is { } s && s.Year == n.Year && s.Month == n.Month && s.Day == n.Day && s.Hour == n.Hour && s.Minute == n.Minute;
    }

    private static bool _Due(string when, string now) => _DueInZone(when, now, TimeZoneInfo.Local);

    [Theory]
    [InlineData("09:00", "2026-07-13T09:00:30", true)]
    [InlineData("9:00", "2026-07-13T09:00:00", true)]
    [InlineData("09:00", "2026-07-13T09:01:00", false)]
    [InlineData("09:00", "2026-07-13T10:00:00", false)]
    [InlineData("mon 09:00", "2026-07-13T09:00:00", true)] // 2026-07-13 is a Monday.
    [InlineData("mon 09:00", "2026-07-14T09:00:00", false)] // Tuesday.
    [InlineData("mon,fri 09:00", "2026-07-17T09:00:00", true)] // Friday.
    [InlineData("once 2026-10-01 09:00", "2026-10-01T09:00:00", true)]
    [InlineData("once 2026-10-01 09:00", "2026-09-30T09:00:00", false)] // Has not arrived yet.
    public void ATimeOfDay_IsDueInTheMinuteItNames(string when, string now, bool due) =>
        Assert.Equal(due, _Due(when, now));

    [Theory]
    [InlineData("every 15m", "2026-07-13T09:30:00", true)]
    [InlineData("every 15m", "2026-07-13T09:31:00", false)]
    [InlineData("every 2h", "2026-07-13T10:00:00", true)]
    [InlineData("every 2h", "2026-07-13T11:00:00", false)]
    public void AnInterval_IsDueWhenItDivides(string when, string now, bool due) =>
        Assert.Equal(due, _Due(when, now));

    [Theory]
    [InlineData("", "2026-07-13T09:00:00")]
    [InlineData("soon", "2026-07-13T09:00:00")]
    [InlineData("every", "2026-07-13T09:00:00")]
    [InlineData("every 0m", "2026-07-13T09:00:00")]
    [InlineData("every -5m", "2026-07-13T09:00:00")]
    [InlineData("* * * * *", "2026-07-13T09:00:00")]
    [InlineData("25:00", "2026-07-13T09:00:00")]
    [InlineData("mon 25:00", "2026-07-13T09:00:00")]
    [InlineData("once 2026-02-30 09:00", "2026-07-13T09:00:00")] // 30 February does not exist.
    public void AScheduleNobodyCanRead_NeverFires(string when, string now) =>
        Assert.False(_Due(when, now));

    // The zone belongs to the trigger, not the machine running it (#AC-1359) — a container in UTC must still fire
    // "mon 09:00" at Amsterdam's 09:00, and an unknown id never fires. A fall-back day, where 02:30 local happens
    // twice, resolves to one instant deterministically, so the slot fires exactly once, not twice.
    [Theory]
    [InlineData("mon 09:00", "Europe/Amsterdam", "2026-07-13T07:00:00Z", true)] // 09:00 CEST is 07:00 UTC.
    [InlineData("mon 09:00", "Europe/Amsterdam", "2026-07-13T09:00:00Z", false)] // A UTC clock reading its own 09:00 is two hours early.
    [InlineData("mon 09:00", "Mars/Base", "2026-07-13T07:00:00Z", false)]
    [InlineData("02:30", "Europe/Amsterdam", "2026-10-25T01:30:00Z", true)] // The CET (standard-time) occurrence.
    [InlineData("02:30", "Europe/Amsterdam", "2026-10-25T00:30:00Z", false)] // The earlier, CEST occurrence — not this one.
    public void APreviousSlot_IsReadInTheTriggersOwnZone_NotTheMachines(string when, string zoneId, string nowUtc, bool due) =>
        Assert.Equal(due, _DueInZone(when, nowUtc, Schedule.ResolveZone(zoneId)));

    // A single weekday checked before its own time on the day itself has no candidate this week — the previous
    // slot is last week's, not "nothing". Caught during review: the search stopped one day short of a full week.
    [Fact]
    public void AWeekdaySchedule_ChecksBeforeItsOwnTime_FallsBackToLastWeek()
    {
        var slot = Schedule.Previous("mon 09:00", TimeZoneInfo.Local, DateTimeOffset.Parse("2026-07-13T08:00:00")); // Monday, 08:00.

        Assert.NotNull(slot);
        var local = TimeZoneInfo.ConvertTime(slot.Value, TimeZoneInfo.Local);
        Assert.Equal(new DateOnly(2026, 7, 6), DateOnly.FromDateTime(local.Date)); // The Monday before.
    }
}
