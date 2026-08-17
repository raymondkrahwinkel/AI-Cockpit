using System.Reflection;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Cockpit.App.ViewTests;

// AC-882 pins the contract the whole transcript-teardown fix rests on: a forced compositor commit is still
// processed after the app has been idle long enough for the render loop to park its clock.
//
// Avalonia 12 parks the render clock on every platform once no IRenderLoopTask wants another tick
// (DefaultRenderLoop owns that sleep/wake state machine; ServerCompositor.RenderCore returns false to ask for it,
// and SleepLoopRenderTimer parks on an event). What brings it back is the commit itself:
// ServerCompositor.EnqueueBatch calls IRenderLoop.Wakeup. Lose that edge — an Avalonia change, a downgrade to the
// pre-12 architecture AC-882 was written against — and SessionView.OnDetachedFromVisualTree's RequestCommitAsync
// becomes a no-op on a pipeline that never processes the batch, which is the permanent half of the leak.
//
// The parked state itself is only reachable by reflection (DefaultRenderLoop.Timer is internal), and asserting it
// is what makes this decisive rather than merely green: without it a commit that completes proves nothing, since
// a clock that never parked would satisfy it too.
[Collection("avalonia")]
public sealed class RenderClockWakeTests
{
    // CommitGraceTicks is 10, so at 60fps the loop stops asking for ticks ~170ms after the last commit.
    private static readonly TimeSpan LongEnoughToPark = TimeSpan.FromMilliseconds(600);

    // Two orders of magnitude over a healthy round trip, and well under RenderClockHeartbeat.StallAfter, so this
    // fails on a broken wake edge rather than on a slow CI runner.
    private static readonly TimeSpan CommitBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AForcedCommit_IsStillProcessedAfterTheAppHasBeenIdle()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new Window { Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();

            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;

            // Settle first, so the idle below starts from a quiet pipeline rather than from the show.
            await compositor.RequestCommitAsync();
            await Task.Delay(LongEnoughToPark);

            Assert.True(
                _RenderClockIsParked(compositor),
                $"the render clock was still ticking after {LongEnoughToPark.TotalMilliseconds:0}ms of idle, so this "
                + "test cannot prove anything about waking it — raise LongEnoughToPark or check CommitGraceTicks");

            var commit = compositor.RequestCommitAsync();
            var first = await Task.WhenAny(commit, Task.Delay(CommitBudget));

            window.Close();

            Assert.True(
                ReferenceEquals(first, commit),
                $"a forced commit went unprocessed for {CommitBudget.TotalSeconds:0}s while the render clock was "
                + "parked — the commit no longer wakes it (ServerCompositor.EnqueueBatch → IRenderLoop.Wakeup)");
        });
    }

    // Compositor.Loop, DefaultRenderLoop.Timer and IRenderTimer.Tick are all invisible outside Avalonia, so this
    // is the only route to the one fact that gives the test teeth. A null Tick is how the render loop tells a
    // timer to stop, which makes it the readable form of "parked".
    private static bool _RenderClockIsParked(Compositor compositor)
    {
        var loop = _Read(compositor, "Loop");
        var timer = _Read(loop, "Timer");

        return _Property(timer, "Tick").GetValue(timer) is null;
    }

    private static object _Read(object owner, string property) =>
        _Property(owner, property).GetValue(owner)
        ?? throw new InvalidOperationException($"{owner.GetType().FullName}.{property} was null.");

    // Throws rather than degrading to a skip: a parked state this test silently stopped asserting would leave it
    // passing on the strength of nothing at all.
    private static PropertyInfo _Property(object owner, string property) =>
        owner.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"{owner.GetType().FullName} no longer exposes {property} — re-derive how this test observes the parked render clock.");
}
