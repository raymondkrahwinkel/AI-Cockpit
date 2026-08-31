using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

// The same shape as RenderClockRecoveryTests' looper — a control that invalidates itself from LayoutUpdated, so
// every frame queues another pass until Avalonia cuts one off. Two distinct types, so the report can be shown to
// tell the one that loops apart from the one that merely stands next to it.
file sealed class LoopingProbe : Control
{
    public bool Looping { get; set; } = true;

    public LoopingProbe()
    {
        LayoutUpdated += (_, _) =>
        {
            if (Looping)
            {
                InvalidateMeasure();
            }
        };
    }

    protected override Size MeasureOverride(Size availableSize) => new(10, 10);
}

file sealed class QuietProbe : Control
{
    protected override Size MeasureOverride(Size availableSize) => new(10, 10);
}

[Collection("avalonia")]
public sealed class LayoutLoopReportTests
{
    private static void _Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void WhenALayoutLoopIsCutOff_TheReportNamesTheControlThatLooped() => HeadlessAvalonia.Run(() =>
    {
        var looper = new LoopingProbe { Name = "guilty" };
        var bystander = new QuietProbe();
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { looper, bystander } } };
        window.Show();

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

            Assert.NotNull(caught);
            Assert.True(
                RenderClockRecovery.IsCutOff(caught),
                $"Avalonia raised {caught.GetType().Name} rather than the layout-loop cut-off, so this test is not "
                + "exercising the moment the report is taken at");

            // What the whole instrument rests on: the tree still carries the culprit as invalid once the pass is cut.
            var report = LayoutLoopReport.Describe([window]);

            Assert.Contains(report, entry => entry.Contains($"{nameof(LoopingProbe)}#guilty", StringComparison.Ordinal));
            Assert.DoesNotContain(report, entry => entry.Contains(nameof(QuietProbe), StringComparison.Ordinal));
        }
        finally
        {
            looper.Looping = false;
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
        }
    });

    [Fact]
    public void WithNoLayoutLoop_TheReportIsEmpty() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new StackPanel { Children = { new QuietProbe(), new QuietProbe() } },
        };
        window.Show();
        try
        {
            for (var frame = 0; frame < 10; frame++)
            {
                _Frame();
            }

            Assert.Empty(LayoutLoopReport.Describe([window]));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void AnInvisibleSubtree_IsNotNamedAsASuspect() => HeadlessAvalonia.Run(() =>
    {
        // Never measured, so permanently invalid: TranscriptRowView's LoginFlowView sat behind IsVisible like this
        // and turned up in every report of AC-1262's freeze, on a day no login flow ran at all.
        var hidden = new QuietProbe { Name = "hidden" };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new Border { IsVisible = false, Child = hidden },
        };

        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var described = LayoutLoopReport.Describe([window]);
        window.Close();

        Assert.False(hidden.IsMeasureValid, "the probe must be invalid, or the test proves nothing");
        Assert.DoesNotContain(described, entry => entry.Contains("#hidden", StringComparison.Ordinal));
    });
}
