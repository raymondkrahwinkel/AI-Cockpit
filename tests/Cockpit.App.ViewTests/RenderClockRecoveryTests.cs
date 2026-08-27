using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

// A control that invalidates itself from LayoutUpdated — the shape of the real defect, where a ScrollViewer's
// ScrollChanged (raised from LayoutUpdated, so after the pass has ended) queues another pass. Each one counts
// towards the 153 MediaContext.FireInvokeOnRenderCallbacks cuts a frame off at.
file sealed class SelfInvalidatingControl : Control
{
    public bool Looping { get; set; } = true;

    public int Measures { get; private set; }

    public SelfInvalidatingControl()
    {
        LayoutUpdated += (_, _) =>
        {
            if (Looping)
            {
                InvalidateMeasure();
            }
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Measures++;
        return new Size(10, 10);
    }
}

[Collection("avalonia")]
public sealed class RenderClockRecoveryTests
{
    private static void _Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Drives a layout loop until Avalonia cuts the frame, then checks that layout is dead afterwards and that
    /// asking a window for an animation frame — what the global net does on AC-1104 — brings it back.
    /// </summary>
    [Fact]
    public void AfterAnInfiniteLayoutLoopIsCutOff_AskingForAnAnimationFrame_RestartsLayout() => HeadlessAvalonia.Run(() =>
    {
        var looper = new SelfInvalidatingControl();
        var bystander = new SelfInvalidatingControl { Looping = false };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { looper, bystander } } };
        window.Show();

        // Caught the way the global net catches it, and judged by the same decision, so this test also guards the
        // message RenderClockRecovery matches on: change it and the recovery below stops happening.
        Exception? caught = null;
        void OnUnhandled(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            caught = e.Exception;
            e.Handled = true;
        }

        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            for (var frame = 0; frame < 10 && caught is null; frame++)
            {
                _Frame();
            }

            Assert.True(
                caught is not null
                && RenderClockRecovery.ShouldRecover(caught, RenderClockRecovery.MinimumInterval),
                $"Avalonia raised {caught?.GetType().Name ?? "nothing"} rather than the cut-off RenderClockRecovery "
                + "looks for, so this test is not exercising AC-1104's case");

            // Stop the loop, so what follows measures the render clock rather than the loop throwing again.
            looper.Looping = false;

            var before = bystander.Measures;
            bystander.InvalidateMeasure();
            for (var frame = 0; frame < 5; frame++)
            {
                _Frame();
            }

            Assert.Equal(before, bystander.Measures);

            if (RenderClockRecovery.ShouldRecover(caught, RenderClockRecovery.MinimumInterval))
            {
                window.RequestAnimationFrame(_ => { });
            }

            for (var frame = 0; frame < 5; frame++)
            {
                _Frame();
            }

            Assert.True(
                bystander.Measures > before,
                "layout never resumed after the animation-frame request: the cut-off left the render clock stalled "
                + "and the recovery in Program's global net does not lift it");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
        }
    });
}
