using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-2241: the empty panel's way to the Depot settings, the route the settings cog uses.
internal static class DepotAddServerButton
{
    public static Button Create(ICockpitUiHost host)
    {
        var button = new Button { Content = "Add Depot server", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        button.Click += async (_, _) => await host.ShowSettingsAsync().ConfigureAwait(true);
        return button;
    }
}
