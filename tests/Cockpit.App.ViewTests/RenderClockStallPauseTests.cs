#if DEBUG
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.ViewTests;

// AC-883: on macOS the OS can stop the render clock without touching WindowState, so minimising is not the
// coverage there that it is on Windows and X11. These pin that AC-882's probe reaches a pane, never fires on a
// merely busy machine, and does not lift a minimise that is still standing.
[Collection("avalonia")]
public sealed class RenderClockStallPauseTests
{
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
