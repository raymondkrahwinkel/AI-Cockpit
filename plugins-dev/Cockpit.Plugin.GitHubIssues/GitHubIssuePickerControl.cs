using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Picks a GitHub issue for one session (#77). Opened from that session's own header, so the issue lands on the pane
/// you opened it from. A list of the open issues for the owner you configured, and a box to narrow it — the question
/// is "which of these am I working on here", and nothing else belongs on screen.
/// </summary>
internal sealed class GitHubIssuePickerControl : UserControl
{
    private readonly GitHubIssuesSettings _settings;
    private readonly Action<GitHubIssue> _picked;
    private readonly GitHubGhClient _client = new();

    private readonly TextBox _search;
    private readonly CheckBox _mine;
    private readonly ListBox _issues;
    private readonly TextBlock _status;

    private IReadOnlyList<GitHubIssue> _all = [];

    public GitHubIssuePickerControl(GitHubIssuesSettings settings, Action<GitHubIssue> picked)
    {
        _settings = settings;
        _picked = picked;

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        _search = new TextBox { Watermark = "Filter by number, title or repo…", MinWidth = 260 };
        _search.TextChanged += (_, _) => _Render();

        _mine = new CheckBox { Content = "Assigned to me", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _mine.IsCheckedChanged += async (_, _) => await _LoadAsync();

        _issues = new ListBox { Margin = new Thickness(0, 8, 0, 0) };
        _issues.DoubleTapped += (_, _) => _Pick();

        var use = new Button { Content = "Track in this session", Classes = { "Accent" } };
        use.Click += (_, _) => _Pick();

        Content = new DockPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                _Docked(
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _search, _mine } },
                    Dock.Top),
                _Docked(
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 10, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { use },
                    },
                    Dock.Bottom),
                _Docked(_status, Dock.Bottom),
                _issues,
            },
        };

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        _status.Text = "Looking…";
        _issues.ItemsSource = null;

        try
        {
            _all = await _client.SearchOpenIssuesAsync(
                _settings.GhOwner,
                _mine.IsChecked == true,
                forceRefresh: false,
                CancellationToken.None,
                _settings.PickerTerms);

            _status.Text = _all.Count == 0 ? "No open issues here." : string.Empty;
            _Render();
        }
        catch (Exception exception)
        {
            _all = [];
            _status.Text = exception.Message;
        }
    }

    private void _Render()
    {
        var term = _search.Text?.Trim();

        var matches = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(issue =>
                issue.Number.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
                || issue.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || issue.Repository.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        _issues.ItemsSource = matches.Select(issue => new IssueRow(issue)).ToList();

        if (_issues.ItemCount > 0)
        {
            _issues.SelectedIndex = 0;
        }
    }

    private void _Pick()
    {
        if (_issues.SelectedItem is IssueRow row)
        {
            _picked(row.Issue);
        }
    }

    private static Control _Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private sealed record IssueRow(GitHubIssue Issue)
    {
        public override string ToString() => $"{Issue.Repository}#{Issue.Number} · {Issue.Title}";
    }
}
