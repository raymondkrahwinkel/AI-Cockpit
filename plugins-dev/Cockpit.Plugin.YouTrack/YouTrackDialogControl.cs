using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The "YouTrack Issues" dialog opened from the side section's "View all issues" button: a search box and a
/// sortable <see cref="DataGrid"/> of open issues for the configured project on the left, and a details panel
/// on the right showing the selected issue's summary, state, description, a link, and a preview of the prompt
/// it would produce (with a copy button). "Add to prompt" injects the prompt into the active session and
/// only shows when one is active; the copy button always works. Built in code; the DataGrid theme is
/// provided app-wide by the host.
/// </summary>
internal sealed class YouTrackDialogControl : UserControl
{
    private const int MaxResults = 100;

    private readonly YouTrackSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly YouTrackClient _client = new();

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

    private IReadOnlyList<YouTrackIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    public YouTrackDialogControl(YouTrackSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        _search = new TextBox { PlaceholderText = "Filter by id, summary or state…", Width = 320 };
        _search.TextChanged += (_, _) => _ApplyFilter();

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await _LoadAsync();

        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding(nameof(YouTrackIssue.IdReadable)), Width = new DataGridLength(90) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Summary", Binding = new Binding(nameof(YouTrackIssue.Summary)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(YouTrackIssue.State)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => _ShowDetail(_grid.SelectedItem as YouTrackIssue);
        _grid.DoubleTapped += (_, _) => _AddToPrompt(_grid.SelectedItem as YouTrackIssue);

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Right);
        topBar.Children.Add(refresh);
        topBar.Children.Add(_search);

        // Details panel (right).
        _detailTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _detailMeta = new TextBlock { FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as YouTrackIssue);
        var openBrowser = new Button { Content = "Open in browser" };
        openBrowser.Click += (_, _) => _OpenInBrowser(_grid.SelectedItem as YouTrackIssue);
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

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        _status.Text = "Loading…";
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.InstanceUrl) || string.IsNullOrWhiteSpace(_settings.Token) || string.IsNullOrWhiteSpace(_settings.ProjectTag))
            {
                _status.Text = "Set the instance URL, token and project in settings.";
                return;
            }

            _all = await _client.GetOpenIssuesAsync(_settings.InstanceUrl, _settings.Token, _settings.ProjectTag, _settings.ExtraQuery, MaxResults, CancellationToken.None);
            _ApplyFilter();
            _status.Text = $"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        IEnumerable<YouTrackIssue> filtered = _all;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = _all.Where(issue =>
                issue.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                || issue.IdReadable.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (issue.State?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _grid.ItemsSource = new ObservableCollection<YouTrackIssue>(filtered);
    }

    private void _ShowDetail(YouTrackIssue? issue)
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
        _detailTitle.Text = issue.Summary;
        var url = YouTrackClient.BuildIssueUrl(_settings.InstanceUrl, issue.IdReadable);
        _detailMeta.Text = $"{issue.IdReadable}  ·  {issue.State ?? "(no state)"}  ·  {url}";
        _detailBody.Text = string.IsNullOrWhiteSpace(issue.Description) ? "(no description)" : issue.Description;
        _renderedPrompt = PromptTemplate.Render(_settings.Template, issue, url);
        _promptPreview.Text = _renderedPrompt;

        // "Add to prompt" only makes sense with a live session; otherwise the copy button is the way to grab it.
        _inject.IsVisible = _actions.HasActiveSession;
    }

    private void _AddToPrompt(YouTrackIssue? issue)
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

        var url = YouTrackClient.BuildIssueUrl(_settings.InstanceUrl, issue.IdReadable);
        _ = _actions.InjectIntoActiveSessionAsync(PromptTemplate.Render(_settings.Template, issue, url));
        _detailStatus.Text = $"✓ Added issue {issue.IdReadable} to the active session's prompt.";
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

    private void _OpenInBrowser(YouTrackIssue? issue)
    {
        if (issue is null || string.IsNullOrWhiteSpace(issue.IdReadable))
        {
            _status.Text = "Select an issue first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(YouTrackClient.BuildIssueUrl(_settings.InstanceUrl, issue.IdReadable)) { UseShellExecute = true });
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
