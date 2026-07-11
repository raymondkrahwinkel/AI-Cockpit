using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The plugin store dialog (#62): sidebar/search/sort/grid/detail over a
/// <see cref="PluginStoreDialogViewModel"/>. Disposes the view model on close so it unsubscribes from
/// the shared (long-lived) <see cref="PluginManagerViewModel"/>'s collection/property-changed events —
/// otherwise every store-dialog open would leak one more subscription on that shared instance.
/// </summary>
public partial class PluginStoreDialog : Window
{
    public PluginStoreDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        (DataContext as PluginStoreDialogViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenHomepage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Homepage: { } url })
        {
            _OpenUrl(url);
        }
    }

    private void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Repository: { } url })
        {
            _OpenUrl(url);
        }
    }

    // Mirrors AboutDialog/MarkdownView's link handler: only ever shell out to an http(s) URL, and a
    // failed browser launch must not crash the UI thread.
    private static void _OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort: a failed browser launch must not crash the UI thread.
        }
    }
}
