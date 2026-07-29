using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Usage;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-398: a TTY session's tokens land in the usage trail the same way an SDK session's already do
/// (<c>Cockpit.Core.Tests.Usage.SessionUsageRecordingTests</c>) — fed by the transcript tail instead of the SDK
/// event stream, since a TTY panel has no parsed event stream of its own.
/// </summary>
/// <remarks>
/// Here rather than in the unit tests because the status tail marshals every reading onto the UI thread the same
/// way <see cref="SessionPanelViewModel.SessionStatus"/> already does — without a pumped dispatcher the posted
/// work is never run, and the recording would never happen, in a test or in the app (see <see cref="HeadlessAvalonia"/>).
/// Waited for by polling, not <c>Task.WaitAsync</c>: that timeout mechanism resumes its continuation off the
/// dispatcher's own synchronization context, so it does not pump the queued <c>Dispatcher.UIThread.Post</c> work
/// this path depends on — the same reason <c>TtyReadAloudTests</c>/<c>ScheduledResumeTimerTests</c> poll instead.
/// Asserted with xunit's own Assert: the fluent library its neighbours use is on its way out (AC-372).
/// </remarks>
[Collection("avalonia")]
public class TtyUsageRecordingTests
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

    /// <summary>A trail whose write does not finish until it is released, so a test can hold the last turn's record in flight — same idiom as SessionUsageRecordingTests.BlockingUsageHistory.</summary>
    private sealed class BlockingUsageHistory(Task gate) : IUsageHistory
    {
        public TaskCompletionSource WriteStarted { get; } = new();

        public async Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await gate;
        }

        public Task<IReadOnlyList<UsageSnapshot>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UsageSnapshot>>([]);
    }

    private static readonly SessionProfile Work = new("work", ClaudePluginProfile.Create("/config/work", null));

    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    /// <summary>A transcript reader whose ReadActivityAsync yields the given readings, then idles until cancelled — like a live tail with nothing further to say.</summary>
    private static ISessionTranscriptReader _ReaderYielding(params SessionTranscriptActivity[] readings)
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.SnapshotTranscripts(Arg.Any<SessionProfile?>()).Returns(new HashSet<string>());
        reader.ReadActivityAsync(Arg.Any<SessionProfile?>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _YieldThenWaitForCancellation(readings, callInfo.ArgAt<CancellationToken>(2)));
        return reader;
    }

    private static async IAsyncEnumerable<SessionTranscriptActivity> _YieldThenWaitForCancellation(
        IReadOnlyList<SessionTranscriptActivity> readings, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var reading in readings)
        {
            yield return reading;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should become true within the poll window");
    }

    [Fact]
    public Task ATurnThatUsedATool_SumsEveryAssistantLinesUsage_AsOneTurn() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The bug this guards: a turn that calls a tool writes several assistant lines before it completes, each
        // with its own usage — only summing the very last one would silently undercount every tool-using turn,
        // which is most of them.
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.Busy, "tool-use line", new TokenUsage(100, 20, 0, 0)),
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "final line", new TokenUsage(50, 200, 10, 5)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        await _WaitUntilAsync(() => history.Recorded.Count > 0);
        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(150, snapshot.InputTokens);
        Assert.Equal(220, snapshot.OutputTokens);
        Assert.Equal(10, snapshot.CacheReadInputTokens);
        Assert.Equal(5, snapshot.CacheCreationInputTokens);
        Assert.Equal(1, snapshot.Turns);
    });

    [Fact]
    public Task ATurnsUsage_IsRecordedWithNoCost_RatherThanAGuessedFigure() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The CLI's on-disk transcript reports tokens per assistant message but never a cost field (unlike the
        // SDK path's stream-json result event) — the cockpit does not compute one itself from tokens.
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "line", new TokenUsage(10, 5, 0, 0)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        await _WaitUntilAsync(() => history.Recorded.Count > 0);
        Assert.Equal(0, Assert.Single(history.Recorded).TotalCostUsd);
    });

    [Fact]
    public Task ASecondCompletedTurn_WritesAgain_WithTheRunningTotalSoFar() => HeadlessAvalonia.RunAsync(async () =>
    {
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "first", new TokenUsage(10, 5, 0, 0)),
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "second", new TokenUsage(20, 8, 0, 0)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        await _WaitUntilAsync(() => history.Recorded.Count >= 2);
        Assert.Equal(30, history.Recorded[1].InputTokens);
        Assert.Equal(13, history.Recorded[1].OutputTokens);
        Assert.Equal(2, history.Recorded[1].Turns);
    });

    [Fact]
    public Task ItIsAlwaysRecordedAsInteractiveWithNoRun() => HeadlessAvalonia.RunAsync(async () =>
    {
        // Nothing embeds a TTY session today (CockpitViewModel.Embed only ever builds an SDK SessionViewModel) —
        // pinning this documents that, rather than leaving it an untested assumption.
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "line", new TokenUsage(10, 5, 0, 0)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        await _WaitUntilAsync(() => history.Recorded.Count > 0);
        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(UsageRunKind.Interactive, snapshot.RunKind);
        Assert.Null(snapshot.RunId);
    });

    [Fact]
    public Task ATurnWithNoUsageReading_LeavesNoRecord_RatherThanARowOfZeroes() => HeadlessAvalonia.RunAsync(async () =>
    {
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(new SessionTranscriptActivity(SessionActivity.TurnComplete, "line", Usage: null));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        // Give the (correctly silent) tail a moment to have run, since there is no positive signal to poll for.
        await Task.Delay(200);
        Assert.Empty(history.Recorded);
    });

    [Fact]
    public Task ASyntheticTurnCompleteWithNoRawLine_DoesNotFlushASecondTime() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The bug this guards: the reader re-emits TurnComplete with no RawLine once a background sub-agent's
        // work ends (a keep-alive reading, not a second real turn) — flushing on that too would write a
        // duplicate row with the same totals and double the Turns count for one real turn.
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "line", new TokenUsage(10, 5, 0, 0)),
            new SessionTranscriptActivity(SessionActivity.TurnComplete, RawLine: null, Usage: null));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        vm.OnLaunchSucceeded();

        await _WaitUntilAsync(() => history.Recorded.Count > 0);
        // Give the synthetic reading a chance to have reached the tail too before asserting nothing further landed.
        await Task.Delay(200);
        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(1, snapshot.Turns);
    });

    [Fact]
    public Task DisposeAsync_FlushesAnUnterminatedTurnsTokens_RatherThanDroppingThem() => HeadlessAvalonia.RunAsync(async () =>
    {
        // The bug this guards: a pane closed (or its CLI killed) mid-turn never sees its terminating end_turn
        // line, so the tokens accumulated so far would otherwise never be recorded at all.
        var history = new RecordingUsageHistory();
        var reader = _ReaderYielding(new SessionTranscriptActivity(SessionActivity.Busy, "tool-use line", new TokenUsage(10, 5, 0, 0)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: history);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.OnLaunchSucceeded();

        // The one reading above has no terminating TurnComplete — wait for it to have been processed, then close.
        await Task.Delay(200);
        await vm.DisposeAsync();

        var snapshot = Assert.Single(history.Recorded);
        Assert.Equal(10, snapshot.InputTokens);
        Assert.Equal(5, snapshot.OutputTokens);
        Assert.Equal(1, snapshot.Turns);
    });

    [Fact]
    public Task DisposeAsync_WaitsForTheLastTurnsWrite_RatherThanRacingTheProcessOut() => HeadlessAvalonia.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource();
        var blockingHistory = new BlockingUsageHistory(gate.Task);
        var reader = _ReaderYielding(
            new SessionTranscriptActivity(SessionActivity.TurnComplete, "line", new TokenUsage(10, 5, 0, 0)));
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader, usageHistory: blockingHistory);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.OnLaunchSucceeded();
        await _WaitUntilAsync(() => blockingHistory.WriteStarted.Task.IsCompleted);

        var closing = vm.DisposeAsync();
        Assert.False(closing.IsCompleted, "the write was still in flight when Dispose was called");

        gate.SetResult();
        await closing;
    });
}
