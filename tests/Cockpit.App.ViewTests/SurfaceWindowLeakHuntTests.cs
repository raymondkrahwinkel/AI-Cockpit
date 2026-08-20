#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.Diagnostics;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

// AC-878: hunts the same leak for the windows SurfaceWindows opens (plugin dialogs, MCP servers, the plugin
// store — PluginDialogHost always goes through SurfaceWindows.ShowAsync). Stays green with no fix: closing a
// Window, unlike a UserControl detaching while its host stays open, already flushes its own composited scene.
[Collection("avalonia")]
public sealed class SurfaceWindowLeakHuntTests
{
    [Fact]
    public async Task ClosingASurfaceWindowWithoutARender_AlreadyReleasesItsContent()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            LeakTracker.Reset();
            await _ShowAndCloseNoRenderAsync();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforePump = LeakTracker.AliveCount(nameof(TranscriptRowView));

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var afterPump = LeakTracker.AliveCount(nameof(TranscriptRowView));

            Assert.True(
                afterPump == 0,
                $"closed-surface rows alive: right after close={beforePump}, after the pump={afterPump} "
                + "(expected 0 with no fix — regression guard: if this ever goes non-zero, SurfaceWindows has "
                + "started carrying the AC-878 leak and its Closed hook needs CompositorTeardown.Flush)");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task _ShowAndCloseNoRenderAsync()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        owner.UpdateLayout();

        var content = new StackPanel();
        for (var i = 0; i < 30; i++)
        {
            content.Children.Add(new TranscriptRowView
            {
                DataContext = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {i}"),
            });
        }

        var surface = new Window { Content = content, Width = 400, Height = 600 };
        var surfaces = new SurfaceWindows();
        var pending = surfaces.ShowAsync(new object(), surface, owner);
        surface.UpdateLayout();   // realise the rows
        await Task.Delay(40);

        // Close WITHOUT a following render pass of our own — the paused-renderer/background-desk case.
        surface.Close();
        await pending;
    }
}
#endif
