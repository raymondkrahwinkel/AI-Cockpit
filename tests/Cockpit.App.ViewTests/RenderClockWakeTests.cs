using System.Reflection;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Cockpit.App.ViewTests;

// AC-882: a forced compositor commit must still be processed once the render loop has parked its clock — the
// contract SessionView.OnDetachedFromVisualTree's teardown commit rests on. Asserting the parked state is what
// gives this teeth; without it a commit that completes proves nothing.
[Collection("avalonia")]
public sealed class RenderClockWakeTests
{
    // Poll rather than sleep a fixed span: the clock is process-wide, and windows other tests in this collection
    // left open keep waking it. A single sleep-then-look reads whatever moment it lands on and fails at random
    // (measured: green alone and in one full run, red in the next). Polling waits for a parked moment instead.
    private static readonly TimeSpan ParkBudget = TimeSpan.FromSeconds(20);

    // Two orders of magnitude over a healthy round trip, and well under RenderClockHeartbeat.StallAfter, so this
    // fails on a broken wake edge rather than on a slow CI runner.
    private static readonly TimeSpan CommitBudget = TimeSpan.FromSeconds(5);

    // AC-1076: forcing SkipException.ForSkip on [Fact] reported [FAIL], not skip, under xunit 2.9.3 + xunit.runner.visualstudio 3.1.4.
    // Recheck after either package upgrades.
    [SkippableFact]
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

            var waited = TimeSpan.Zero;
            while (!_RenderClockIsParked(compositor) && waited < ParkBudget)
            {
                await Task.Delay(50);
                waited += TimeSpan.FromMilliseconds(50);
            }

            Skip.IfNot(
                _RenderClockIsParked(compositor),
                $"the render clock was still ticking after {waited.TotalMilliseconds:0}ms of idle, so this test "
                + "cannot prove anything about waking it — a neighbouring test is keeping the compositor busy");

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
