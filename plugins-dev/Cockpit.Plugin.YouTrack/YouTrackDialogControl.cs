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
    private readonly ICockpitHost _host;
    private readonly ICockpitActions _actions;
    private readonly SessionIssueLinks _links;
    private readonly YouTrackClient _client = new();
    private readonly YouTrackWorkflow _workflow;

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
    private readonly Button _start;
    private readonly Button _setState;
    private readonly Button _link;
    private readonly SelectableTextBlock _detailBody;
    private readonly SelectableTextBlock _promptPreview;
    private readonly TextBlock _detailStatus;

    private IReadOnlyList<YouTrackIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    // The selected issue's status field, as its project defines it (#75) — what it may become, and whether a
    // workflow governs it. Loaded per selection, so the action buttons only offer what the board allows.
    private YouTrackIssueFields? _fields;
    private int _fieldsToken;

    // Guards the project-filter reset that _OnInstanceChangedAsync does after fetching the new instance's
    // projects: setting _projectFilter.SelectedItem there would otherwise also fire _OnProjectChangedAsync
    // and trigger a second, redundant issues fetch before the first one (driven explicitly below) even ran.
    private bool _isSyncingProjectFilter;

    public YouTrackDialogControl(YouTrackSettings settings, ICockpitHost host, SessionIssueLinks links)
    {
        _settings = settings;
        _host = host;
        _actions = host.Actions;
        _links = links;
        _workflow = new YouTrackWorkflow(_client);

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

        // The workflow actions (#75). Each hides itself when the board cannot back it: no in-progress state, no
        // states to move to, or no session to attach the issue to.
        _start = new Button { Content = "Start", IsVisible = false };
        _start.Click += async (_, _) => await _StartAsync(_grid.SelectedItem as YouTrackIssue);
        _setState = new Button { Content = "Set state ▾", IsVisible = false };
        _setState.Click += (_, _) => _ShowStateMenu(_grid.SelectedItem as YouTrackIssue);
        _link = new Button { Content = "Link to session", IsVisible = false };
        _link.Click += (_, _) => _LinkToActiveSession(_grid.SelectedItem as YouTrackIssue);

        var detailButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { _inject, _start, _setState, _link, openBrowser })
        {
            button.Margin = new Thickness(0, 0, 6, 6);
            detailButtons.Children.Add(button);
        }

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
        _link.IsVisible = _host.Sessions.ActivePaneId is { Length: > 0 };
        _ = _LoadFieldsAsync(issue);
    }

    // What this issue's project allows, read per selection: until it is known, the status actions stay hidden
    // rather than being offered and then refused.
    private async Task _LoadFieldsAsync(YouTrackIssue issue)
    {
        _fields = null;
        _start.IsVisible = false;
        _setState.IsVisible = false;

        if (_instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return;
        }

        var token = ++_fieldsToken;
        try
        {
            var fields = await _client.GetIssueFieldsAsync(instance.InstanceUrl, instance.Token, issue, CancellationToken.None);
            if (token != _fieldsToken || !ReferenceEquals(_grid.SelectedItem, issue))
            {
                return;
            }

            _fields = fields;
            _setState.IsVisible = fields.State?.AvailableTargets.Count > 0;
            _start.IsVisible = fields.State is { } state && YouTrackWorkflow.FindStartTarget(state) is not null;
        }
        catch (Exception exception)
        {
            if (token == _fieldsToken)
            {
                _detailStatus.Text = $"Could not read this issue's states: {exception.Message}";
            }
        }
    }

    // Start = the three steps Raymond starts a ticket with: move it to in progress, put his name on it, and tie
    // it to the session he is going to do the work in.
    private async Task _StartAsync(YouTrackIssue? issue)
    {
        if (issue is null || _instanceSelector.SelectedItem is not YouTrackInstance instance
            || _fields is not { State: { } state } fields
            || YouTrackWorkflow.FindStartTarget(state) is not { } target)
        {
            return;
        }

        _start.IsEnabled = false;
        try
        {
            _detailStatus.Text = await _workflow.StartAsync(instance, issue, fields, target, CancellationToken.None);
            _LinkToActiveSession(issue);
            await _LoadIssuesAsync();
        }
        catch (Exception exception)
        {
            _detailStatus.Text = $"Could not start {issue.IdReadable}: {exception.Message}";
        }
        finally
        {
            _start.IsEnabled = true;
        }
    }

    private void _ShowStateMenu(YouTrackIssue? issue)
    {
        if (issue is null || _fields?.State is not { } state)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = _setState };
        var items = new List<MenuItem>();
        foreach (var target in state.AvailableTargets)
        {
            var item = new MenuItem { Header = target };
            item.Click += async (_, _) => await _SetStateAsync(issue, target);
            items.Add(item);
        }

        menu.ItemsSource = items;
        menu.Open(_setState);
    }

    private async Task _SetStateAsync(YouTrackIssue issue, string target)
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance || _fields?.State is not { } state)
        {
            return;
        }

        try
        {
            await _client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, state, target, CancellationToken.None);
            _detailStatus.Text = $"✓ {issue.IdReadable} → {target}.";
            await _LoadIssuesAsync();
        }
        catch (Exception exception)
        {
            _detailStatus.Text = $"Could not move {issue.IdReadable}: {exception.Message}";
        }
    }

    // Ties the issue to the session pane that is selected right now, which is the one the header item showing it
    // sits in — the dialog itself belongs to no session.
    private void _LinkToActiveSession(YouTrackIssue? issue)
    {
        if (issue is null || _instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return;
        }

        if (_host.Sessions.ActivePaneId is not { Length: > 0 } paneId)
        {
            _detailStatus.Text = "No active session to link this issue to.";
            return;
        }

        _links.Link(paneId, new LinkedIssue(instance, issue));
        _detailStatus.Text = $"✓ {issue.IdReadable} linked to the active session.";
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
