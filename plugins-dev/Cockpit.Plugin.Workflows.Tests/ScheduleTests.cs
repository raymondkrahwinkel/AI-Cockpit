using Cockpit.Plugin.Workflows.Engine;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// When a scheduled flow is due (#69). The rule that matters most is the last one: a schedule nobody can read must
/// never fire. The alternative — treating an unparseable "When" as "always" — is a flow that runs every half minute
/// forever, and the operator's only clue is that their machine got hot.
/// </summary>
public class ScheduleTests
{
    [Theory]
    [InlineData("09:00", "2026-07-13T09:00:30", true)]
    [InlineData("9:00", "2026-07-13T09:00:00", true)]
    [InlineData("09:00", "2026-07-13T09:01:00", false)]
    [InlineData("09:00", "2026-07-13T10:00:00", false)]
    public void ATimeOfDay_IsDueInTheMinuteItNames(string when, string now, bool due) =>
        Schedule.IsDue(when, DateTimeOffset.Parse(now)).Should().Be(due);

    [Theory]
    [InlineData("every 15m", "2026-07-13T09:30:00", true)]
    [InlineData("every 15m", "2026-07-13T09:31:00", false)]
    [InlineData("every 2h", "2026-07-13T10:00:00", true)]
    [InlineData("every 2h", "2026-07-13T11:00:00", false)]
    public void AnInterval_IsDueWhenItDivides(string when, string now, bool due) =>
        Schedule.IsDue(when, DateTimeOffset.Parse(now)).Should().Be(due);

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("every")]
    [InlineData("every 0m")]
    [InlineData("every -5m")]
    [InlineData("* * * * *")]
    [InlineData("25:00")]
    public void AScheduleNobodyCanRead_NeverFires(string when) =>
        Schedule.IsDue(when, DateTimeOffset.Parse("2026-07-13T09:00:00")).Should().BeFalse();
}
