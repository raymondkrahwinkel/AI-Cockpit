using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Tests;

public sealed class WeekOfferTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Prompt_FormatsOpenRowsAndKeepsFixedRulesOutsideEditableInstructions()
    {
        var ownId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var proposal = new WeekProposal(
            [
                new ProposalDay(Monday, TimeSpan.FromHours(8), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)),
                new ProposalDay(Monday.AddDays(1), TimeSpan.FromHours(8), TimeSpan.FromMinutes(75), TimeSpan.FromHours(3)),
            ],
            [
                new ProposalLine("Overleg klant YYY", "syn-portal", ownId, "Portal", [new ProposalCell(Monday, TimeSpan.FromMinutes(30), CockpitTimeEntryStatus.Open, "syn-portal")]),
                new ProposalLine("Portal", "syn-portal", null, "SYN-12", [new ProposalCell(Monday.AddDays(1), TimeSpan.FromMinutes(75), CockpitTimeEntryStatus.Open, "syn-portal", Description: "SYN-12")]),
            ]);

        var draft = WeekOfferPrompt.Build(proposal, 43, "Gebruik de klantnaam uit de omschrijving.");

        Assert.Equal(
            """
            Boek dit urenvoorstel voor week 43.

            Voorstel
            | Datum | Duur | Project | Omschrijving | Soort |
            | --- | --- | --- | --- | --- |
            | ma 19-10-2026 | 0:30 | Portal | Overleg klant YYY [cockpit:wk43-ma-1] | eigen |
            | di 20-10-2026 | 1:15 | Portal | SYN-12 [cockpit:wk43-di-1] |  |

            Werkschema (harde grens)
            | Datum | Schema |
            | --- | --- |
            | ma 19-10-2026 | 8:00 |
            | di 20-10-2026 | 8:00 |

            Boek per dag nooit meer dan het schema, met wat er al geboekt staat meegerekend. Komt een dag erboven, of staat er al meer dan het schema: boek die dag niets en leg hem eerst voor.

            Maak eerst je eigen boekingsvoorstel (per regel datum, duur, contact of project, omschrijving; per dag het totaal tegenover het schema), leg het voor en vraag expliciet akkoord. Schrijf niets voordat je dat akkoord hebt.

            Instructies
            Gebruik de klantnaam uit de omschrijving.
            """.ReplaceLineEndings("\n"),
            draft.Prompt);
        Assert.Equal([ownId], draft.OwnEntryIds);
        Assert.Equal("[cockpit:wk43-di-1]", Assert.Single(draft.ProjectCells).Marker);

        var withoutInstructions = WeekOfferPrompt.Build(proposal, 43, string.Empty).Prompt;
        Assert.Contains(WeekOfferPrompt.HardBoundary, withoutInstructions, StringComparison.Ordinal);
        Assert.Contains(WeekOfferPrompt.PriorApproval, withoutInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Instructies\n", withoutInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_MarksOnlyAfterDeliveryAndLosesNothingOnCancelRefusalOrAmbiguousNewSession()
    {
        var existing = _Rig([new OpenCockpitSession("pane-a", "Werk", "Codex", "Idle")]);
        var request = _Request(WeekOfferDestination.Session("pane-a", "Werk"));

        var sent = await existing.Sender.SendAsync(request);

        Assert.Equal("pane-a", sent.PaneId);
        Assert.Equal(request.Prompt, existing.Actions.SentPrompt);
        Assert.Equal((request.OwnEntryIds, Now, "pane-a"), existing.Entries.Marked);
        var frozen = Assert.Single(existing.Storage.Get<FrozenProjectCell[]>(FrozenProjectCell.StorageKey(Monday)) ?? []);
        Assert.Equal((Monday, "syn-portal", 60, "SYN-12", "[cockpit:wk43-ma-1]", Now, "pane-a"),
            (frozen.Day, frozen.ProjectId, frozen.Minutes, frozen.Description, frozen.Marker, frozen.OfferedAt, frozen.PaneId));

        var refused = _Rig([new OpenCockpitSession("pane-a", "Werk")]);
        refused.Actions.RefuseSend = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => refused.Sender.SendAsync(request));
        _AssertUntouched(refused);

        var cancelled = _Rig([new OpenCockpitSession("pane-a", "Werk")]);
        _AssertUntouched(cancelled);

        var unique = _Rig([]);
        unique.Actions.AfterStart = name => unique.Sessions.Items = [new OpenCockpitSession("pane-new", name, "Codex", "Idle")];
        var started = await unique.Sender.SendAsync(_Request(WeekOfferDestination.Profile("Codex")));
        Assert.Equal("pane-new", started.PaneId);
        Assert.Equal("pane-new", unique.Entries.Marked?.PaneId);

        var ambiguous = _Rig([]);
        ambiguous.Actions.AfterStart = name => ambiguous.Sessions.Items =
        [
            new OpenCockpitSession("pane-one", name),
            new OpenCockpitSession("pane-two", name),
        ];
        await Assert.ThrowsAsync<InvalidOperationException>(() => ambiguous.Sender.SendAsync(_Request(WeekOfferDestination.Profile("Codex"))));
        _AssertUntouched(ambiguous);
    }

    private static WeekOfferSendRequest _Request(WeekOfferDestination destination) =>
        new(
            destination,
            "exact preview",
            Monday,
            43,
            [Guid.Parse("22222222-2222-2222-2222-222222222222")],
            [new WeekOfferProjectCell(Monday, "syn-portal", 60, "SYN-12", "[cockpit:wk43-ma-1]")],
            "cockpit");

    private static _Fixture _Rig(IReadOnlyList<OpenCockpitSession> sessions)
    {
        var actions = new _Actions();
        var observer = new _Sessions { Items = sessions };
        var entries = new _Entries();
        var storage = new _Storage();
        return new _Fixture(new WeekOfferSender(actions, observer, entries, storage, new _Clock()), actions, observer, entries, storage);
    }

    private static void _AssertUntouched(_Fixture fixture)
    {
        Assert.Null(fixture.Entries.Marked);
        Assert.Empty(fixture.Storage.Values);
    }

    private sealed record _Fixture(WeekOfferSender Sender, _Actions Actions, _Sessions Sessions, _Entries Entries, _Storage Storage);

    private sealed class _Actions : ICockpitActions
    {
        public bool RefuseSend { get; set; }
        public string? SentPrompt { get; private set; }
        public Action<string>? AfterStart { get; set; }

        public Task SendPromptToSessionAsync(string paneId, string prompt, string reason)
        {
            if (RefuseSend)
            {
                throw new InvalidOperationException("The operator declined.");
            }

            SentPrompt = prompt;
            return Task.CompletedTask;
        }

        public Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName)
        {
            SentPrompt = prompt;
            AfterStart?.Invoke(sessionName ?? string.Empty);
            return Task.FromResult(sessionName ?? string.Empty);
        }
    }

    private sealed class _Sessions : ICockpitSessionObserver
    {
        public IReadOnlyList<OpenCockpitSession> Items { get; set; } = [];
        public IReadOnlyList<OpenCockpitSession> OpenSessions => Items;

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }
    }

    private sealed class _Entries : ICockpitTimeEntries
    {
        public (IReadOnlyCollection<Guid> Ids, DateTimeOffset At, string PaneId)? Marked { get; private set; }

        public Task MarkOfferedAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset offeredAt, string paneId, CancellationToken cancellationToken = default)
        {
            Marked = (ids, offeredAt, paneId);
            return Task.CompletedTask;
        }

        public Task<CockpitTimeEntry> AddAsync(string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CockpitTimeEntrySource source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CockpitTimeEntry> UpdateAsync(Guid id, string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CockpitTimeEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CockpitRunningStopwatch> StartStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CockpitRunningStopwatch> UpdateStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CockpitTimeEntry> StopStopwatchAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CockpitRunningStopwatch?> GetRunningStopwatchAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOutcomeAsync(IReadOnlyCollection<Guid> ids, CockpitTimeEntryOutcome outcome, string? reason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class _Storage : IPluginStorage
    {
        public Dictionary<string, object?> Values { get; } = [];
        public T? Get<T>(string key) => Values.GetValueOrDefault(key) is T value ? value : default;
        public void Set<T>(string key, T value) => Values[key] = value;
    }

    private sealed class _Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
