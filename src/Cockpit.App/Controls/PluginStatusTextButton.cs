using Avalonia.Controls;
using Cockpit.App.Plugins;

namespace Cockpit.App.Controls;

// The status line of a plugin that offered a popup: a statusMeter button like the disk and CPU ones, opening the plugin's control in a flyout.
internal sealed class PluginStatusTextButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnClick()
    {
        base.OnClick();
        if (DataContext is not PluginStatusText { Popup: { } createPopup })
            return;
        var flyout = new Flyout { Placement = PlacementMode.Top };
        flyout.Content = createPopup(flyout.Hide);
        flyout.ShowAt(this);
    }
}
