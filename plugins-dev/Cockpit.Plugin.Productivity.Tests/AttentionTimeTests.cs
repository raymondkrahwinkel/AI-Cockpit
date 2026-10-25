using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Tests;

// AC-1731 layer 2 (money): the number the hours proposal is billed from.
public sealed class AttentionTimeTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 8, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Compute_CountsOverlapOnceAndKeepsAgentTimeBeside()
    {
        var cases = new[]
        {
            (Sessions: 1, Busy: 2295, Measured: 1565, Own: 315, Overlap: 20, CountWithoutYou: false, Your: 1860, Total: 1860, Agent: 2295, WithoutYou: 730),
            (Sessions: 1, Busy: 2295, Measured: 1565, Own: 315, Overlap: 20, CountWithoutYou: true, Your: 1860, Total: 2590, Agent: 2295, WithoutYou: 730),
            (Sessions: 3, Busy: 60, Measured: 20, Own: 0, Overlap: 0, CountWithoutYou: false, Your: 20, Total: 20, Agent: 180, WithoutYou: 40),
        };
        foreach (var item in cases)
        {
            // Actions every 5 minutes keep one block open; the last one adds the 10-minute block length.
            var lines = Enumerable.Range(0, ((item.Measured - 10) / 5) + 1)
                .Select(step => _Line(CockpitTimelineKind.Operator, Monday.AddMinutes(step * 5), "you"))
                .ToList();
            for (var session = 0; session < item.Sessions; session++)
            {
                lines.Add(_Line(CockpitTimelineKind.Status, Monday, $"agent-{session}", CockpitTimelineStatus.Busy));
                lines.Add(_Line(CockpitTimelineKind.Status, Monday.AddMinutes(item.Busy), $"agent-{session}", CockpitTimelineStatus.Idle));
            }

            CockpitTimeEntry[] own = item.Own == 0
                ? []
                : [new(Guid.NewGuid(), "Eigen regel", Monday.AddMinutes(item.Measured - item.Overlap), TimeSpan.FromMinutes(item.Own), "cockpit", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Open)];

            var time = AttentionTime.Compute(
                new CockpitTimeline(lines, [], []),
                own,
                AttentionSettings.Default with { CountAgentWithoutYou = item.CountWithoutYou },
                Monday.AddDays(7));

            Assert.Equal(TimeSpan.FromMinutes(item.Your), time.YourTime);
            Assert.Equal(TimeSpan.FromMinutes(item.Total), time.Total);
            Assert.Equal(TimeSpan.FromMinutes(item.Agent), time.AgentTime);
            Assert.Equal(item.Sessions, time.AgentWithoutYouPerSession.Count);
            Assert.All(time.AgentWithoutYouPerSession.Values, value => Assert.Equal(TimeSpan.FromMinutes(item.WithoutYou), value));
        }
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
