using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.TimeTracking;
using NSubstitute;

namespace Cockpit.Plugin.Productivity.Tests;

// Layer 2: actual booking overruns stay visible, and only the verified recipient may change billing outcomes.
public sealed class HoursResultTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly Guid EntryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task BookedAboveTheSchedule_IsAnOverrunAndNeverGreen()
    {
        var (host, entries, storage) = _Rig("receiver");
        var reporter = new HoursResultReporter(host);

        var report = await reporter.ReportAsync(new HoursResultRequest(Monday, [new(WeekOfferMarker.Format(43, Monday, 1), "booked")], [new(Monday, 60)]));

        var warning = Assert.Single(report.Warnings);
        Assert.True(warning.Overrun);
        Assert.Equal((480, 60, 480), (warning.ScheduleMinutes, warning.AlreadyBooked, warning.ProposedMinutes));
        Assert.False(report.IsGreen);
        Assert.Equal("⚠ overschrijding", report.AgentStatus(EntryId, "portal"));
        await entries.Received(1).SetOutcomeAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(EntryId)), CockpitTimeEntryOutcome.Booked, null, Arg.Any<CancellationToken>());
        Assert.Equal("booked", Assert.Single(storage.Get<WeekReport>(WeekReport.StorageKey(Monday))!.Rows).Status);
    }

    [Fact]
    public async Task AnotherVerifiedPane_CannotReportForTheRecipientOrWriteAnOutcome()
    {
        var (host, entries, storage) = _Rig("another-pane");
        var reporter = new HoursResultReporter(host);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reporter.ReportAsync(new HoursResultRequest(Monday, [new(WeekOfferMarker.Format(43, Monday, 1), "booked")], [])));

        Assert.Contains("ontvanger", error.Message, StringComparison.Ordinal);
        await entries.DidNotReceiveWithAnyArgs().SetOutcomeAsync(default!, default, default, default);
        Assert.Null(Assert.Single(storage.Get<WeekReport>(WeekReport.StorageKey(Monday))!.Rows).Status);
    }

    private static (ICockpitHost Host, ICockpitTimeEntries Entries, _Storage Storage) _Rig(string caller)
    {
        var host = Substitute.For<ICockpitHost>();
        var entries = Substitute.For<ICockpitTimeEntries>();
        var storage = new _Storage();
        host.CurrentMcpCaller.Returns(new McpCaller(caller, McpCallerKind.Session));
        host.TimeEntries.Returns(entries);
        host.Storage.Returns(storage);
        entries.ListAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([new CockpitTimeEntry(EntryId, "Portal", new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(2)), TimeSpan.FromHours(8), "portal", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Offered, OfferedToPaneId: "receiver")]);
        storage.Set(WeekReport.StorageKey(Monday), new WeekReport([new(new WeekOfferRow(Monday, 1, 480, EntryId, "portal"), "receiver", 480)], null, null, 0));
        return (host, entries, storage);
    }

    private sealed class _Storage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];
        public T? Get<T>(string key) => _values.GetValueOrDefault(key) is T value ? value : default;
        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
