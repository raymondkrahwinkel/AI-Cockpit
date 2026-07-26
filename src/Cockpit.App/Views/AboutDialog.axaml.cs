using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// About dialog (#46): app name, running version, a short description, the providers a session can run
/// under, the licence, and links to the public GitHub repo, the issue tracker and the plugin store.
/// <see cref="Window.DataContext"/> is an <see cref="AboutInfo"/> built by the caller from the entry assembly.
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
            ExternalLink.TryOpen(info.GitHubUrl);
        }
    }

    private void OnOpenIssues(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutInfo info)
        {
            ExternalLink.TryOpen(info.IssuesUrl);
        }
    }

    private void OnOpenPluginStore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AboutInfo info)
        {
            ExternalLink.TryOpen(info.PluginStoreUrl);
        }
    }
}
