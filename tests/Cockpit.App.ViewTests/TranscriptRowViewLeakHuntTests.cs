#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.Diagnostics;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-878's inventory names TranscriptRowView in its own right. This checks whether SessionView's single,
// root-level CompositorTeardown.Flush(e.RootVisual) also releases the rows nested inside it — it does, so rows
// need no hook of their own; a parent's one flush covers its whole detached subtree.
[Collection("avalonia")]
public sealed class TranscriptRowViewLeakHuntTests
{
    private static string MarkdownDoc(int i) => $"## Heading {i}\n\nSome **bold** text for row {i}.\n";

    [Fact]
    public async Task ClosingAPaneWithoutARender_AlsoReleasesTheRowsInsideIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = new Window { Width = 820, Height = 640 };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(40);

            LeakTracker.Reset();
            _BuildDetachNoRender(window);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforePump = LeakTracker.AliveCount(nameof(TranscriptRowView));

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var afterPump = LeakTracker.AliveCount(nameof(TranscriptRowView));

            Assert.True(
                afterPump == 0,
                $"paused-close rows alive: right after close={beforePump}, after the pump={afterPump} "
                + "(expected 0 with no per-row fix — regression guard: if this ever goes non-zero, rows are no "
                + "longer covered by SessionView's own commit and need their own CompositorTeardown.Flush)");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _BuildDetachNoRender(Window window)
    {
        var vm = new SessionViewModel();
        for (var i = 0; i < 60; i++)
        {
            vm.VisibleTranscript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, MarkdownDoc(i)));
        }

        var view = new SessionView { DataContext = vm };
        window.Content = view;
        window.UpdateLayout();   // realise it

        // Close WITHOUT a following render pass — the background-tab case.
        window.Content = new Border();
        view.DataContext = null;
    }
}
#endif
