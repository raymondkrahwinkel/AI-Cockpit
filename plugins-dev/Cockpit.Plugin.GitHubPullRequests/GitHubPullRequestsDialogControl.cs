using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The "GitHub Pull Requests" dialog opened from the side section's "View all open PRs" button: a search
/// box and a sortable <see cref="DataGrid"/> of open pull requests (across all repos in GitHub CLI mode, or
/// one repo in HTTP mode) on the left, and a details panel on the right showing the selected pull request's
/// title, repository, author, body, a link, and a preview of the prompt it would produce (with a copy
/// button). "Add to prompt" injects the prompt into the active session and only shows when one is active;
/// the copy button always works. Built in code; the DataGrid theme is provided app-wide by the host.
/// </summary>
internal sealed class GitHubPullRequestsDialogControl : UserControl
{
    private readonly GitHubPullRequestsSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly GitHubPullRequestsClient _http = new();
    private readonly GitHubPrGhClient _gh = new();

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

    private IReadOnlyList<GitHubPullRequest> _all = [];
    private string _renderedPrompt = string.Empty;

    public GitHubPullRequestsDialogControl(GitHubPullRequestsSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        // Assigned-to-me narrows the fetch server-side (gh --assignee @me) or client-side against the token
        // user (HTTP), so a toggle re-loads rather than filtering the already-fetched list.
        _assignedToMe = new CheckBox
        {
            Content = "Assigned to me",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _assignedToMe.IsCheckedChanged += async (_, _) => await _LoadAsync(forceRefresh: true);

        _search = new TextBox { PlaceholderText = "Filter by title, repository, author or number…", Width = 320 };
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
        _grid.Columns.Add(new DataGridTextColumn { Header = "Repository", Binding = new Binding(nameof(GitHubPullRequest.Repository)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(GitHubPullRequest.Number)), Width = new DataGridLength(64) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Title", Binding = new Binding(nameof(GitHubPullRequest.Title)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Author", Binding = new Binding(nameof(GitHubPullRequest.Author)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => _ShowDetail(_grid.SelectedItem as GitHubPullRequest);
        _grid.DoubleTapped += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubPullRequest);

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(_assignedToMe, Dock.Left);
        topBar.Children.Add(refresh);
        topBar.Children.Add(_assignedToMe);
        topBar.Children.Add(_search);

        // Details panel (right).
        _detailTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _detailMeta = new TextBlock { FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubPullRequest);
        var openBrowser = new Button { Content = "Open in browser" };
        openBrowser.Click += (_, _) => _OpenInBrowser(_grid.SelectedItem as GitHubPullRequest);
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
            Text = "Select a pull request to see its details.",
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
                _all = await _gh.SearchOpenPullRequestsAsync(_settings.GhOwner, assignedToMe, forceRefresh, CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
                {
                    _status.Text = "Set a repository in settings, or turn on the GitHub CLI.";
                    return;
                }

                _all = await _http.GetOpenPullRequestsAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe, CancellationToken.None);
            }

            _ApplyFilter();
            _status.Text = $"{_all.Count} open pull request(s). Click one for details, or double-click to add it to the prompt.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load pull requests: {exception.Message}";
        }
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        IEnumerable<GitHubPullRequest> filtered = _all;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = _all.Where(pullRequest =>
                pullRequest.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pullRequest.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pullRequest.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pullRequest.Number.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _grid.ItemsSource = new ObservableCollection<GitHubPullRequest>(filtered);
    }

    private void _ShowDetail(GitHubPullRequest? pullRequest)
    {
        _detailStatus.Text = string.Empty;
        if (pullRequest is null)
        {
            _detailContent.IsVisible = false;
            _detailPlaceholder.IsVisible = true;
            return;
        }

        _detailPlaceholder.IsVisible = false;
        _detailContent.IsVisible = true;
        _detailTitle.Text = pullRequest.Title;
        _detailMeta.Text = $"{pullRequest.Repository}  ·  #{pullRequest.Number}  ·  by {(string.IsNullOrWhiteSpace(pullRequest.Author) ? "(unknown)" : pullRequest.Author)}  ·  {pullRequest.Url}";
        _detailBody.Text = string.IsNullOrWhiteSpace(pullRequest.Body) ? "(no description)" : pullRequest.Body;
        _renderedPrompt = _RenderPrompt(pullRequest);
        _promptPreview.Text = _renderedPrompt;

        // "Add to prompt" only makes sense with a live session; otherwise the copy button is the way to grab it.
        _inject.IsVisible = _actions.HasActiveSession;
    }

    private string _RenderPrompt(GitHubPullRequest pullRequest)
    {
        var parts = pullRequest.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        return PromptTemplate.Render(_settings.Template, pullRequest, owner, repo);
    }

    private void _AddToPrompt(GitHubPullRequest? pullRequest)
    {
        if (pullRequest is null)
        {
            _status.Text = "Select a pull request first.";
            return;
        }

        if (!_actions.HasActiveSession)
        {
            _detailStatus.Text = "No active session — use Copy to put the prompt on the clipboard.";
            return;
        }

        _ = _actions.InjectIntoActiveSessionAsync(_RenderPrompt(pullRequest));
        _detailStatus.Text = $"✓ Added pull request #{pullRequest.Number} to the active session's prompt.";
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

    private void _OpenInBrowser(GitHubPullRequest? pullRequest)
    {
        if (pullRequest is null || string.IsNullOrWhiteSpace(pullRequest.Url))
        {
            _status.Text = "Select a pull request first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(pullRequest.Url) { UseShellExecute = true });
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
