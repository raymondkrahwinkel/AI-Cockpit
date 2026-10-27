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

    [Theory]
    [InlineData(480, 60, 0, true, "⚠ overschrijding")]
    public async Task BookedAboveTheSchedule_IsAnOverrunAndNeverGreen(int minutes, int alreadyBooked, int priorBooked, bool overrun, string expectedStatus)
    {
        var (host, entries, storage) = _Rig("receiver", minutes, priorBooked);
        var reporter = new HoursResultReporter(host);
        var request = new HoursResultRequest(Monday, [new(WeekOfferMarker.Format(43, Monday, 1), "booked")], [new(Monday, alreadyBooked)]);
        var offered = Assert.Single(await entries.ListAsync(default, default));
        entries.When(substitute => substitute.SetOutcomeAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CockpitTimeEntryOutcome>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())).Do(call =>
        {
            entries.ListAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([offered with { Status = CockpitTimeEntryStatus.Booked }]);
            storage.FailNextSave = true;
        });

        await Assert.ThrowsAsync<IOException>(() => reporter.ReportAsync(request));
        var report = await reporter.ReportAsync(request);

        Assert.Equal(overrun, report.Warnings.Any(warning => warning.Overrun));
        Assert.Equal(!overrun, report.IsGreen);
        Assert.Equal(expectedStatus, report.AgentStatus(EntryId, "portal"));
        await entries.Received(1).SetOutcomeAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(EntryId)), CockpitTimeEntryOutcome.Booked, null, Arg.Any<CancellationToken>());
        var saved = storage.Get<WeekReport>(WeekReport.StorageKey(Monday));
        Assert.NotNull(saved);
        Assert.Equal("booked", saved.Rows.Single(row => row.Row.EntryId == EntryId).Status);
    }

    [Fact]
    public async Task AnotherVerifiedPane_CannotReportForTheRecipientOrWriteAnOutcome()
    {
        var (host, entries, storage) = _Rig("another-pane");
        var reporter = new HoursResultReporter(host);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reporter.ReportAsync(new HoursResultRequest(Monday, [new(WeekOfferMarker.Format(43, Monday, 1), "booked")], [])));

        Assert.Contains("ontvanger", error.Message, StringComparison.Ordinal);
        await entries.DidNotReceive().SetOutcomeAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CockpitTimeEntryOutcome>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        var saved = storage.Get<WeekReport>(WeekReport.StorageKey(Monday));
        Assert.NotNull(saved);
        Assert.Null(Assert.Single(saved.Rows).Status);
    }

    private static (ICockpitHost Host, ICockpitTimeEntries Entries, _Storage Storage) _Rig(string caller, int minutes = 480, int priorBooked = 0)
    {
        var host = Substitute.For<ICockpitHost>();
        var entries = Substitute.For<ICockpitTimeEntries>();
        var storage = new _Storage();
        host.CurrentMcpCaller.Returns(new McpCaller(caller, McpCallerKind.Session));
        host.TimeEntries.Returns(entries);
        host.Storage.Returns(storage);
        entries.ListAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([new CockpitTimeEntry(EntryId, "Portal", new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(2)), TimeSpan.FromMinutes(minutes), "portal", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Offered, OfferedToPaneId: "receiver")]);
        WeekReportedRow[] prior = priorBooked > 0 ? [new(new WeekOfferRow(Monday, 2, priorBooked, Guid.NewGuid(), "other"), "receiver", 480, "booked", OfferId: Guid.NewGuid())] : [];
        storage.Set(WeekReport.StorageKey(Monday), new WeekReport([.. prior, new(new WeekOfferRow(Monday, 1, minutes, EntryId, "portal"), "receiver", 480, OfferId: Guid.NewGuid(), KnownBookedAtOffer: priorBooked)], null, null, 0));
        return (host, entries, storage);
    }

    private sealed class _Storage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];
        public bool FailNextSave { get; set; }
        public T? Get<T>(string key) => _values.GetValueOrDefault(key) is T value ? value : default;
        public void Set<T>(string key, T value)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Storage was interrupted after the host outcome was saved.");
            }

            _values[key] = value;
        }
    }
}
