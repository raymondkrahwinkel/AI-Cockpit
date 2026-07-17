using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// One pull-requests widget instance's settings: how many pull requests the pane shows (#AC-18). Implements
/// <see cref="IPluginSettingsView"/>, so the host wraps it in its standard Save/Close footer — the widget
/// supplies the content, never the window.
/// </summary>
/// <remarks>
/// Reads and writes through the instance's own <see cref="IWidgetContext.Storage"/>, which is what lets two
/// PR widgets on one dashboard show different numbers of rows. Connection, repositories and the prompt template
/// are deliberately absent: those are shared plugin settings, edited once from the plugin manager's gear, not
/// per pane.
/// </remarks>
internal sealed class GitHubPullRequestsWidgetSettingsView : UserControl, IPluginSettingsView
{
    private readonly IWidgetContext _context;
    private readonly NumericUpDown _maxItems = new()
    {
        Minimum = GitHubPullRequestsWidgetConfig.MinItems,
        Maximum = GitHubPullRequestsWidgetConfig.MaxItemsAllowed,
        Increment = 1,
        FormatString = "0",
        Width = 120,
    };

    public GitHubPullRequestsWidgetSettingsView(IWidgetContext context)
    {
        _context = context;

        var config = (context.Storage.Get<GitHubPullRequestsWidgetConfig>(GitHubPullRequestsWidgetConfig.StorageKey)
            ?? GitHubPullRequestsWidgetConfig.Default).Sanitized();
        _maxItems.Value = config.MaxItems;

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(4),
            Children =
            {
                new TextBlock { Text = "Pull requests to show", FontWeight = FontWeight.SemiBold },
                _maxItems,
                new TextBlock
                {
                    Text = "How many of the newest open pull requests this pane lists (1–20). Connection, "
                        + "repositories and the prompt template are shared settings — edit those from the plugin "
                        + "manager's gear.",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    public bool Save()
    {
        _context.Storage.Set(GitHubPullRequestsWidgetConfig.StorageKey, new GitHubPullRequestsWidgetConfig
        {
            MaxItems = (int)(_maxItems.Value ?? GitHubPullRequestsWidgetConfig.Default.MaxItems),
        }.Sanitized());

        return true;
    }
}
