#if DEBUG
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.ViewTests;

// AC-883: on macOS the OS can stop the render clock without touching WindowState — screen lock, display sleep, a
// Space switch, full occlusion — so minimising is not the coverage there that it is on Windows and X11. The
// diagnostics probe from AC-882 is the signal; these pin that it reaches a pane, that it never fires on a machine
// that is merely busy, and that lifting it does not lift a minimise that is still standing.
[Collection("avalonia")]
public sealed class RenderClockStallPauseTests
{
    // Enough queued work to hold the UI thread for about two seconds — two orders of magnitude over a healthy
    // round trip, so the measurement below is a genuinely loaded machine rather than a nominally busy one.
    private const int LoadJobs = 200;
    private const int LoadJobMilliseconds = 10;

    [Fact]
    public async Task ABusyButLiveMachine_KeepsGettingItsCommitsProcessed()
    {
        // The measured half of the false-positive guard: RenderClockHeartbeatTests pins the thresholds, this shows
        // what a genuinely loaded machine does to a forced commit. Measured here, not assumed: with ~2s of work
        // still queued, the commit came back in tens of milliseconds — because RequestCommitAsync posts its trigger
        // at DispatcherPriority.Send, which overtakes the whole backlog. Queue depth therefore cannot produce a
        // slow round trip at all, which is what makes an unprocessed commit evidence of a stopped clock and not of
        // a busy one. The first draft of this test asserted the opposite and failed, which is how that was found.
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new Window { Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();

            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
            await compositor.RequestCommitAsync();

            var jobsDone = 0;
            for (var i = 0; i < LoadJobs; i++)
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        var spin = Stopwatch.StartNew();
                        while (spin.ElapsedMilliseconds < LoadJobMilliseconds)
                        {
                        }

                        jobsDone++;
                    },
                    DispatcherPriority.Background);
            }

            var startedAt = Stopwatch.StartNew();
            var commit = compositor.RequestCommitAsync();
            var settled = await Task.WhenAny(commit, Task.Delay(RenderClockHeartbeat.StallAfter));
            var roundTrip = startedAt.Elapsed;
            var jobsLeft = LoadJobs - jobsDone;

            Assert.True(
                ReferenceEquals(settled, commit),
                $"a forced commit went unprocessed for {RenderClockHeartbeat.StallAfter.TotalSeconds:0}s under "
                + "dispatcher load — if load alone can do that, the stall signal cannot be trusted to pause a pane");

            Assert.True(
                jobsLeft > 0,
                "every load job had already finished when the commit came back, so this measured an idle machine "
                + "rather than a busy one — raise LoadJobs or LoadJobMilliseconds");

            Assert.False(
                RenderClockHeartbeat.ShouldPauseRenderers(roundTrip, isMacOs: true),
                $"a machine with {jobsLeft} jobs still queued answered its commit in {roundTrip.TotalMilliseconds:0}ms, "
                + $"and that reads as a stopped clock against PauseAfter={RenderClockHeartbeat.PauseAfter.TotalSeconds:0}s");

            // Drain before tearing down, so a couple of seconds of spinning does not run on into the next test.
            while (jobsDone < LoadJobs)
            {
                await Task.Delay(50);
            }

            window.Close();
        });
    }

    [Fact]
    public async Task TheStallSignal_SuspendsTheTranscript_AndLiftingItResumes()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var diagnostics = new DiagnosticsBackgroundService(NullLogger<DiagnosticsBackgroundService>.Instance);
            var (view, window, scroll) = _ShowPane(diagnostics);

            Assert.True(scroll.IsVisible, "transcript is realised on a machine whose render clock is fine");

            // Through the service and its event, not by poking the view: the wiring from probe to pane is the part
            // that would silently do nothing on a Mac nobody here can test on.
            diagnostics.SetRenderersShouldPause(true);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.False(scroll.IsVisible, "transcript is suspended while the render clock cannot process commits");

            diagnostics.SetRenderersShouldPause(false);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(scroll.IsVisible, "transcript is resumed once the render clock comes back");

            window.Close();
            GC.KeepAlive(view);
        });
    }

    [Fact]
    public void TheStallLifting_DoesNotResumeAPaneWhoseWindowIsStillMinimised()
    {
        // The two reasons are ORed, and this is the assertion that keeps them that way: a refactor that lets the
        // newer signal own the flag outright would resume a minimised pane and undo cc85ca1e's fix.
        HeadlessAvalonia.Run(() =>
        {
            var diagnostics = new DiagnosticsBackgroundService(NullLogger<DiagnosticsBackgroundService>.Instance);
            var (view, window, scroll) = _ShowPane(diagnostics);

            window.WindowState = WindowState.Minimized;
            window.UpdateLayout();
            Assert.False(scroll.IsVisible);

            view.SetRenderClockPaused(true);
            window.UpdateLayout();
            Assert.False(scroll.IsVisible);

            view.SetRenderClockPaused(false);
            window.UpdateLayout();
            Assert.False(scroll.IsVisible, "the window is still minimised, so the pane stays suspended");

            window.WindowState = WindowState.Normal;
            window.UpdateLayout();
            Assert.True(scroll.IsVisible, "both reasons have lifted");

            window.Close();
        });
    }

    [Fact]
    public void ADetachedPane_NoLongerHearsTheSignal()
    {
        // The service outlives every pane. A pane that stays subscribed after detach is both a leak and a write to
        // a torn-down template — the failure this ticket is meant to reduce, not add to.
        HeadlessAvalonia.Run(() =>
        {
            var diagnostics = new DiagnosticsBackgroundService(NullLogger<DiagnosticsBackgroundService>.Instance);
            var (_, window, scroll) = _ShowPane(diagnostics);

            window.Content = null;
            window.UpdateLayout();

            diagnostics.SetRenderersShouldPause(true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(scroll.IsVisible, "a detached pane must not still be taking orders from the signal");

            window.Close();
        });
    }

    [Fact]
    public void OnWindowsAndLinux_APaneDoesNotEvenSubscribe()
    {
        // The hard requirement from AC-882: their behaviour after af2fe273/cc85ca1e must not change at all. Gating
        // only the decision would still leave every pane holding a delegate on a process-lifetime singleton — an
        // inert leak surface on platforms with no problem to solve. So the resolve itself is gated, and this says so.
        // The container is populated on purpose: with Program.Services left null, "resolved nothing" and "was never
        // allowed to resolve" are indistinguishable, and the first draft of this test passed on that emptiness.
        var previous = Program.Services;
        var container = new ServiceCollection()
            .AddSingleton<DiagnosticsBackgroundService>()
            .AddSingleton(NullLogger<DiagnosticsBackgroundService>.Instance)
            .AddSingleton<ILogger<DiagnosticsBackgroundService>>(NullLogger<DiagnosticsBackgroundService>.Instance)
            .BuildServiceProvider();

        try
        {
            Program.Services = container;

            HeadlessAvalonia.Run(() =>
            {
                Assert.NotNull(Program.Services.GetService<DiagnosticsBackgroundService>());

                var vm = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
                var view = new SessionView { DataContext = vm };
                var window = new Window { Content = view, Width = 820, Height = 640 };
                window.Show();
                window.UpdateLayout();

                Assert.True(
                    OperatingSystem.IsMacOS() || view.Diagnostics is null,
                    "a pane off macOS reached into the container anyway, so it is subscribing to a signal that can "
                    + "never fire there — the pre-AC-883 Windows/Linux path is no longer untouched");

                window.Close();
            });
        }
        finally
        {
            Program.Services = previous;
            container.Dispose();
        }
    }

    private static (SessionView View, Window Window, ScrollViewer Scroll) _ShowPane(
        DiagnosticsBackgroundService diagnostics)
    {
        var vm = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
        for (var i = 0; i < 5; i++)
        {
            vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {i}"));
        }

        // Set before Show, so the attach subscribes to this instance instead of resolving the container's.
        var view = new SessionView { DataContext = vm, Diagnostics = diagnostics };
        var window = new Window { Content = view, Width = 820, Height = 640 };
        window.Show();
        window.UpdateLayout();

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TranscriptScroll");
        return (view, window, scroll);
    }
}
#endif
