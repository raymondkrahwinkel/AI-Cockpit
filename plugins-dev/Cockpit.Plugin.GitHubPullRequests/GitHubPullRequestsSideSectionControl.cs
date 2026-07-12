using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The inline accordion section registered via <see cref="ICockpitHost.AddSideMenuSection"/>, always visible
/// under the session list: up to <see cref="MaxItems"/> open pull requests (across all repos in GitHub CLI
/// mode, or one repo in HTTP mode), each a clickable row that renders the prompt template and injects it into
/// the active session — or, with no active session, copies it to the clipboard instead — plus a "View all
/// open PRs" button that opens the full <see cref="GitHubPullRequestsDialogControl"/> dialog.
/// </summary>
internal sealed class GitHubPullRequestsSideSectionControl : UserControl
{
    private const int MaxItems = 5;

    private readonly GitHubPullRequestsSettings _settings;
    private readonly ICockpitHost _host;
    private readonly GitHubPullRequestsClient _http = new();
    private readonly GitHubPrGhClient _gh = new();

    private readonly TextBlock _status;
    private readonly StackPanel _list;

    public GitHubPullRequestsSideSectionControl(GitHubPullRequestsSettings settings, ICockpitHost host)
    {
        _settings = settings;
        _host = host;

        _status = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };

        var refresh = new Button { Content = "⟳", FontSize = 11, Padding = new Thickness(6, 2) };
        refresh.Click += async (_, _) => await _LoadAsync(forceRefresh: true);

        var header = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(_status);

        var viewAll = new Button
        {
            Content = "View all open PRs",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "Accent" },
        };
        viewAll.Click += (_, _) => _ = _host.ShowDialogAsync(
            "GitHub Pull Requests",
            () => new GitHubPullRequestsDialogControl(_settings, _host.Actions),
            1040,
            700);

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { header, _list, viewAll },
        };

        // Re-fetch with the just-saved settings (owner/repo, token, gh-CLI toggle) instead of leaving this
        // already-built section showing data loaded under the old configuration until an app restart (#52).
        host.OnSettingsSaved(() => _ = _LoadAsync(forceRefresh: true));

        _ = _LoadAsync(forceRefresh: false);
    }

    private async Task _LoadAsync(bool forceRefresh)
    {
        _status.Text = "Loading…";
        _list.Children.Clear();
        try
        {
            IReadOnlyList<GitHubPullRequest> all;
            if (_settings.UseGitHubCli)
            {
                all = await _gh.SearchOpenPullRequestsAsync(_settings.GhOwner, assignedToMe: false, forceRefresh, CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
                {
                    _status.Text = "Set a repository in settings, or turn on the GitHub CLI.";
                    return;
                }

                all = await _http.GetOpenPullRequestsAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe: false, CancellationToken.None);
            }

            foreach (var pullRequest in all.Take(MaxItems))
            {
                _list.Children.Add(_BuildRow(pullRequest));
            }

            _status.Text = all.Count switch
            {
                0 => "No open pull requests.",
                _ => $"{Math.Min(all.Count, MaxItems)} of {all.Count} open PR(s) — click to add to the prompt.",
            };
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load pull requests: {exception.Message}";
        }
    }

    private Button _BuildRow(GitHubPullRequest pullRequest)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"#{pullRequest.Number} {pullRequest.Title}", FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = pullRequest.Repository, FontSize = 10, Opacity = 0.6 },
                },
            },
        };
        button.Click += async (_, _) => await _InjectAsync(pullRequest);
        return button;
    }

    private async Task _InjectAsync(GitHubPullRequest pullRequest)
    {
        var parts = pullRequest.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        var prompt = PromptTemplate.Render(_settings.Template, pullRequest, owner, repo);

        if (_host.Actions.HasActiveSession)
        {
            await _host.Actions.InjectIntoActiveSessionAsync(prompt);
            _status.Text = $"✓ Added PR #{pullRequest.Number} to the active session's prompt.";
        }
        else
        {
            await _host.Actions.SetClipboardTextAsync(prompt);
            _status.Text = $"✓ No active session — copied PR #{pullRequest.Number}'s prompt to the clipboard.";
        }
    }
}
