using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Browser.UI.Runtime;

namespace Cockpit.Plugin.Browser.UI;

// Which engine this plugin version brings, and whether it has aged past the point where updating the plugin
// is advised (AC-1727). Information only: an old engine is never blocked.
internal sealed class BrowserEngineInfoControl : UserControl
{
    public BrowserEngineInfoControl(CefRuntimeCatalog catalog)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Browser engine", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"CEF {catalog.CefVersion} · Chromium {catalog.ChromiumVersion}" },
                new TextBlock { Text = $"Built {catalog.BuildDate:yyyy-MM-dd}" },
            },
        };

        if (catalog.IsOutdated(DateOnly.FromDateTime(DateTime.Today)))
        {
            panel.Children.Add(new TextBlock { Text = "Engine outdated · update the plugin", Foreground = Brushes.OrangeRed });
        }

        Content = panel;
    }
}
