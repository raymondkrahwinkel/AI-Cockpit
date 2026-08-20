#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.App.ViewTests;

// AC-878's inventory names "plugin-workspace-bodies" — CockpitView binds a workspace tab's body straight onto a
// ContentControl (`Content="{Binding Workspaces.ActivePluginBody}"`), the same Content-swap-while-host-stays-open
// shape SessionView's own leak needs. Stays released with no fix added at the host.
[Collection("avalonia")]
public sealed class PluginWorkspaceBodyLeakHuntTests
{
    [Fact]
    public async Task DetachingAPluginBodyWithoutARender_AlreadyReleasesIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var alive = _BuildDetachNoRender();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforePump = alive.IsAlive;
            Assert.True(beforePump, "vacuous test: the body was already collected before the pump even ran");

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(120);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var afterPump = alive.IsAlive;

            Assert.False(
                afterPump,
                $"paused-detach plugin body alive: right after detach={beforePump}, after the pump={afterPump} "
                + "(expected released with no fix — regression guard: if this ever goes true, workspace-body "
                + "detach has started carrying the AC-878 leak and CockpitView's ActivePluginBody swap needs a "
                + "CompositorTeardown.Flush call)");
        });
    }

    // A plugin body with real visual weight and no embedded session — the shape none of this repo's own bodies
    // are, since every one of them embeds a session (already covered).
    private static Control _FakePluginBody()
    {
        var panel = new StackPanel { Spacing = 6 };
        for (var i = 0; i < 80; i++)
        {
            panel.Children.Add(new Border
            {
                Padding = new Thickness(8),
                Background = Brushes.Transparent,
                Child = new TextBlock { Text = $"Plugin-drawn row {i}", TextWrapping = TextWrapping.Wrap },
            });
        }

        return new ScrollViewer { Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference _BuildDetachNoRender()
    {
        var window = new Window { Width = 820, Height = 640 };
        window.Show();
        window.UpdateLayout();

        var body = _FakePluginBody();
        var host = new ContentControl { Content = body };
        window.Content = host;
        window.UpdateLayout();   // realise it — the same "workspace tab is open" state

        var tracked = new WeakReference(body);

        // Swap ActivePluginBody away WITHOUT a following render pass — closing/switching a tab while the main
        // window is minimised/paused.
        host.Content = null;

        return tracked;
    }
}
#endif
