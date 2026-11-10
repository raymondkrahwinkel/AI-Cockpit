using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static void _StageScratch(Window window, bool grown, bool hover)
    {
        var details = window.GetVisualDescendants().OfType<ConversationDetailsButton>()
            .First(button => button.IsEffectivelyVisible && button.DataContext is SessionPanelViewModel);
        var pane = (SessionPanelViewModel)(details.DataContext ?? throw new InvalidOperationException("Scene has no session."));
        var resources = new SceneResources([], new Dictionary<string, (string Path, long? Bytes)>
        {
            [pane.PaneId] = (@"C:\ck\A1819B", grown ? 50L * 1024 * 1024 : 0),
        });
        details.Use(resources);
        var disk = window.GetVisualDescendants().OfType<DiskStorageStatus>().Single();
        disk.Use(resources, new SceneDocker());
        if (hover)
        {
            ToolTip.SetIsOpen(disk, true);
        }
        else
        {
            _OpenFlyout(details, "DetailsButton");
        }
    }
}
