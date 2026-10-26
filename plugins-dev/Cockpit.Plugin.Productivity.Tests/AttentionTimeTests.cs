using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Tests;

// AC-1731 layer 2 (money): the number the hours proposal is billed from.
public sealed class AttentionTimeTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 8, 0, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(1, 2295, 1565, 315, 20, true, 1860, 2590, 2295, 730)]
    public void Compute_CountsOverlapOnceAndKeepsAgentTimeBeside(
        int sessions,
        int busyMinutes,
        int measuredMinutes,
        int ownMinutes,
        int overlapMinutes,
        bool countAgentWithoutYou,
        int expectedYourMinutes,
        int expectedTotalMinutes,
        int expectedAgentMinutes,
        int expectedWithoutYouPerSessionMinutes)
    {
        // Actions every 5 minutes keep one block open; the last one adds the 10-minute block length.
        var lines = Enumerable.Range(0, ((measuredMinutes - 10) / 5) + 1)
            .Select(step => _Line(CockpitTimelineKind.Operator, Monday.AddMinutes(step * 5), "you"))
            .ToList();
        for (var session = 0; session < sessions; session++)
        {
            lines.Add(_Line(CockpitTimelineKind.Status, Monday, $"agent-{session}", CockpitTimelineStatus.Busy));
            lines.Add(_Line(CockpitTimelineKind.Status, Monday.AddMinutes(busyMinutes), $"agent-{session}", CockpitTimelineStatus.Idle));
        }

        CockpitTimeEntry[] own = ownMinutes == 0
            ? []
            : [new(Guid.NewGuid(), "Eigen regel", Monday.AddMinutes(measuredMinutes - overlapMinutes), TimeSpan.FromMinutes(ownMinutes), "cockpit", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Open)];

        var time = AttentionTime.Compute(
            new CockpitTimeline(lines, [], []),
            own,
            AttentionSettings.Default with { CountAgentWithoutYou = countAgentWithoutYou },
            Monday.AddDays(7));

        Assert.Equal(TimeSpan.FromMinutes(expectedYourMinutes), time.YourTime);
        Assert.Equal(TimeSpan.FromMinutes(expectedTotalMinutes), time.Total);
        Assert.Equal(TimeSpan.FromMinutes(expectedAgentMinutes), time.AgentTime);
        Assert.Equal(sessions, time.AgentWithoutYouPerSession.Count);
        Assert.All(time.AgentWithoutYouPerSession.Values, value => Assert.Equal(TimeSpan.FromMinutes(expectedWithoutYouPerSessionMinutes), value));
    }

    [Fact]
    public void Compute_GivesEachMinuteToTheProjectLastActedOn_AcrossSessions()
    {
        var lines = new[]
        {
            _Line(CockpitTimelineKind.Operator, Monday, "pane-a", project: "syn-portal"),
            _Line(CockpitTimelineKind.Operator, Monday.AddMinutes(4), "pane-b", project: "syn-api"),
            _Line(CockpitTimelineKind.Operator, Monday.AddMinutes(8), "pane-c", project: "syn-portal"),
        };

        var time = AttentionTime.Compute(new CockpitTimeline(lines, [], []), [], AttentionSettings.Default, Monday.AddDays(1));

        Assert.Equal(TimeSpan.FromMinutes(18), time.Measured);
        Assert.Equal(
            new ProjectTime[] { new("syn-portal", TimeSpan.FromMinutes(14)), new("syn-api", TimeSpan.FromMinutes(4)) },
            time.MeasuredPerProject);
    }

    private static CockpitTimelineLine _Line(
        CockpitTimelineKind kind,
        DateTimeOffset at,
        string pane,
        CockpitTimelineStatus? status = null,
        string project = "cockpit") =>
        new(kind, at, pane, project, null, null, status, kind == CockpitTimelineKind.Operator ? CockpitOperatorAction.MessageSent : null, null);
}
