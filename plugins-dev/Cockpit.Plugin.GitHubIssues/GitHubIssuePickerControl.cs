using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Picks a GitHub issue for one session (#77). Opened from that session's own header, so the issue lands on the pane
/// you opened it from. A list of the open issues for the owner you configured, and a box to narrow it — the question
/// is "which of these am I working on here", and nothing else belongs on screen. Scoped to the repository the
/// session's project is linked to when one is (AC-548), the same as the full issues dialog.
/// </summary>
internal sealed class GitHubIssuePickerControl : UserControl
{
    private readonly GitHubIssuesSettings _settings;
    private readonly ICockpitHost _host;
    private readonly string? _paneId;
    private readonly Action<GitHubIssue> _picked;
    private readonly GitHubGhClient _client = new();

    private readonly TextBox _search;
    private readonly CheckBox _mine;
    private readonly ListBox _issues;
    private readonly TextBlock _status;

    private IReadOnlyList<GitHubIssue> _all = [];

    public GitHubIssuePickerControl(GitHubIssuesSettings settings, ICockpitHost host, string? paneId, Action<GitHubIssue> picked)
    {
        _settings = settings;
        _host = host;
        _paneId = paneId;
        _picked = picked;

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        _search = new TextBox { PlaceholderText = "Filter by number, title or repo…", MinWidth = 260 };
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
            // AC-548: the same resolution the issues dialog uses (GitHubRepositoryField.ResolvePreferredRepositoryAsync)
            // — the session's own linked repository wins, instead of this picker only ever searching every
            // repository the owner has. Sent as a search qualifier alongside PickerTerms, the same way the dialog's
            // label filter is (GitHubGhClient.LabelSearchTerm).
            var linkedRepository = await GitHubRepositoryField.ResolvePreferredRepositoryAsync(_host, _paneId, CancellationToken.None);
            var extraTerms = string.Join(
                ' ',
                new[] { linkedRepository is { Length: > 0 } repo ? GitHubGhClient.RepoSearchTerm(repo) : null, _settings.PickerTerms }
                    .Where(term => !string.IsNullOrWhiteSpace(term)));

            // The truncation signal (AC-519) is a dialog-only concern so far — this picker has never warned about a
            // capped page and stays out of that scope here; only the loaded issues are kept.
            (_all, _) = await _client.SearchOpenIssuesAsync(
                _settings.GhOwner,
                _mine.IsChecked == true,
                forceRefresh: false,
                CancellationToken.None,
                extraTerms.Length > 0 ? extraTerms : null);

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
