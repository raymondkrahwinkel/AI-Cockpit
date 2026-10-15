using Cockpit.Plugin.Schedules.Engine;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1542 criterion 1: a cron means wall-clock time in its own zone, across the daylight-saving switch.
public class TaskScheduleTests
{
    private static readonly DateTimeOffset MidMarch = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

    // 2026-03-29 is the spring switch in Amsterdam: Monday 09:00 is 08:00 UTC before it and 07:00 UTC after.
    [Theory]
    [InlineData("Europe/Amsterdam", new[] { 8, 8, 7, 7 })]
    [InlineData("UTC", new[] { 9, 9, 9, 9 })]
    public void MondayNine_FiresOnMondayNineInItsZone_AcrossTheSpringSwitch(string zoneId, int[] utcHours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var schedule = TaskSchedule.TryParse("0 9 * * 1", zone) ?? throw new InvalidOperationException("Unparsed.");

        var slots = schedule.Slots(MidMarch, MidMarch.AddDays(28), zone).ToList();

        Assert.All(slots, slot => Assert.Equal((DayOfWeek.Monday, 9, 0), (TimeZoneInfo.ConvertTime(slot, zone).DayOfWeek, TimeZoneInfo.ConvertTime(slot, zone).Hour, slot.Minute)));
        Assert.Equal(utcHours, slots.Select(slot => slot.UtcDateTime.Hour));
    }

    [Fact]
    public void EveryDayAtNine_FiresOnSevenDifferentDaysInAWeek()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        var schedule = TaskSchedule.TryParse("0 9 * * *", zone) ?? throw new InvalidOperationException("Unparsed.");

        var days = schedule.Slots(MidMarch, MidMarch.AddDays(7), zone).Select(slot => slot.Date).Distinct();

        Assert.Equal(7, days.Count());
    }

    [Fact]
    public void Once_IsThatLocalMoment_AndEveryNMinutes_SitsOnTheUtcGrid()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        var once = TaskSchedule.TryParse("once 2026-03-30 09:00", zone) ?? throw new InvalidOperationException("Unparsed.");
        var every = TaskSchedule.TryParse("every 15 m", zone) ?? throw new InvalidOperationException("Unparsed.");
        var from = MidMarch.AddMinutes(7);

        Assert.Equal([new DateTimeOffset(2026, 3, 30, 7, 0, 0, TimeSpan.Zero)], once.Slots(MidMarch, MidMarch.AddDays(28), zone));
        Assert.Equal([MidMarch.AddMinutes(15), MidMarch.AddMinutes(30)], every.Slots(from, from.AddMinutes(30), zone));
    }

    // 02:30 on 2026-03-29 does not exist in Amsterdam.
    [Theory]
    [InlineData("")]
    [InlineData("61 * * * *")]
    [InlineData("every 0 m")]
    [InlineData("once 2026-13-01 09:00")]
    [InlineData("once 2026-03-29 02:30")]
    public void AnUnreadableSchedule_IsRefused(string schedule)
    {
        Assert.Null(TaskSchedule.TryParse(schedule, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")));
    }
}
