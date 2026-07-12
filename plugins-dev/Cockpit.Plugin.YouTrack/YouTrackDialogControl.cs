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
/// The "YouTrack Issues" dialog opened from the side-menu button (#48): an instance selector (which of the
/// configured <see cref="YouTrackInstance"/>s to query), a project filter (plus "All", populated from the
/// instance's admin API with a silent fallback to the projects already present in the fetched issues), a state
/// filter (plus "All", populated from the already-fetched issues' State/Stage custom field) and a search box,
/// driving a sortable <see cref="DataGrid"/> of open issues on the left plus a details panel on the right —
/// summary, state, description, a link, and a preview of the prompt it would produce (with a copy button).
/// Switching instance or project re-fetches (a different instance is a different server; a specific project
/// narrows the server-side query); switching state or typing a search term only re-filters the already-fetched
/// list, client-side. "Add to prompt" injects into the active session and only shows when one is active; the
/// copy button always works. Built in code; the DataGrid theme is provided app-wide by the host.
/// </summary>
internal sealed class YouTrackDialogControl : UserControl
{
    private const string AllOption = "All";
    private const int MaxResults = 100;

    // The project-filter's "every project" entry: a null Tag omits the server-side project: clause (#48).
    private static readonly YouTrackProjectOption AllProjectOption = new(null, AllOption);

    private readonly YouTrackSettings _settings;
    private readonly ICockpitActions _actions;
    private readonly YouTrackClient _client = new();

    private readonly ComboBox _instanceSelector;
    private readonly ComboBox _projectFilter;
    private readonly ComboBox _stateFilter;
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

    private IReadOnlyList<YouTrackIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    // Guards the project-filter reset that _OnInstanceChangedAsync does after fetching the new instance's
    // projects: setting _projectFilter.SelectedItem there would otherwise also fire _OnProjectChangedAsync
    // and trigger a second, redundant issues fetch before the first one (driven explicitly below) even ran.
    private bool _isSyncingProjectFilter;

    public YouTrackDialogControl(YouTrackSettings settings, ICockpitActions actions)
    {
        _settings = settings;
        _actions = actions;

        _instanceSelector = new ComboBox
        {
            ItemsSource = settings.Instances,
            Width = 160,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _instanceSelector.SelectionChanged += async (_, _) => await _OnInstanceChangedAsync();

        _projectFilter = new ComboBox
        {
            ItemsSource = new List<YouTrackProjectOption> { AllProjectOption },
            SelectedIndex = 0,
            Width = 220,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _projectFilter.SelectionChanged += async (_, _) => await _OnProjectChangedAsync();

        _stateFilter = new ComboBox
        {
            ItemsSource = new List<string> { AllOption },
            SelectedIndex = 0,
            Width = 140,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _stateFilter.SelectionChanged += (_, _) => _ApplyFilter();

        // Assigned-to-me adds YouTrack's "for: me" clause to the server-side query, so a toggle re-fetches
        // rather than filtering the already-loaded list client-side.
        _assignedToMe = new CheckBox
        {
            Content = "Assigned to me",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _assignedToMe.IsCheckedChanged += async (_, _) => await _LoadIssuesAsync();

        _search = new TextBox { PlaceholderText = "Filter by id, summary or state…", Width = 260 };
        _search.TextChanged += (_, _) => _ApplyFilter();

        _status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await _LoadIssuesAsync();

        _grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        _grid.Columns.Add(new DataGridTextColumn { Header = "Project", Binding = new Binding(nameof(YouTrackIssue.Project)), Width = new DataGridLength(90) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding(nameof(YouTrackIssue.IdReadable)), Width = new DataGridLength(90) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Summary", Binding = new Binding(nameof(YouTrackIssue.Summary)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(YouTrackIssue.State)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.SelectionChanged += (_, _) => _ShowDetail(_grid.SelectedItem as YouTrackIssue);
        _grid.DoubleTapped += (_, _) => _AddToPrompt(_grid.SelectedItem as YouTrackIssue);

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(_instanceSelector, Dock.Left);
        DockPanel.SetDock(_projectFilter, Dock.Left);
        DockPanel.SetDock(_stateFilter, Dock.Left);
        DockPanel.SetDock(_assignedToMe, Dock.Left);
        topBar.Children.Add(refresh);
        topBar.Children.Add(_instanceSelector);
        topBar.Children.Add(_projectFilter);
        topBar.Children.Add(_stateFilter);
        topBar.Children.Add(_assignedToMe);
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

        _Initialize();
    }

    private void _Initialize()
    {
        if (_settings.Instances.Count == 0)
        {
            _status.Text = "No YouTrack instances configured — add one in settings.";
            return;
        }

        // Fires _OnInstanceChangedAsync, which loads that instance's projects and issues.
        _instanceSelector.SelectedIndex = 0;
    }

    private async Task _OnInstanceChangedAsync()
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return;
        }

        _status.Text = "Loading projects…";
        var projects = string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token)
            ? []
            : await _client.GetProjectsAsync(instance.InstanceUrl, instance.Token, CancellationToken.None);

        var options = new List<YouTrackProjectOption> { AllProjectOption };
        options.AddRange(projects
            .OrderBy(project => project.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(project => new YouTrackProjectOption(
                project.ShortName,
                string.IsNullOrWhiteSpace(project.Name) ? project.ShortName : $"{project.ShortName} - {project.Name}")));

        _isSyncingProjectFilter = true;
        _projectFilter.ItemsSource = options;
        _projectFilter.SelectedItem = options.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(instance.DefaultProjectTag)
            && string.Equals(option.Tag, instance.DefaultProjectTag, StringComparison.OrdinalIgnoreCase))
            ?? AllProjectOption;
        _isSyncingProjectFilter = false;

        await _LoadIssuesAsync();
    }

    private async Task _OnProjectChangedAsync()
    {
        if (_isSyncingProjectFilter)
        {
            return;
        }

        await _LoadIssuesAsync();
    }

    private async Task _LoadIssuesAsync()
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            _status.Text = _settings.Instances.Count == 0
                ? "No YouTrack instances configured — add one in settings."
                : "Select an instance.";
            return;
        }

        _status.Text = "Loading…";
        try
        {
            if (string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token))
            {
                _status.Text = $"\"{instance.Label}\" is missing an instance URL or token — check settings.";
                _all = [];
                _ApplyFilter();
                return;
            }

            // A null Tag (the "All" option) omits the project: clause and queries every project on the instance.
            var projectTag = (_projectFilter.SelectedItem as YouTrackProjectOption)?.Tag;

            _all = await _client.GetOpenIssuesAsync(instance.InstanceUrl, instance.Token, projectTag, extraFilter: null, _assignedToMe.IsChecked == true, MaxResults, CancellationToken.None);
            _PopulateStateFilter();
            _ApplyFilter();
            _status.Text = $"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not load issues: {exception.Message}";
        }
    }

    // Rebuilds the state dropdown from the distinct states in the freshly loaded issues, keeping the previous
    // selection if it is still present (otherwise falls back to "All") — mirrors the GitHub Issues dialog's
    // repository-filter population, just on the State/Stage custom field instead.
    private void _PopulateStateFilter()
    {
        var previousSelection = _stateFilter.SelectedItem as string;
        var states = _all
            .Select(issue => issue.State)
            .Where(state => !string.IsNullOrEmpty(state))
            .Select(state => state!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(state => state, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var options = new List<string> { AllOption };
        options.AddRange(states);
        _stateFilter.ItemsSource = options;
        _stateFilter.SelectedItem = previousSelection is not null && options.Contains(previousSelection)
            ? previousSelection
            : AllOption;
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        var selectedState = _stateFilter.SelectedItem as string;
        IEnumerable<YouTrackIssue> filtered = _all;
        if (!string.IsNullOrEmpty(selectedState) && selectedState != AllOption)
        {
            filtered = filtered.Where(issue => string.Equals(issue.State, selectedState, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(issue =>
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
        var url = _BuildIssueUrl(issue);
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

        _ = _actions.InjectIntoActiveSessionAsync(PromptTemplate.Render(_settings.Template, issue, _BuildIssueUrl(issue)));
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
            Process.Start(new ProcessStartInfo(_BuildIssueUrl(issue)) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _detailStatus.Text = $"Could not open the browser: {exception.Message}";
        }
    }

    // The selected instance's base URL, not the issue's own project — an issue never carries its instance.
    private string _BuildIssueUrl(YouTrackIssue issue) =>
        _instanceSelector.SelectedItem is YouTrackInstance instance
            ? YouTrackClient.BuildIssueUrl(instance.InstanceUrl, issue.IdReadable)
            : string.Empty;

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : new FontFamily("Cascadia Mono, Consolas, monospace");

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    // A project-filter dropdown entry: Tag is the server-side query value (null = every project, the "All"
    // entry), Display is what the user sees — "SHORTNAME - Full Name", or just the short name when the
    // instance reports no name. ToString drives the ComboBox's default item rendering.
    private sealed record YouTrackProjectOption(string? Tag, string Display)
    {
        public override string ToString() => Display;
    }
}
