using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

// The 31-08 shape, and deliberately not LayoutLoopReportTests' looper: this one dirties itself again on the next
// render tick rather than inside the pass, so every pass converges and Avalonia's own cut-off never fires. That
// is what let the freeze run eleven minutes with no layout-loops.log to show for it.
file sealed class TickLooper : Decorator
{
    public int Measures { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        Measures++;
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(_ => InvalidateMeasure());
        }

        return base.MeasureOverride(availableSize);
    }
}

file sealed class QuietProbe : Control
{
    protected override Size MeasureOverride(Size availableSize) => new(10, 10);
}

[Collection("avalonia")]
public sealed class LayoutLoopGuardTests
{
    // How many render ticks the loop gets to settle on its own. Twenty times the guard's own bound, so "it never
    // converges" is a reading rather than an impatient assertion.
    private const int Frames = 60;

    private static void _Frame()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    // In the running cockpit the sample is a Send-priority post, which on a thread at 100% lands between the
    // re-arm and the pass it feeds. Headless has no such gap between jobs, so the sample is taken from a render
    // callback queued after the looper's own — the same moment, reached the only way this platform offers.
    private static IReadOnlyList<Layoutable> _FrameAndSample(Window window)
    {
        IReadOnlyList<Layoutable> sample = [];
        TopLevel.GetTopLevel(window)!.RequestAnimationFrame(_ => sample = LayoutLoopReport.Collect([window]));
        _Frame();
        return sample;
    }

    [Fact]
    public void WithTheGuardOn_TheSameLoopIsCutAtTheConfiguredSampleAndTheTreeSettles() => HeadlessAvalonia.Run(() =>
    {
        var looper = new TickLooper { Name = "runaway", Child = new TextBlock { Text = "content" } };
        var bystander = new QuietProbe();
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { looper, bystander } } };
        var guard = new LayoutLoopGuard(3);

        window.Show();
        try
        {
            Layoutable? cut = null;
            for (var frame = 0; frame < Frames && cut is null; frame++)
            {
                cut = guard.Observe(_FrameAndSample(window));
            }

            Assert.Same(looper, cut);

            // The record an operator gets a ticket out of: the subtree named, with the path it sits on.
            Assert.Contains(
                LayoutLoopReport.Group([cut!]),
                entry => entry.Contains($"{nameof(TickLooper)}#runaway", StringComparison.Ordinal));

            LayoutLoopGuard.Cut(cut!);
            for (var frame = 0; frame < 5; frame++)
            {
                _Frame();
            }

            var measuresAfterTheCut = looper.Measures;
            _Frame();

            Assert.Equal(measuresAfterTheCut, looper.Measures);
            Assert.Empty(LayoutLoopReport.Collect([window]));
            Assert.False(looper.IsVisible);
            Assert.True(bystander.IsVisible, "only the subtree that would not settle should be gone");
        }
        finally
        {
            window.Close();
        }
    });

    // The half that costs nothing while it works and everything when it is wrong: a pass that is getting
    // somewhere must never be cut, however many samples it takes to get there.
    [Fact]
    public void APassThatKeepsShrinkingItsDirtySetIsNeverCut() => HeadlessAvalonia.Run(() =>
    {
        var guard = new LayoutLoopGuard(3);
        var window = new Window { Width = 400, Height = 300 };
        var panel = new StackPanel();
        var slow = Enumerable.Range(0, 8).Select(_ => new QuietProbe()).ToArray();
        foreach (var probe in slow)
        {
            panel.Children.Add(probe);
        }

        window.Content = panel;
        window.Show();
        try
        {
            for (var remaining = slow.Length; remaining > 0; remaining--)
            {
                Assert.Null(guard.Observe(slow.Take(remaining).Cast<Layoutable>().ToArray()));
            }

            // The last shrinking sample is already the first of a streak, so a set that stops shrinking still
            // needs two more before the bound is reached. Standing still is what gets cut, not being slow.
            Assert.Null(guard.Observe([slow[0]]));
            Assert.Same(slow[0], guard.Observe([slow[0]]));
        }
        finally
        {
            window.Close();
        }
    });
}
