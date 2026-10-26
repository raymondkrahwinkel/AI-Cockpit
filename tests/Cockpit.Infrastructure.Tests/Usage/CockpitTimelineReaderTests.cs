using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Usage;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Usage;

// AC-1720: the timeline reader. (a) L2 money: a range over the 90-day boundary counts the boundary day once, from its
// aggregate, and the first raw delta after it from the aggregate's closing total. (b) L2 formats: per-turn deltas taken
// from running totals, two records of one pane.
public sealed class CockpitTimelineReaderTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public CockpitTimelineReaderTests() => Directory.CreateDirectory(_directory);

    private string _TimelinePath => Path.Combine(_directory, "session-timeline.jsonl");

    private string _UsagePath => Path.Combine(_directory, "usage-history.jsonl");

    private string _AggregatesPath => Path.Combine(_directory, "session-day-aggregates.jsonl");

    [Fact]
    public async Task ARangeOverTheBoundary_CountsTheBoundaryDayOnceFromItsAggregate_AndTheNextRawTurnFromItsClosingTotal()
    {
        var boundary = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        var started = boundary.AddHours(9);
        var closing = _Total("builder", started, boundary.AddHours(10), input: 100, cost: 1.0, turns: 1);
        await new SessionDayAggregateLog(_AggregatesPath, NullLogger.Instance).AppendAsync(new SessionDayAggregate(
            "builder", DateOnly.FromDateTime(boundary.DateTime), "the-project", null, ["AC-9"], TimeSpan.FromHours(1), TimeSpan.Zero, 1,
            100, 0, 0, 0, 1.0, 1, null, closing));
        var usage = new UsageHistoryLog(_UsagePath, NullLogger<UsageHistoryLog>.Instance);
        await usage.RecordAsync(_Total("builder", started, boundary.AddDays(1).AddHours(10), input: 160, cost: 1.6, turns: 2));
        var timeline = new SessionTimelineLog(_TimelinePath, NullLogger.Instance);
        foreach (var at in new[] { boundary.AddHours(9), boundary.AddDays(1).AddHours(9) })
        {
            await timeline.RecordAsync(new SessionTimelineEntry(SessionTimelineKind.Status, at, "builder", "the-project", null, "AC-9", SessionStatus.Busy));
        }

        var range = await _Reader(usage).QueryAsync(boundary.AddDays(-1), boundary.AddDays(2));

        Assert.Equal(DateOnly.FromDateTime(boundary.DateTime), Assert.Single(range.Days).Day);
        Assert.Equal(boundary.AddDays(1).AddHours(9), Assert.Single(range.Lines).At);
        var delta = Assert.Single(range.Usage);
        Assert.Equal((60, 1), (delta.InputTokens, delta.Turns));
        Assert.Equal(160, range.Days.Sum(day => day.InputTokens) + range.Usage.Sum(turn => turn.InputTokens));
        Assert.Equal(1.6, range.Days.Sum(day => day.ReportedCostUsd) + range.Usage.Sum(turn => turn.CostUsd), 6);
    }

    [Fact]
    public async Task AWorkWeek_GivesEachSessionItsTimelineAndPerTurnDeltas_ThatSumToItsLastRunningTotal()
    {
        var usage = new UsageHistoryLog(_UsagePath, NullLogger<UsageHistoryLog>.Instance);
        var started = Monday.AddHours(9);
        await usage.RecordAsync(_Total("builder", started, Monday.AddHours(10), input: 100, cost: 1.0, turns: 1));
        await usage.RecordAsync(_Total("other", started, Monday.AddHours(11), input: 40, cost: 0.4, turns: 1));
        await usage.RecordAsync(_Total("builder", started, Monday.AddDays(1).AddHours(10), input: 250, cost: 2.5, turns: 3));
        await usage.RecordAsync(_Total("builder", started, Monday.AddDays(8), input: 900, cost: 9, turns: 9));
        await new SessionTimelineLog(_TimelinePath, NullLogger.Instance).RecordAsync(
            new SessionTimelineEntry(SessionTimelineKind.Status, Monday.AddHours(9), "builder", "the-project", "run-1", "AC-9", SessionStatus.Busy));

        var week = await _Reader(usage).QueryAsync(Monday, Monday.AddDays(5), new CockpitTimelineFilter(ProjectId: "the-project"));

        var line = Assert.Single(week.Lines);
        Assert.Equal((CockpitTimelineKind.Status, CockpitTimelineStatus.Busy, "AC-9"), (line.Kind, line.Status, line.Ticket));
        Assert.Equal([(100, 1.0, 1), (150, 1.5, 2)], week.Usage.Select(delta => (delta.InputTokens, delta.CostUsd, delta.Turns)));
        Assert.All(week.Usage, delta => Assert.Equal("builder", delta.PaneId));
        Assert.Equal((250, 2.5, 3), (week.Usage.Sum(delta => delta.InputTokens), week.Usage.Sum(delta => delta.CostUsd), week.Usage.Sum(delta => delta.Turns)));
    }

    private CockpitTimelineReader _Reader(UsageHistoryLog usage) =>
        new(usage, _TimelinePath, _AggregatesPath, new UtcClock(), NullLogger.Instance);

    private static UsageSnapshot _Total(string paneId, DateTimeOffset startedAt, DateTimeOffset recordedAt, int input, double cost, int turns) =>
        new() { PaneId = paneId, StartedAt = startedAt, RecordedAt = recordedAt, InputTokens = input, TotalCostUsd = cost, Turns = turns };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class UtcClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
