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
        BrowserEngineState.Unavailable => _Unavailable(engine),
        _ => _Centered(MaterialIconKind.Web, "Starting the browser engine…", null),
    };

    // AC-1760: a failed download is tried again from here; only an engine that CEF already loaded needs a restart.
    private static Control _Unavailable(BrowserEngine engine)
    {
        var reason = engine.Reason ?? "The browser engine is not available.";
        if (engine.CanRetry == false)
        {
            return _Centered(MaterialIconKind.WebOff, "Browser unavailable", reason + Environment.NewLine + Environment.NewLine
                + "Restart Cockpit to try again. If it keeps failing, reinstall the Browser plugin from the plugin manager.");
        }

        var retry = new Button { Content = "Try again", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Center };
        retry.Click += (_, _) => _ = engine.RetryAsync();
        var panel = (StackPanel)_Centered(MaterialIconKind.WebOff, "The browser engine could not be fetched", reason);
        panel.Children.Add(retry);
        return panel;
    }

    // AC-1760: the browser opens by itself once this is done, so the text says that, and how far along it is.
    private static Control _Fetching(Runtime.CefRuntimeProgress? progress, string? downloadText)
    {
        var unpacking = progress?.Phase == "Unpacking";
        var title = unpacking ? "Unpacking the browser engine…" : "Downloading the browser engine";
        if (unpacking == false && progress is { Total: > 0 } known)
        {
            title += $" · {_Megabytes(known.Done)} of {_Megabytes(known.Total.Value)} MB ({100 * known.Done / known.Total.Value}%)";
        }

        var detail = (downloadText is null ? string.Empty : downloadText + Environment.NewLine)
            + "This happens once. The browser opens here by itself when it is done; Cockpit does not need a restart.";

        var bar = new ProgressBar
        {
            Width = 320,
            Minimum = 0,
            Maximum = 1,
            Value = progress is { Total: > 0 } part ? (double)part.Done / part.Total.Value : 0,
            IsIndeterminate = progress?.Total is null or 0 || unpacking,
        };

        var panel = (StackPanel)_Centered(MaterialIconKind.Download, title, detail);
        panel.Children.Add(bar);
        return panel;
    }

    private static long _Megabytes(long bytes) => (bytes + 500_000) / 1_000_000;

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
