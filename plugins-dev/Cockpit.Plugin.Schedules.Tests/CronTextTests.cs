using Cockpit.Plugin.Schedules.Contracts;

namespace Cockpit.Plugin.Schedules.Tests;

// The builder's English and cron, both ways; a cron no shape fits reads as Custom (null).
public class CronTextTests
{
    [Theory]
    [InlineData("*/15 * * * *", "Every 15 minutes")]
    [InlineData("0 9 * * *", "Every day at 09:00")]
    [InlineData("30 7 * * 1-5", "Every weekday at 07:30")]
    [InlineData("0 9 * * 1,3,5", "Every Monday, Wednesday and Friday at 09:00")]
    [InlineData("0 10 * * 0#1", "First Sunday of the month at 10:00")]
    [InlineData("0 8 1 * *", "On day 1 of every month at 08:00")]
    public void CronAndEnglish_TranslateBothWays(string cron, string english)
    {
        Assert.Equal((english, cron), (CronText.Describe(cron), CronText.Parse(english)));
    }

    [Theory]
    [InlineData("0 10 1-7 * 0")]
    public void ACronNoShapeFits_ReadsAsCustom(string cron)
    {
        Assert.Null(CronText.Describe(cron));
    }
}
