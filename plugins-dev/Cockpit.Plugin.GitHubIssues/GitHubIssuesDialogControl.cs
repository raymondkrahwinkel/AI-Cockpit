using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The "GitHub Issues" dialog opened from the left-menu button: a search box and a sortable
/// <see cref="DataGrid"/> of open issues (across all repos in GitHub CLI mode, or one repo in HTTP mode).
/// Selecting an issue and choosing "Add to prompt" (or double-clicking it) injects the rendered template
/// into the active session so the agent opens and reviews it. Built in code; the DataGrid theme is provided
/// app-wide by the host.
/// </summary>
internal sealed class GitHubIssuesDialogControl : UserControl
{
    private readonly GitHubIssuesSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly GitHubIssuesClient _http = new();
    private readonly GitHubGhClient _gh = new();

    private readonly TextBox _search;
    private readonly TextBlock _status;
    private readonly DataGrid _grid;
    private IReadOnlyList<GitHubIssue> _all = [];

    public GitHubIssuesDialogControl(GitHubIssuesSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        _search = new TextBox { PlaceholderText = "Filter by title, repository or number…", Width = 320 };
        _search.TextChanged += (_, _) => _ApplyFilter();

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await _LoadAsync();

        var add = new Button { Content = "Add to prompt", Classes = { "Accent" } };
        add.Click += (_, _) => _Inject(_grid.SelectedItem as GitHubIssue);

        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Repository", Binding = new Binding(nameof(GitHubIssue.Repository)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(GitHubIssue.Number)), Width = new DataGridLength(64) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Title", Binding = new Binding(nameof(GitHubIssue.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.DoubleTapped += (_, _) => _Inject(_grid.SelectedItem as GitHubIssue);

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(refresh);
        buttons.Children.Add(add);
        DockPanel.SetDock(buttons, Dock.Right);
        topBar.Children.Add(buttons);
        topBar.Children.Add(_search);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(topBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(topBar);
        root.Children.Add(_status);
        root.Children.Add(_grid);
        Content = root;

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        _status.Text = "Loading…";
        try
        {
            if (_settings.UseGitHubCli)
            {
                _all = await _gh.SearchOpenIssuesAsync(_settings.GhOwner, CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
                {
                    _status.Text = "Set a repository in settings, or turn on the GitHub CLI.";
                    return;
                }

                _all = await _http.GetOpenIssuesAsync(_settings.Owner, _settings.Repo, _settings.Token, CancellationToken.None);
            }

            _ApplyFilter();
            _status.Text = $"{_all.Count} open issue(s). Double-click one, or select it and choose Add to prompt.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        IEnumerable<GitHubIssue> filtered = _all;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = _all.Where(issue =>
                issue.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || issue.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || issue.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _grid.ItemsSource = new ObservableCollection<GitHubIssue>(filtered);
    }

    private void _Inject(GitHubIssue? issue)
    {
        if (issue is null)
        {
            _status.Text = "Select an issue first.";
            return;
        }

        var parts = issue.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        var prompt = PromptTemplate.Render(_settings.Template, issue, owner, repo);

        if (_actions.HasActiveSession)
        {
            _ = _actions.InjectIntoActiveSessionAsync(prompt);
            _status.Text = $"Added issue #{issue.Number} to the active session's prompt.";
        }
        else
        {
            _ = _actions.SetClipboardTextAsync(prompt);
            _status.Text = $"No active session — issue #{issue.Number} copied to the clipboard.";
        }
    }
}
