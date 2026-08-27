using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1204: while the UI thread is starved, <see cref="SessionEventQueue"/> must not pile up one entry per
/// enqueued event — it folds an unbroken run of the same delta on arrival instead of waiting for a drain that a
/// starved thread never gets to run.
/// </summary>
/// <remarks>
/// Same arrangement as AC-1138's T2/T3 (<c>Ac1138UiThreadDeadlineTests</c>): a starved thread proves it kept
/// working rather than blocking (<see cref="StarvedDispatcher.Rounds"/>), and a quiet-thread control in the same
/// run is what makes a passing starved test mean something — without it, a queue that silently dropped
/// everything would pass too.
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1204SessionEventQueueBackpressureTests
{
    private const string SessionId = "ac-1204-session";
    private const int TotalEvents = 10_000;

    [Fact]
    public Task EnqueueingWhileTheUiThreadIsStarvedAtRender_StaysBounded_AndAppliesAfterRecovery() =>
        _StarvedEnqueueStaysBounded(DispatcherPriority.Render);

    [Fact]
    public Task EnqueueingWhileTheUiThreadIsStarvedAtLoaded_StaysBounded_AndAppliesAfterRecovery() =>
        _StarvedEnqueueStaysBounded(DispatcherPriority.Loaded);

    /// <summary>The silent positive control: a quiet UI thread applies one event within a few ms, in the same run.</summary>
    [Fact]
    public async Task TheSameQueueOnAQuietUiThread_AppliesOneEventWithinMilliseconds()
    {
        var applied = new ConcurrentQueue<SessionEvent>();
        var queue = new SessionEventQueue(applied.Enqueue);

        var clock = Stopwatch.StartNew();
        await Task.Run(() => queue.Enqueue(_Delta("x")));

        while (applied.IsEmpty && clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        clock.Stop();

        Assert.Single(applied);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(200), $"a free UI thread took {clock.Elapsed}");
    }

    private static async Task _StarvedEnqueueStaysBounded(DispatcherPriority priority)
    {
        var applied = new ConcurrentQueue<SessionEvent>();
        var queue = new SessionEventQueue(applied.Enqueue);

        using (var starver = StarvedDispatcher.Start(priority))
        {
            // Let the starvation loop actually take over the thread before flooding it — otherwise the first
            // enqueue's drain could win the race and apply before starvation ever kicks in.
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            await Task.Run(() =>
            {
                for (var i = 0; i < TotalEvents; i++)
                {
                    queue.Enqueue(_Delta("x"));
                }
            });

            await Task.Delay(TimeSpan.FromMilliseconds(800));

            Assert.True(starver.Rounds > 10, $"the thread has to have kept working, not blocked; rounds={starver.Rounds}");
            Assert.True(applied.IsEmpty, $"nothing should apply while the drain can't get a turn; applied={applied.Count}");
            Assert.True(
                queue.PendingCount <= 2,
                $"an unbroken run of the same delta must fold on arrival instead of piling up; pending={queue.PendingCount}");
        }

        // Recovery: once the starver stops reposting, the one Default-priority drain already queued gets its turn.
        // Same trick as AC-1138's T4 — a Background-priority hop only runs once everything at Default has.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.False(applied.IsEmpty, "the folded event(s) should have applied once the thread recovered");
        Assert.Equal(TotalEvents, applied.OfType<AssistantTextDelta>().Sum(e => e.Text.Length));
    }

    private static AssistantTextDelta _Delta(string text) =>
        new() { SessionId = SessionId, BlockIndex = 0, Text = text };
}
