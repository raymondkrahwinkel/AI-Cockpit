using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.GitHubIssues.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubIssues.UI;

// Picks a GitHub issue for one session (#77). Opened from that session's own header, so the issue lands on the pane
// you opened it from. A list of the open issues for the owner you configured, and a box to narrow it — the question
// is "which of these am I working on here", and nothing else belongs on screen. Scoped to the repositories the
// session's project is linked to when it has any (AC-548/AC-940), the same as the full issues dialog.
//
// AC-1396: the backend part resolves those repositories and runs the gh search (GitHubIssuesChannel.PickerIssues);
// this control no longer creates a gh client or reads the project link itself.
internal sealed class GitHubIssuePickerControl : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly string? _paneId;
    private readonly Func<GitHubIssue, Task> _picked;

    private readonly TextBox _search;
    private readonly CheckBox _mine;
    private readonly ListBox _issues;
    private readonly TextBlock _status;

    private IReadOnlyList<GitHubIssue> _all = [];

    public GitHubIssuePickerControl(ICockpitUiHost host, string? paneId, Func<GitHubIssue, Task> picked)
    {
        _host = host;
        _paneId = paneId;
        _picked = picked;

        _status = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        _search = new TextBox { PlaceholderText = "Filter by number, title or repo…", MinWidth = 260 };
        _search.TextChanged += (_, _) => _Render();

        _mine = new CheckBox { Content = "Assigned to me", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _mine.IsCheckedChanged += async (_, _) => await _LoadAsync();

        _issues = new ListBox { Margin = new Thickness(0, 8, 0, 0) };
        _issues.DoubleTapped += async (_, _) => await _PickAsync();

        var use = new Button { Content = "Track in this session", Classes = { "Accent" } };
        use.Click += async (_, _) => await _PickAsync();

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

        AttachedToVisualTree += _OnFirstAttached;
    }

    // AC-1396: the first load, explicit rather than fired from the constructor and forgotten; the first attach
    // awaits it, and a caller or a test may await it too.
    internal Task InitializeAsync() => _LoadAsync();

    // An event handler is the one place that cannot hand the task back, so the load is awaited and caught here.
    private async void _OnFirstAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachedToVisualTree -= _OnFirstAttached;
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    private async Task _LoadAsync()
    {
        _status.Text = "Looking…";
        _issues.ItemsSource = null;

        try
        {
            _all = await _host.Channel.AskAsync<IReadOnlyList<GitHubIssue>>(
                GitHubIssuesChannel.PickerIssues,
                new GitHubIssuesPickerRequest(_paneId, _mine.IsChecked == true)) ?? [];

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

    private async Task _PickAsync()
    {
        if (_issues.SelectedItem is not IssueRow row)
        {
            return;
        }

        // AC-1396: linking is a round trip to the backend part now, so a failure is said here rather than lost.
        try
        {
            await _picked(row.Issue);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
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
