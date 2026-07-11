using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// About dialog (#46): app name, running version, a short description, and links to the public GitHub
/// repo and plugin store. <see cref="Window.DataContext"/> is an <see cref="AboutInfo"/> built by the
/// caller from the entry assembly.
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutInfo info)
        {
            _OpenUrl(info.GitHubUrl);
        }
    }

    private void OnOpenPluginStore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutInfo info)
        {
            _OpenUrl(info.PluginStoreUrl);
        }
    }

    // Mirrors MarkdownView's link handler: only ever shell out to an http(s) URL, and a failed browser
    // launch must not crash the UI thread.
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
