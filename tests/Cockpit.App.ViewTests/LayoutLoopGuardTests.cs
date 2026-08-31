using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;
using Cockpit.App.Services;
using Cockpit.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    // The counter-proof, on the setup the next test cuts: same tree, same frames, guard turned off with the very
    // switch an operator has (N=0). Nothing stops it, and nothing in Avalonia notices it is happening.
    [Fact]
    public void WithTheGuardOff_ATickLoopNeverSettlesAndAvaloniaNeverCutsItOff() => HeadlessAvalonia.Run(() =>
    {
        var looper = new TickLooper { Name = "runaway", Child = new TextBlock { Text = "content" } };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { looper, new QuietProbe() } } };
        var guard = new LayoutLoopGuard(0);

        Exception? caught = null;
        void OnUnhandled(object? _, DispatcherUnhandledExceptionEventArgs e)
        {
            caught = e.Exception;
            e.Handled = true;
        }

        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        window.Show();
        try
        {
            var dirtySamples = 0;
            for (var frame = 0; frame < Frames; frame++)
            {
                var dirty = _FrameAndSample(window);
                Assert.Null(guard.Observe(dirty));
                if (dirty.Count > 0)
                {
                    dirtySamples++;
                }
            }

            // One measure per tick, right to the last one: the loop is still running, not merely still dirty.
            Assert.True(looper.Measures >= Frames, $"the looper measured {looper.Measures} times over {Frames} frames");
            Assert.True(dirtySamples >= Frames - 1, $"{dirtySamples} of {Frames} samples found the subtree dirty");
            Assert.True(looper.IsVisible, "the guard was off, so nothing should have been cut");
            Assert.Null(caught);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            window.Close();
        }
    });

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

    // Without this the guard's first cut would also be its last: the elements it just hid stay invalid for good,
    // and every later sample reads them back as the same subtree standing still.
    [Fact]
    public void AHiddenSubtreeIsNotReadAsStuckInLayout() => HeadlessAvalonia.Run(() =>
    {
        var hidden = new QuietProbe();
        var wrapper = new Border { Child = hidden, IsVisible = false };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { wrapper, new QuietProbe() } } };
        window.Show();
        try
        {
            for (var frame = 0; frame < 5; frame++)
            {
                _Frame();
            }

            Assert.False(hidden.IsMeasureValid);
            Assert.Empty(LayoutLoopReport.Collect([window]));
        }
        finally
        {
            window.Close();
        }
    });

    // AC-1263 criterion 4: what the guard costs a healthy cockpit. Its only input is the dirty sample, and that
    // is taken exclusively while an alarm stands -- so on a responsive thread the tree is never walked at all.
    // The starvation at the end is this test's positive control: without it a service that never ran would pass.
    [Fact]
    public async Task WhileTheUiThreadAnswers_TheGuardNeverWalksTheTree()
    {
        var walks = 0;
        var service = new DiagnosticsBackgroundService(
            NullLogger<DiagnosticsBackgroundService>.Instance, alarmAfter: TimeSpan.FromSeconds(2));
        Window? window = null;

        try
        {
            window = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var opened = new Window { Width = 400, Height = 300, Content = new StackPanel() };
                opened.Show();
                opened.UpdateLayout();
                return opened;
            });

            service.SetLayoutRoots(() =>
            {
                Interlocked.Increment(ref walks);
                return [window!];
            });

            service.Start();

            // Three times the injected alarm, on a thread nothing is holding: the samples the guard feeds on
            // are not merely rare here, they never happen.
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Equal(0, Volatile.Read(ref walks));

            using var starver = StarvedDispatcher.Start(DispatcherPriority.Normal);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (Volatile.Read(ref walks) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(Volatile.Read(ref walks) > 0, "the same service never sampled even once under starvation");
        }
        finally
        {
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => window?.Close(), DispatcherPriority.Background);
        }
    }

    // AC-1263 criterion 1, end to end through the production chain rather than the guard alone: the freeze alarm,
    // the dirty sampling it gates, the guard's judgement, the cut and the line it writes. The app-level runner
    // could not raise the 31-08 freeze on demand (see the harness README), so this is where the chain is closed.
    [Fact]
    public async Task AStarvedThreadWithADirtySetThatStandsStill_HasItsSubtreeCutAndSaidSo()
    {
        var logger = new _Lines();
        var service = new DiagnosticsBackgroundService(
            logger, alarmAfter: TimeSpan.FromSeconds(2), dirtySampleInterval: TimeSpan.FromSeconds(1));
        var (window, panel) = await _ASettledWindow();

        try
        {
            service.SetLayoutRoots(() => [window]);
            using var starver = StarvedDispatcher.Start(DispatcherPriority.Normal);
            service.Start();
            await _OnUiThread<object?>(() => { panel.InvalidateMeasure(); return null; });

            Assert.True(
                await logger.Appears("layout loop cut off after"),
                "three samples found the same subtree standing still and nothing cut it");

            var line = logger.First("layout loop cut off after");
            Assert.Contains($"after {LayoutLoopGuard.DefaultSamplesBeforeCut} dirty sample(s)", line, StringComparison.Ordinal);
            Assert.Contains("StackPanel#TheLoop", line, StringComparison.Ordinal);
            Assert.False(await _OnUiThread(() => panel.IsVisible), "the subtree was reported as cut but is still in layout");
        }
        finally
        {
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => window.Close(), DispatcherPriority.Background);
        }
    }

    // The counter-proof, taken with the switch an operator actually has: the same tree, the same starvation, the
    // same samples, and nothing stops it. Without this the test above proves only that something happened.
    [Fact]
    public async Task WithTheSwitchOff_TheSameStandstillIsSampledAndLeftAlone()
    {
        var before = Environment.GetEnvironmentVariable(LayoutLoopGuard.SamplesEnvironmentVariable);
        Environment.SetEnvironmentVariable(LayoutLoopGuard.SamplesEnvironmentVariable, "0");

        var logger = new _Lines();
        var service = new DiagnosticsBackgroundService(
            logger, alarmAfter: TimeSpan.FromSeconds(2), dirtySampleInterval: TimeSpan.FromSeconds(1));
        var (window, panel) = await _ASettledWindow();

        try
        {
            service.SetLayoutRoots(() => [window]);
            using var starver = StarvedDispatcher.Start(DispatcherPriority.Normal);
            service.Start();
            await _OnUiThread<object?>(() => { panel.InvalidateMeasure(); return null; });

            // Past the sample the run above cut on, so "not cut" is a reading rather than an impatient assertion.
            Assert.True(await logger.Appears("sample=3/"), "the episode was never sampled three times");
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.DoesNotContain(logger.Lines, line => line.Contains("cut off after", StringComparison.Ordinal));
            Assert.True(await _OnUiThread(() => panel.IsVisible), "the switch was off and the subtree was cut anyway");
            Assert.False(await _OnUiThread(() => panel.IsMeasureValid), "the tree settled on its own, so nothing was there to cut");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LayoutLoopGuard.SamplesEnvironmentVariable, before);
            service.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => window.Close(), DispatcherPriority.Background);
        }
    }

    // A settled tree to start from. It is left mid-pass only once the starvation is in place: invalidating
    // before that queues a pass which is served straight away, and the tree the guard should judge settles.
    private static async Task<(Window Window, StackPanel Panel)> _ASettledWindow()
    {
        var made = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var panel = new StackPanel { Name = "TheLoop", Children = { new QuietProbe() } };
            var opened = new Window { Width = 400, Height = 300, Content = panel };
            opened.Show();
            opened.UpdateLayout();
            return (Window: opened, Panel: panel);
        });

        return made;
    }

    // Send priority: above the starvation the tests below install, so reading the tree is not itself starved.
    private static Task<T> _OnUiThread<T>(Func<T> read) =>
        Dispatcher.UIThread.InvokeAsync(read, DispatcherPriority.Send).GetTask();

    private sealed class _Lines : ILogger<DiagnosticsBackgroundService>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _Scope.Instance;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) =>
            _lines.Enqueue(format(state, error));

        public string First(string token) => Lines.First(line => line.Contains(token, StringComparison.Ordinal));

        public async Task<bool> Appears(string token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (Lines.Any(line => line.Contains(token, StringComparison.Ordinal)))
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        private sealed class _Scope : IDisposable
        {
            public static readonly _Scope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
