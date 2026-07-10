using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The plugin's left-menu section: a Refresh button and the list of open issues. Clicking an issue renders
/// the (editable) prompt template for it and injects it into the active session via
/// <see cref="ICockpitActions"/> — so the agent is asked to open and review that issue — falling back to
/// the clipboard when no session is active. Built in code for the same reason as the Options tab.
/// </summary>
internal sealed class GitHubIssuesPanelControl : UserControl
{
    private readonly GitHubIssuesSettings _settings;
    private readonly GitHubIssuesClient _client;
    private readonly ICockpitActions _actions;
    private readonly StackPanel _list;
    private readonly TextBlock _status;

    public GitHubIssuesPanelControl(GitHubIssuesSettings settings, GitHubIssuesClient client, ICockpitActions actions)
    {
        _settings = settings;
        _client = client;
        _actions = actions;

        _list = new StackPanel { Spacing = 4 };
        _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };

        var refresh = new Button { Content = "Refresh issues", HorizontalAlignment = HorizontalAlignment.Stretch };
        refresh.Click += async (_, _) => await _LoadAsync();

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { refresh, _status, _list },
        };
    }

    private async Task _LoadAsync()
    {
        _list.Children.Clear();

        if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
        {
            _status.Text = "Set the repository owner and name in Options first.";
            return;
        }

        _status.Text = "Loading…";
        try
        {
            var issues = await _client.GetOpenIssuesAsync(_settings.Owner, _settings.Repo, _settings.Token, CancellationToken.None);
            _status.Text = issues.Count == 0
                ? "No open issues."
                : $"{issues.Count} open issue(s). Click one to add it to the prompt.";

            foreach (var issue in issues)
            {
                var button = new Button
                {
                    Content = $"#{issue.Number}  {issue.Title}",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                var captured = issue;
                button.Click += async (_, _) => await _InjectAsync(captured);
                _list.Children.Add(button);
            }
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    private async Task _InjectAsync(GitHubIssue issue)
    {
        try
        {
            var prompt = PromptTemplate.Render(_settings.Template, issue, _settings.Owner, _settings.Repo);
            if (_actions.HasActiveSession)
            {
                await _actions.InjectIntoActiveSessionAsync(prompt);
                _status.Text = $"Added issue #{issue.Number} to the active session's prompt.";
            }
            else
            {
                await _actions.SetClipboardTextAsync(prompt);
                _status.Text = $"No active session — issue #{issue.Number} copied to the clipboard.";
            }
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not add the issue: {exception.Message}";
        }
    }
}
