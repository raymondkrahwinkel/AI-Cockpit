using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Usage;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Usage;

// AC-1719: retention. (a) L2 money: a day 100 days back folds into an aggregate with the same time, operator actions,
// tokens and cost as its raw lines, the usage as differences of running totals. (b) L2 data loss: folding twice, or a
// rollover dropping a day, gives one aggregate per session and day; an own time entry (AC-1729) keeps its status.
public sealed class SessionDayRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = new(Now.AddDays(-100).Date, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public SessionDayRetentionTests() => Directory.CreateDirectory(_directory);

    private string _TimelinePath => Path.Combine(_directory, "session-timeline.jsonl");

    private string _UsagePath => Path.Combine(_directory, "usage-history.jsonl");

    private string _AggregatesPath => Path.Combine(_directory, "session-day-aggregates.jsonl");

    [Fact]
    public async Task ADayOlderThan90Days_FoldsIntoOneAggregate_WithTheSameTimeTokensAndCost_AsItsRawLines()
    {
        await _WriteFixturesAsync();

        await _Retention().RunAsync();

        var aggregate = Assert.Single(await _AggregatesAsync());
        Assert.Equal(("builder", DateOnly.FromDateTime(Old.DateTime), "the-project", "run-7"), (aggregate.PaneId, aggregate.Day, aggregate.ProjectId, aggregate.RunId));
        Assert.Equal(["AC-9"], aggregate.Tickets);
        Assert.Equal(TimeSpan.FromMinutes(105), aggregate.AgentTime);
        Assert.Equal(TimeSpan.FromMinutes(15), aggregate.WaitingTime);
        Assert.Equal(2, aggregate.OperatorActions);
        Assert.Equal((600, 70, 3), (aggregate.InputTokens, aggregate.OutputTokens, aggregate.Turns));
        Assert.Equal(0.80, aggregate.ReportedCostUsd, 6);

        var cut = new DateTimeOffset(Now.AddDays(-90).Date, TimeSpan.Zero);
        var timeline = _Lines<SessionTimelineEntry>(_TimelinePath);
        var usage = _Lines<UsageSnapshot>(_UsagePath);
        Assert.All(timeline, line => Assert.True(line.At >= cut));
        Assert.All(usage, snapshot => Assert.True(snapshot.RecordedAt >= cut));
        Assert.Equal(SessionStatus.Idle, Assert.Single(timeline).Status);
        Assert.Equal(900, Assert.Single(usage).InputTokens);
    }

    [Fact]
    public async Task FoldingTwice_GivesOneAggregatePerSessionAndDay_AndAnOwnEntryOf200DaysBackStaysBooked()
    {
        var ownEntries = new CockpitTimeEntriesLog(Path.Combine(_directory, "own-time-entries.jsonl"), NullLogger<CockpitTimeEntriesLog>.Instance, new Clock());
        var entry = await ownEntries.AddAsync("Client call", Now.AddDays(-200), TimeSpan.FromHours(1), null, CockpitTimeEntrySource.Duration);
        await ownEntries.MarkOfferedAsync([entry.Id], Now.AddDays(-199), "pane-agent");
        await ownEntries.SetOutcomeAsync([entry.Id], CockpitTimeEntryOutcome.Booked);
        await _WriteFixturesAsync();
        var timeline = await File.ReadAllTextAsync(_TimelinePath);
        var usage = await File.ReadAllTextAsync(_UsagePath);

        await _Retention().RunAsync();

        // A crash after the aggregates were written but before the raw lines went: the next start folds them again.
        await File.WriteAllTextAsync(_TimelinePath, timeline);
        await File.WriteAllTextAsync(_UsagePath, usage);
        await _Retention().RunAsync();

        Assert.Equal("builder", Assert.Single(await _AggregatesAsync()).PaneId);

        // A busy trail rolls twice: the second roll would drop a whole day, which is folded first, once.
        var tiny = new UsageHistoryLog(_UsagePath, NullLogger<UsageHistoryLog>.Instance, maxSizeBytes: 1);
        var dropped = _Snapshot(Now.AddDays(-3), 50, 5, 0.05, 1) with { PaneId = "roller", StartedAt = Now.AddDays(-3) };
        await tiny.RecordAsync(dropped);
        await tiny.RecordAsync(dropped with { RecordedAt = Now.AddDays(-2), InputTokens = 80, Turns = 2 });
        await tiny.RecordAsync(dropped with { RecordedAt = Now.AddDays(-2).AddHours(1), InputTokens = 90, Turns = 3 });
        var folded = Assert.Single(await _AggregatesAsync(), aggregate => aggregate.PaneId == "roller");
        Assert.Equal((50, 1), (folded.InputTokens, folded.Turns));
        Assert.Equal([80, 90], (await tiny.ReadRecentAsync()).Where(snapshot => snapshot.PaneId == "roller").Select(snapshot => snapshot.InputTokens).Order());

        var kept = Assert.Single(await ownEntries.ListAsync(Now.AddDays(-201), Now));
        Assert.Equal((entry.Id, CockpitTimeEntryStatus.Booked), (kept.Id, kept.Status));
    }

    // One busy session on `Old`, with its usage as running totals per turn, and one recent line in each raw file.
    private async Task _WriteFixturesAsync()
    {
        _Timeline(
            _Line(SessionTimelineKind.Status, Old.AddHours(9), status: SessionStatus.Busy),
            _Line(SessionTimelineKind.Operator, Old.AddHours(9.5), action: SessionOperatorAction.MessageSent),
            _Line(SessionTimelineKind.Status, Old.AddHours(10), status: SessionStatus.NeedsAttention),
            _Line(SessionTimelineKind.Operator, Old.AddHours(10.25), action: SessionOperatorAction.PermissionAnswered),
            _Line(SessionTimelineKind.Status, Old.AddHours(10.25), status: SessionStatus.Busy),
            _Line(SessionTimelineKind.Status, Old.AddHours(11), status: SessionStatus.Idle),
            _Line(SessionTimelineKind.Status, Now.AddDays(-5), status: SessionStatus.Idle));

        var usage = new UsageHistoryLog(_UsagePath, NullLogger<UsageHistoryLog>.Instance);
        await usage.RecordAsync(_Snapshot(Old.AddHours(9.3), 100, 10, 0.10, 1));
        await usage.RecordAsync(_Snapshot(Old.AddHours(10.8), 300, 40, 0.35, 2));
        await usage.RecordAsync(_Snapshot(Old.AddHours(10.98), 600, 70, 0.80, 3));
        await usage.RecordAsync(_Snapshot(Now.AddDays(-5), 900, 100, 1.20, 4));
    }

    private static UsageSnapshot _Snapshot(DateTimeOffset at, int input, int output, double cost, int turns) => new()
    {
        PaneId = "builder",
        StartedAt = Old.AddHours(9),
        RecordedAt = at,
        RunId = "run-7",
        Model = "claude-opus-5-5",
        InputTokens = input,
        OutputTokens = output,
        TotalCostUsd = cost,
        Turns = turns,
    };

    private static SessionTimelineEntry _Line(SessionTimelineKind kind, DateTimeOffset at, SessionStatus? status = null, SessionOperatorAction? action = null) =>
        new(kind, at, "builder", "the-project", null, "AC-9", status, action);

    private void _Timeline(params SessionTimelineEntry[] lines) =>
        File.WriteAllLines(_TimelinePath, lines.Select(line => JsonSerializer.Serialize(line, Json)));

    private static List<T> _Lines<T>(string path) =>
        [.. File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<T>(line, Json)).OfType<T>()];

    private SessionDayRetention _Retention() =>
        new(_TimelinePath, _UsagePath, new Clock(), NullLogger.Instance);

    private async Task<IReadOnlyList<SessionDayAggregate>> _AggregatesAsync() =>
        await new SessionDayAggregateLog(_AggregatesPath, NullLogger.Instance).ReadRecentAsync(int.MaxValue);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
