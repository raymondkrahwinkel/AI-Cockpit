using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Browser.UI.Engine;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Browser.UI;

// What the page area shows while there is no engine to show a page: the download with its progress, the start, or
// why the browser is off and what to do about it. Never hidden, so a missing engine is never a silent blank panel.
internal static class EngineStatusView
{
    public static Control For(BrowserEngine engine) => engine.State switch
    {
        BrowserEngineState.Fetching => _Fetching(engine.Progress, engine.DownloadText),
        BrowserEngineState.Unavailable => Unavailable(engine.Reason ?? "The browser engine is not available."),
        _ => _Centered(MaterialIconKind.Web, "Starting the browser engine…", null),
    };

    public static Control Unavailable(string reason) =>
        _Centered(MaterialIconKind.WebOff, "Browser unavailable", reason + Environment.NewLine + Environment.NewLine
            + "Restart Cockpit to try again. If it keeps failing, reinstall the Browser plugin from the plugin manager.");

    private static Control _Fetching(Runtime.CefRuntimeProgress? progress, string? downloadText)
    {
        var total = progress?.Total;
        var title = progress?.Phase == "Unpacking" ? "Unpacking the browser engine" : "Downloading the browser engine";
        var detail = (downloadText is null ? string.Empty : downloadText + Environment.NewLine) + "Once only: the engine is kept for the next start.";

        var bar = new ProgressBar
        {
            Width = 320,
            Minimum = 0,
            Maximum = 1,
            Value = progress is { Total: > 0 } known ? (double)known.Done / known.Total.Value : 0,
            IsIndeterminate = total is null or 0 || progress?.Phase == "Unpacking",
        };

        var panel = (StackPanel)_Centered(MaterialIconKind.Download, title, detail);
        panel.Children.Add(bar);
        return panel;
    }

    private static Control _Centered(MaterialIconKind icon, string title, string? detail)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Children =
            {
                new MaterialIcon { Kind = icon, Width = 40, Height = 40 }.Themed(MaterialIcon.ForegroundProperty, "CockpitTextFaintBrush"),
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
            },
        };

        if (detail is not null)
        {
            panel.Children.Add(new TextBlock { Text = detail, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap }
                .Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush"));
        }

        return panel;
    }
}
