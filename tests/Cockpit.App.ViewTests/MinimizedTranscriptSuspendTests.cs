#if DEBUG
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

// A minimised window pauses its renderer, so a streaming transcript's recycled rows never get the compositor commit
// that removes their server scene visuals — they pile up without bound (the overnight "idle at multi-GB, 0 sessions"
// growth). SessionView suspends the transcript while minimised by collapsing its scroll owner, so the panel
// dematerialises its rows and stops building new ones. Headless has no real compositor so it cannot measure the
// leak; this pins the wiring that prevents it so a refactor cannot silently drop it.
[Collection("avalonia")]
public sealed class MinimizedTranscriptSuspendTests
{
    [Fact]
    public async Task Minimize_SuspendsTranscript_And_Restore_ResumesIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var vm = new SessionViewModel { ReadingLevel = ReadingLevel.Focus };
            for (var i = 0; i < 5; i++)
            {
                vm.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {i}"));
            }

            var view = new SessionView { DataContext = vm };
            var window = new Window { Content = view, Width = 820, Height = 640 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(1);

            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TranscriptScroll");
            Assert.True(scroll.IsVisible, "transcript is realised while the window is shown");

            window.WindowState = WindowState.Minimized;
            window.UpdateLayout();
            Assert.False(scroll.IsVisible, "transcript is suspended while the window is minimised");

            window.WindowState = WindowState.Normal;
            window.UpdateLayout();
            Assert.True(scroll.IsVisible, "transcript is resumed once the window is restored");
        });
    }
}
#endif
