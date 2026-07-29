using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Usage;
using NSubstitute;

namespace Cockpit.Core.Tests.Usage;

/// <summary>
/// That a session actually writes what it spends to the trail (AC-251). The meter itself was already covered;
/// what was missing — and what made yesterday's spend unrecoverable — is that nothing carried it out of memory.
/// Recording happens per completed turn rather than at close, so a run that crashes still leaves its figure.
/// </summary>
public class SessionUsageRecordingTests
{
    private sealed class RecordingUsageHistory : IUsageHistory
    {
        public List<UsageSnapshot> Recorded { get; } = [];

        public Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Recorded.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UsageSnapshot>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsageSnapshot>>(Recorded);
    }

    // A trail whose write does not finish until it is released, so a test can hold the last turn's record in flight.
    private sealed class BlockingUsageHistory(Task gate) : IUsageHistory
    {
        public async Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default) => await gate;

        public Task<IReadOnlyList<UsageSnapshot>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsageSnapshot>>([]);
    }

    private static SessionViewModel _Session(IUsageHistory history) =>
        new(Substitute.For<ISessionManager>(), usageHistory: history);

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static TurnCompleted _Turn(TokenUsage? usage, double? costUsd) =>
        new() { SessionId = "s-1", Subtype = "success", Result = "done", IsError = false, Usage = usage, TotalCostUsd = costUsd };

    // What lets the meter simply follow the newest reported cost (AC-481): a panel drives one CLI process for
    // its whole life, so there is never an earlier process whose spend would have to be carried over. Pinned
    // here because SessionUsageMeter's own documentation leans on it.
    [Fact]
    public async Task ASecondStart_IsRefused_SoOnePanelDrivesOneProcess()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        var profile = new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude"));
        var vm = new SessionViewModel(new SessionManager(_FactoryFor(driver)));

        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        await vm.StartConfiguredAsync(profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await vm.DisposeAsync();
    }

    [Fact]
    public void ACompletedTurn_WritesTheSessionsRunningTotals_ToTheTrail()
    {
        var history = new RecordingUsageHistory();
        var session = _Session(history);
        session.ActiveProfileLabel = "raymond";
        session.SelectedModel = new ModelOption("Opus", "opus");

        session._AccumulateUsage(_Turn(new TokenUsage(120, 40, 9_000, 300), 0.25));

        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(session.PaneId, snapshot.PaneId);
        Assert.Equal(120, snapshot.InputTokens);
        Assert.Equal(40, snapshot.OutputTokens);
        Assert.Equal(9_000, snapshot.CacheReadInputTokens);
        Assert.Equal(300, snapshot.CacheCreationInputTokens);
        Assert.Equal(0.25, snapshot.TotalCostUsd);
        Assert.Equal(1, snapshot.Turns);
        Assert.Equal("raymond", snapshot.ProfileLabel);
        Assert.Equal("opus", snapshot.Model);
    }

    [Fact]
    public void EachTurn_WritesAgain_WithTheTotalSoFar()
    {
        var history = new RecordingUsageHistory();
        var session = _Session(history);

        // The cost figures rise the way a real session reports them — each result states what the session has
        // cost so far, not what its last turn cost (AC-481). The pair used to fall (0.10 then 0.05), which the
        // old summing meter added up to the same 0.15 for the wrong reason.
        session._AccumulateUsage(_Turn(new TokenUsage(100, 10, 0, 0), 0.10));
        session._AccumulateUsage(_Turn(new TokenUsage(50, 5, 0, 0), 0.15));

        // Cumulative, not per-turn: the newest record is the session's total, which is what makes a record per
        // turn a crash-proof snapshot rather than something a reader has to add up.
        Assert.Equal(2, history.Recorded.Count);
        Assert.Equal(150, history.Recorded[1].InputTokens);
        Assert.Equal(15, history.Recorded[1].OutputTokens);
        Assert.Equal(0.15, history.Recorded[1].TotalCostUsd, precision: 10);
        Assert.Equal(2, history.Recorded[1].Turns);
    }

    [Fact]
    public void ATurnThatReportedNothing_LeavesNoRecord_RatherThanARowOfZeroes()
    {
        var history = new RecordingUsageHistory();
        var session = _Session(history);

        session._AccumulateUsage(_Turn(usage: null, costUsd: null));

        Assert.Empty(history.Recorded);
    }

    [Fact]
    public void AnOperatorsOwnSession_IsRecordedAsInteractive_WithNoRun()
    {
        var history = new RecordingUsageHistory();
        var session = _Session(history);

        session._AccumulateUsage(_Turn(new TokenUsage(10, 1, 0, 0), 0.01));

        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(UsageRunKind.Interactive, snapshot.RunKind);
        Assert.Null(snapshot.RunId);
    }

    [Fact]
    public void ASessionEmbeddedForARun_CarriesThatRun_SoItsSessionsAddUp()
    {
        var history = new RecordingUsageHistory();
        var session = _Session(history);
        // What CockpitViewModel.Embed stamps from the embedder's request.
        session.RunKind = UsageRunKind.Embedded;
        session.RunId = "run-7";
        session.RunLabel = "AC-251 - persist usage";

        session._AccumulateUsage(_Turn(new TokenUsage(10, 1, 0, 0), 0.01));

        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(UsageRunKind.Embedded, snapshot.RunKind);
        Assert.Equal("run-7", snapshot.RunId);
        Assert.Equal("AC-251 - persist usage", snapshot.RunLabel);
    }

    [Fact]
    public async Task ClosingASession_WaitsForItsLastTurnsWrite_RatherThanRacingTheProcessOut()
    {
        // The write is not awaited per turn, so a session closing right behind its last turn would otherwise take
        // the app down before that record reached disk — losing exactly the figure this ticket exists to keep.
        var gate = new TaskCompletionSource();
        var session = _Session(new BlockingUsageHistory(gate.Task));

        session._AccumulateUsage(_Turn(new TokenUsage(10, 1, 0, 0), 0.01));

        var closing = session.DisposeAsync();
        Assert.False(closing.IsCompleted);

        gate.SetResult();
        await closing;
    }

    [Fact]
    public async Task ClosingASessionThatNeverSpentAnything_DoesNotWaitOnAWriteThatWasNeverMade()
    {
        var session = _Session(new BlockingUsageHistory(new TaskCompletionSource().Task));

        // No turn, so no record in flight: closing must not hang on a gate nobody is going to open.
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ASessionWithNoTrail_StillKeepsItsMeter()
    {
        // The design-time and test graphs have no usage history; the header must not lose its meter over it.
        var session = new SessionViewModel(Substitute.For<ISessionManager>());

        session._AccumulateUsage(_Turn(new TokenUsage(10, 1, 0, 0), 0.01));

        Assert.True(session.HasUsage);
    }
}
