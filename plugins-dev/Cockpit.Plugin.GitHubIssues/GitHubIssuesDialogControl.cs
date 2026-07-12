using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The "GitHub Issues" dialog opened from the left-menu button: a repository filter, a search box, and a
/// sortable <see cref="DataGrid"/> of open issues (across all repos in GitHub CLI mode, or one repo in HTTP
/// mode) on the left, and a details panel on the right showing the selected issue's title, repository, body,
/// a link, and a preview of the prompt it would produce (with a copy button). The repository filter is
/// populated from the distinct <see cref="GitHubIssue.Repository"/> values in the loaded issues plus an
/// "All" entry (default); it filters the grid client-side, no extra API calls. "Add to prompt" injects the
/// prompt into the active session and only shows when one is active; the copy button always works. Built in
/// code; the DataGrid theme is provided app-wide by the host.
/// </summary>
internal sealed class GitHubIssuesDialogControl : UserControl
{
    private const string AllRepositoriesOption = "All";

    private readonly GitHubIssuesSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly GitHubIssuesClient _http = new();
    private readonly GitHubGhClient _gh = new();

    private readonly ComboBox _repoFilter;
    private readonly CheckBox _assignedToMe;
    private readonly TextBox _search;
    private readonly TextBlock _status;
    private readonly DataGrid _grid;

    private readonly TextBlock _detailPlaceholder;
    private readonly DockPanel _detailContent;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _detailMeta;
    private readonly Button _inject;
    private readonly SelectableTextBlock _detailBody;
    private readonly SelectableTextBlock _promptPreview;
    private readonly TextBlock _detailStatus;

    private IReadOnlyList<GitHubIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    public GitHubIssuesDialogControl(GitHubIssuesSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        _repoFilter = new ComboBox
        {
            ItemsSource = new List<string> { AllRepositoriesOption },
            SelectedIndex = 0,
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _repoFilter.SelectionChanged += (_, _) => _ApplyFilter();

        // Assigned-to-me narrows the fetch server-side (gh --assignee @me, or the REST assignee filter), so a
        // toggle re-loads rather than filtering the already-fetched list client-side.
        _assignedToMe = new CheckBox
        {
            Content = "Assigned to me",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _assignedToMe.IsCheckedChanged += async (_, _) => await _LoadAsync(forceRefresh: true);

        _search = new TextBox { PlaceholderText = "Filter by title, repository or number…", Width = 320 };
        _search.TextChanged += (_, _) => _ApplyFilter();

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await _LoadAsync(forceRefresh: true);

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
        _grid.SelectionChanged += (_, _) => _ShowDetail(_grid.SelectedItem as GitHubIssue);
        _grid.DoubleTapped += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubIssue);

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(_repoFilter, Dock.Left);
        DockPanel.SetDock(_assignedToMe, Dock.Left);
        topBar.Children.Add(refresh);
        topBar.Children.Add(_repoFilter);
        topBar.Children.Add(_assignedToMe);
        topBar.Children.Add(_search);

        // Details panel (right).
        _detailTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _detailMeta = new TextBlock { FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubIssue);
        var openBrowser = new Button { Content = "Open in browser" };
        openBrowser.Click += (_, _) => _OpenInBrowser(_grid.SelectedItem as GitHubIssue);
        var detailButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        detailButtons.Children.Add(_inject);
        detailButtons.Children.Add(openBrowser);

        _detailBody = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _promptPreview = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            FontFamily = _MonoFont(),
        };
        _detailStatus = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitAccentBrush"), Margin = new Thickness(0, 2, 0, 0) };

        var copyButton = new Button { Content = "⧉ Copy", FontSize = 11, Padding = new Thickness(8, 2) };
        copyButton.Click += async (_, _) => await _CopyPromptAsync();
        var promptHeader = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(copyButton, Dock.Right);
        promptHeader.Children.Add(copyButton);
        promptHeader.Children.Add(new TextBlock { Text = "Prompt preview", FontWeight = FontWeight.SemiBold, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });

        var promptBlock = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = _promptPreview,
        };

        var detailHeader = new StackPanel
        {
            Children =
            {
                _detailTitle,
                _detailMeta,
                detailButtons,
            },
        };
        DockPanel.SetDock(detailHeader, Dock.Top);

        var detailScroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Description", FontWeight = FontWeight.SemiBold, FontSize = 11, Opacity = 0.7 },
                    _detailBody,
                    promptHeader,
                    promptBlock,
                    _detailStatus,
                },
            },
        };

        _detailContent = new DockPanel { IsVisible = false };
        _detailContent.Children.Add(detailHeader);
        _detailContent.Children.Add(detailScroll);

        _detailPlaceholder = new TextBlock
        {
            Text = "Select an issue to see its details.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var detailPanel = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = _Brush("CockpitSecondaryBgBrush"),
            CornerRadius = new CornerRadius(6),
            Child = new Panel { Children = { _detailPlaceholder, _detailContent } },
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*") };
        Grid.SetColumn(_grid, 0);
        Grid.SetColumn(detailPanel, 1);
        split.Children.Add(_grid);
        split.Children.Add(detailPanel);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(topBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(topBar);
        root.Children.Add(_status);
        root.Children.Add(split);
        Content = root;

        _ = _LoadAsync(forceRefresh: false);
    }

    private async Task _LoadAsync(bool forceRefresh)
    {
        _status.Text = "Loading…";
        try
        {
            var assignedToMe = _assignedToMe.IsChecked == true;
            if (_settings.UseGitHubCli)
            {
                _all = await _gh.SearchOpenIssuesAsync(_settings.GhOwner, assignedToMe, forceRefresh, CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
                {
                    _status.Text = "Set a repository in settings, or turn on the GitHub CLI.";
                    return;
                }

                _all = await _http.GetOpenIssuesAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe, CancellationToken.None);
            }

            _PopulateRepoFilter();
            _ApplyFilter();
            _status.Text = $"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    // Rebuilds the repository dropdown from the distinct repositories in the freshly loaded issues, keeping
    // the previous selection if it is still present (otherwise falls back to "All").
    private void _PopulateRepoFilter()
    {
        var previousSelection = _repoFilter.SelectedItem as string;
        var repositories = _all
            .Select(issue => issue.Repository)
            .Where(repository => !string.IsNullOrEmpty(repository))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(repository => repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var options = new List<string> { AllRepositoriesOption };
        options.AddRange(repositories);
        _repoFilter.ItemsSource = options;
        _repoFilter.SelectedItem = previousSelection is not null && options.Contains(previousSelection)
            ? previousSelection
            : AllRepositoriesOption;
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        var selectedRepo = _repoFilter.SelectedItem as string;
        IEnumerable<GitHubIssue> filtered = _all;
        if (!string.IsNullOrEmpty(selectedRepo) && selectedRepo != AllRepositoriesOption)
        {
            filtered = filtered.Where(issue => string.Equals(issue.Repository, selectedRepo, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(issue =>
                issue.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || issue.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || issue.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _grid.ItemsSource = new ObservableCollection<GitHubIssue>(filtered);
    }

    private void _ShowDetail(GitHubIssue? issue)
    {
        _detailStatus.Text = string.Empty;
        if (issue is null)
        {
            _detailContent.IsVisible = false;
            _detailPlaceholder.IsVisible = true;
            return;
        }

        _detailPlaceholder.IsVisible = false;
        _detailContent.IsVisible = true;
        _detailTitle.Text = issue.Title;
        _detailMeta.Text = $"{issue.Repository}  ·  #{issue.Number}  ·  {issue.Url}";
        _detailBody.Text = string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body;
        _renderedPrompt = _RenderPrompt(issue);
        _promptPreview.Text = _renderedPrompt;

        // "Add to prompt" only makes sense with a live session; otherwise the copy button is the way to grab it.
        _inject.IsVisible = _actions.HasActiveSession;
    }

    private string _RenderPrompt(GitHubIssue issue)
    {
        var parts = issue.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        return PromptTemplate.Render(_settings.Template, issue, owner, repo);
    }

    private void _AddToPrompt(GitHubIssue? issue)
    {
        if (issue is null)
        {
            _status.Text = "Select an issue first.";
            return;
        }

        if (!_actions.HasActiveSession)
        {
            _detailStatus.Text = "No active session — use Copy to put the prompt on the clipboard.";
            return;
        }

        _ = _actions.InjectIntoActiveSessionAsync(_RenderPrompt(issue));
        _detailStatus.Text = $"✓ Added issue #{issue.Number} to the active session's prompt.";
    }

    private async Task _CopyPromptAsync()
    {
        if (string.IsNullOrEmpty(_renderedPrompt))
        {
            return;
        }

        await _actions.SetClipboardTextAsync(_renderedPrompt);
        _detailStatus.Text = "✓ Prompt copied to the clipboard.";
    }

    private void _OpenInBrowser(GitHubIssue? issue)
    {
        if (issue is null || string.IsNullOrWhiteSpace(issue.Url))
        {
            _status.Text = "Select an issue first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(issue.Url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _detailStatus.Text = $"Could not open the browser: {exception.Message}";
        }
    }

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : new FontFamily("Cascadia Mono, Consolas, monospace");

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
