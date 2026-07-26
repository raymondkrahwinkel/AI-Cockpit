using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The "YouTrack Issues" dialog opened from the side-menu button (#48): an instance selector (which of the
/// configured <see cref="YouTrackInstance"/>s to query), a project filter (plus "All", populated from the
/// instance's admin API with a silent fallback to the projects already present in the fetched issues), a state
/// filter (plus "All", populated from the already-fetched issues' State/Stage custom field) and a search box,
/// driving a sortable <see cref="DataGrid"/> of open issues on the left plus a details panel on the right —
/// summary, chips, a rendered description, a fixed action toolbar and a collapsible preview of the prompt it
/// would produce (with a copy button). Switching instance or project re-fetches (a different instance is a
/// different server; a specific project narrows the server-side query); switching state or typing a search term
/// only re-filters the already-fetched list, client-side. "Add to prompt" injects into the active session; "New
/// session" (AC-298) hands the same prompt to the cockpit's own New-session dialog instead. Built in code; the
/// DataGrid theme is provided app-wide by the host.
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
    private readonly IssueStateChanges _stateChanges;
    private readonly YouTrackClient _client = new();
    private readonly YouTrackWorkflow _workflow;

    private readonly ComboBox _instanceSelector;
    private readonly ComboBox _projectFilter;
    private readonly ComboBox _stateFilter;
    private readonly CheckBox _assignedToMe;
    private readonly TextBox _search;

    // The window-level status line, along the bottom edge of the dialog: fetch/load/refresh state and the guard
    // messages ("no instance configured", "select an issue first") that fire before any issue is even selected.
    // Always present; what an action on a selected issue did is reported by _detailStatus, inside the panel that
    // issue is shown in, so the two sit in different places and never carry the same message (AC-299).
    private readonly TextBlock _status;
    private readonly ProgressBar _loading = LoadingBar.Build();
    private readonly DataGrid _grid;

    private readonly TextBlock _detailPlaceholder;
    private readonly DockPanel _detailContent;
    private readonly TextBlock _detailId;
    private readonly TextBlock _detailTitle;
    private readonly WrapPanel _detailChips;
    private readonly Button _inject;
    private readonly Button _newSession;
    private readonly Button _setState;
    private readonly Button _overflow;
    private readonly ContentControl _detailBody;
    private readonly SelectableTextBlock _promptPreview;

    // The detail panel's own status line: the outcome of an action taken on the selected issue (Start work, Set
    // state, Link to session, Copy, Add to prompt, New session). Only ever visible while the panel itself is, so
    // it never has to repeat what _status already said (AC-299).
    private readonly TextBlock _detailStatus;

    private IReadOnlyList<YouTrackIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    // Which issue the line in _detailStatus is about. A result belongs to the issue it was produced for, not to
    // the grid event that happened to be in flight: Start work and Set state report their outcome and then reload,
    // and that reload raises SelectionChanged twice — once on the empty grid, once back on the same issue. Clearing
    // on every selection change wiped the message before the operator could read it (AC-292).
    private string? _detailStatusFor;

    // The selected issue's status field, as its project defines it (#75) — what it may become, and whether a
    // workflow governs it. Loaded per selection, so Set state only offers what the board allows.
    private YouTrackIssueFields? _fields;
    private int _fieldsToken;

    // Guards the project-filter reset that _OnInstanceChangedAsync does after fetching the new instance's
    // projects: setting _projectFilter.SelectedItem there would otherwise also fire _OnProjectChangedAsync
    // and trigger a second, redundant issues fetch before the first one (driven explicitly below) even ran.
    private bool _isSyncingProjectFilter;

    public YouTrackDialogControl(YouTrackSettings settings, ICockpitHost host, SessionIssueLinks links, IssueStateChanges stateChanges)
    {
        _settings = settings;
        _host = host;
        _actions = host.Actions;
        _links = links;
        _stateChanges = stateChanges;
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

        // Details panel (right). Id + summary, with the issue's url moved off the text and onto a single icon
        // button (AC-297) — it used to appear spelled out three times (the meta line, the prompt preview, and
        // "Open in browser"'s own label); now only the rendered prompt still carries it, because that copy is
        // the literal text a session receives.
        _detailId = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.65 };
        _detailTitle = new TextBlock { Name = "detailTitle", FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        var openLink = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.OpenInNew, Width = 15, Height = 15 },
            Padding = new Thickness(5),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(openLink, "Open in browser");
        openLink.Click += (_, _) => _OpenInBrowser();

        var titleRow = new DockPanel();
        DockPanel.SetDock(openLink, Dock.Right);
        titleRow.Children.Add(openLink);
        titleRow.Children.Add(new StackPanel { Children = { _detailId, _detailTitle } });

        // Chips replace the old single "{id} · {state} · {url}" line (AC-297) — State and Project are the two
        // fields YouTrackIssue actually carries; an Assignee or Updated chip is left out rather than invented,
        // since neither the list fetch nor YouTrackIssueFields carries an assignee's name or a timestamp.
        _detailChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };

        // Without this the button's own explanation of why it is inert never appears: Avalonia shows no tooltip
        // on a disabled control unless asked to.
        ToolTip.SetShowOnDisabled(_inject, true);
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as YouTrackIssue);

        _newSession = new Button { Content = "New session" };
        _newSession.Click += async (_, _) => await _StartNewSessionAsync();

        // Set state absorbs the old standalone Start button as its own top entry (AC-297): Start was never a
        // session action, it is a state mutation (in progress + assigned to the token owner + linked to the
        // session), so it belongs with the rest of what this project's board allows an issue to become.
        _setState = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Set state", VerticalAlignment = VerticalAlignment.Center },
                    new MaterialIcon { Kind = MaterialIconKind.ChevronDown, Width = 13, Height = 13 },
                },
            },
            IsEnabled = false,
        };
        _setState.Click += (_, _) => _ShowStateMenu();

        _overflow = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.DotsHorizontal, Width = 16, Height = 16 },
            Padding = new Thickness(8, 4),
        };
        ToolTip.SetTip(_overflow, "More actions");
        _overflow.Click += (_, _) => _ShowOverflowMenu();

        // A fixed row (AC-297): the same four controls, in the same order, whether or not Autopilot is installed
        // or a session is active — only Set state's own menu and the overflow menu's contents vary. A row that
        // rearranges itself depending on what happens to be available was judged worse than a button that
        // occasionally does nothing, so split-button and icon-only variants were both dropped.
        // The whole toolbar lives inside the detail panel, which is hidden until an issue is selected — which is
        // why the handlers behind these buttons read the selection and simply exit if it is somehow empty, rather
        // than telling the operator to select an issue they have plainly already selected.
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _inject, _newSession, _setState, _overflow },
        };

        // Description gets its own scroll area (AC-297) rather than sharing one with the prompt preview below it,
        // so a long description does not push the preview and status line out of view together with it. The width
        // cap does nothing at the default 3:2 split — the details column is nowhere near 680 there — it is the
        // ceiling for an operator who drags the splitter over, so the text stays a readable measure instead of
        // running the full width of the dialog.
        _detailBody = new ContentControl();
        var descriptionScroll = new ScrollViewer
        {
            Name = "descriptionScroll",
            Content = new Border { MaxWidth = 680, Margin = new Thickness(0, 10, 0, 0), Child = _detailBody },
        };

        // Deliberately not wrapped: the preview shows the prompt exactly as it will be sent, and re-flowing it
        // would misrepresent a text whose own line breaks are part of what goes out. Long lines scroll sideways
        // instead (see promptScroll). Wrapping would lay out fine — the height cap below is what keeps the
        // preview from crowding out the description; this is a readability choice, not a technical constraint.
        _promptPreview = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 11,
            FontFamily = _MonoFont(),
        };

        var copyButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.ContentCopy, Width = 13, Height = 13 },
                    new TextBlock { Text = "Copy", VerticalAlignment = VerticalAlignment.Center },
                },
            },
            FontSize = 11,
            Padding = new Thickness(8, 2),
        };
        copyButton.Click += async (_, _) => await _CopyPromptAsync();

        // Collapsed by default (AC-297): the preview used to sit permanently open and cost vertical room on every
        // selection even though most of what it shows — the rendered template — only matters right before Add to
        // prompt or Copy. The toggle and the copy button are separate hit targets, deliberately: copying should
        // not also flip the disclosure.
        var promptToggleIcon = new MaterialIcon { Kind = MaterialIconKind.ChevronRight, Width = 13, Height = 13 };
        var promptToggle = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    promptToggleIcon,
                    new TextBlock { Text = "Prompt preview", FontWeight = FontWeight.SemiBold, FontSize = 11, Opacity = 0.7 },
                },
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        // The preview quotes the whole description back inside the rendered template, so it is always taller than
        // the description it repeats. Docked at the bottom of the panel with nothing to bound it, it took every
        // pixel and left the description none. It gets a fixed reading height and scrolls within it: the
        // description is what the panel is for, the preview is a check on what Add to prompt is about to send.
        var promptScroll = new ScrollViewer
        {
            Name = "promptScroll",
            MaxHeight = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _promptPreview,
        };

        var promptBlock = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4, 0, 0),
            Child = promptScroll,
            IsVisible = false,
        };
        promptToggle.Click += (_, _) =>
        {
            promptBlock.IsVisible = !promptBlock.IsVisible;
            promptToggleIcon.Kind = promptBlock.IsVisible ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;
        };

        var promptHeader = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(copyButton, Dock.Right);
        promptHeader.Children.Add(copyButton);
        promptHeader.Children.Add(promptToggle);

        var promptSection = new StackPanel { Children = { promptHeader, promptBlock } };
        DockPanel.SetDock(promptSection, Dock.Bottom);

        _detailStatus = new TextBlock
        {
            Name = "detailStatus",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = _Brush("CockpitAccentBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var detailHeader = new StackPanel
        {
            Children = { titleRow, _detailChips, toolbar },
        };
        DockPanel.SetDock(detailHeader, Dock.Top);
        DockPanel.SetDock(_detailStatus, Dock.Bottom);

        // Added in this order so DockPanel's fill (the last, un-docked child) lands between the header and the
        // prompt section: _detailStatus first reserves the very bottom edge, promptSection docks above it, and
        // descriptionScroll — added last — takes whatever is left.
        _detailContent = new DockPanel { IsVisible = false };
        _detailContent.Children.Add(detailHeader);
        _detailContent.Children.Add(_detailStatus);
        _detailContent.Children.Add(promptSection);
        _detailContent.Children.Add(descriptionScroll);

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

        // The loading bar sits over the top edge of the list rather than replacing it: a refresh keeps the previous
        // results readable, and a fetch that takes a second reads as an empty list without it.
        var listWithLoading = new Panel();
        listWithLoading.Children.Add(_grid);
        listWithLoading.Children.Add(_loading);

        // A GridSplitter between list and details (AC-297) so the operator can trade width between the two —
        // useful now that the dialog itself opens wider (1280×860).
        var splitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns,
            Background = _Brush("CockpitHairlineBrush"),
        };
        // 3:2 rather than 2:1 in the list's favour: the details column carries the work (a four-control toolbar, the
        // chips strip and a rendered description), the list only needs enough for Summary to read. At 2:1 the details
        // column lands near 410px on this dialog — no wider than it was before AC-297, and narrow enough that the
        // toolbar wraps again once the operator runs a scaled display. The splitter lets them trade it either way.
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,6,2*") };
        Grid.SetColumn(listWithLoading, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(detailPanel, 2);
        split.Children.Add(listWithLoading);
        split.Children.Add(splitter);
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
            _SetStatus("No YouTrack instances configured.");
            return;
        }

        // Fires _OnInstanceChangedAsync, which loads that instance's projects and issues.
        _instanceSelector.SelectedIndex = 0;
    }

    private void _SetStatus(string text) => _status.Text = text;

    private async Task _OnInstanceChangedAsync()
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return;
        }

        _SetStatus("Loading projects…");
        var projects = string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token)
            ? []
            : await _client.GetProjectsAsync(instance.InstanceUrl, instance.Token, CancellationToken.None);

        var options = new List<YouTrackProjectOption> { AllProjectOption };
        options.AddRange(projects
            .OrderBy(project => project.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(project => new YouTrackProjectOption(
                project.ShortName,
                string.IsNullOrWhiteSpace(project.Name) ? project.ShortName : $"{project.ShortName} - {project.Name}")));

        // AC-317: what the session's own project says it is tracked in wins over the instance-wide default. The
        // operator linked that project on purpose and to this session; the default is what to fall back on when
        // nothing did. Null when there is no session, no project, or no link — then nothing changes.
        var preferredTag = await _host.GetProjectFieldValueAsync(YouTrackProjectField.Key, cancellationToken: CancellationToken.None)
            ?? instance.DefaultProjectTag;

        _isSyncingProjectFilter = true;
        _projectFilter.ItemsSource = options;
        _projectFilter.SelectedItem = options.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(preferredTag)
            && string.Equals(option.Tag, preferredTag, StringComparison.OrdinalIgnoreCase))
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
            if (_settings.Instances.Count == 0)
            {
                _SetStatus("No YouTrack instances configured.");
            }
            else
            {
                _SetStatus("Select an instance.");
            }

            return;
        }

        _SetStatus("Loading…");
        _loading.IsVisible = true;

        try
        {
            if (string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token))
            {
                _SetStatus($"\"{instance.Label}\" is missing an instance URL or token.");
                _all = [];
                _ApplyFilter();
                return;
            }

            // A null Tag (the "All" option) omits the project: clause and queries every project on the instance.
            var projectTag = (_projectFilter.SelectedItem as YouTrackProjectOption)?.Tag;

            _all = await _client.GetOpenIssuesAsync(instance.InstanceUrl, instance.Token, projectTag, extraFilter: null, _assignedToMe.IsChecked == true, MaxResults, CancellationToken.None);
            _PopulateStateFilter();
            _ApplyFilter();
            _SetStatus($"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.");
        }
        catch (Exception exception)
        {
            _SetStatus($"Could not load issues: {exception.Message}");
        }
        finally
        {
            // In a finally: a bar still moving after a failure says the thing is still coming, which is the one
            // message it must never send.
            _loading.IsVisible = false;
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

        // Every rebuild goes through here — a keystroke in the filter, a state-filter change, and the reload an
        // action kicks off — and every one of them hands the grid a brand-new collection, which drops its
        // selection outright: it does not go looking through the new items for one that compares equal, and an
        // issue whose state a Set state call just changed would no longer be equal anyway. So the issue the
        // operator was reading is put back by identity, here, rather than at each call site (AC-292).
        var previousSelection = (_grid.SelectedItem as YouTrackIssue)?.IdReadable;
        var items = new ObservableCollection<YouTrackIssue>(filtered);
        _grid.ItemsSource = items;
        if (IssueSelection.Restore(items, previousSelection) is { } match)
        {
            _grid.SelectedItem = match;
        }
    }

    private void _ShowDetail(YouTrackIssue? issue)
    {
        if (issue is null)
        {
            // The result line is deliberately left alone: an empty selection is also what a grid rebuild passes
            // through on its way back to the same issue, and the panel holding that line is hidden here anyway.
            _detailContent.IsVisible = false;
            _detailPlaceholder.IsVisible = true;
            return;
        }

        if (!string.Equals(_detailStatusFor, issue.IdReadable, StringComparison.Ordinal))
        {
            _detailStatus.Text = string.Empty;
            _detailStatusFor = null;
        }

        // Everything this panel is about to show is built before any of it is shown, and a failure while building it
        // empties the panel rather than leaving the last issue's content in place. The order is the point: the
        // heading used to be swapped first, so anything that threw in between left this issue's id and title standing
        // over the previous issue's description — and over the prompt "Add to prompt" injects. Emptying matters for
        // the same reason: the grid has already moved to this issue, so a panel still holding the previous one would
        // offer that one's prompt for injection under this one's selection. The exception itself is not swallowed —
        // it is not this dialog's to interpret (AC-304).
        Control description;
        string prompt;
        List<Control> chips;
        try
        {
            var url = _BuildIssueUrl(issue);
            description = _DescriptionView(
                string.IsNullOrWhiteSpace(issue.Description) ? "(no description)" : issue.Description);
            prompt = PromptTemplate.Render(_settings.Template, issue, url);
            chips = [_BuildChip(issue.State ?? "(no state)")];
            if (!string.IsNullOrWhiteSpace(issue.Project))
            {
                chips.Add(_BuildChip(issue.Project));
            }
        }
        catch
        {
            _renderedPrompt = string.Empty;
            _promptPreview.Text = string.Empty;
            _detailContent.IsVisible = false;
            _detailPlaceholder.IsVisible = true;
            throw;
        }

        _detailPlaceholder.IsVisible = false;
        _detailContent.IsVisible = true;
        _detailId.Text = issue.IdReadable;
        _detailTitle.Text = issue.Summary;

        _detailChips.Children.Clear();
        _detailChips.Children.AddRange(chips);

        _detailBody.Content = description;
        _renderedPrompt = prompt;
        _promptPreview.Text = _renderedPrompt;

        _UpdateInjectAvailability();

        _ = _LoadFieldsAsync(issue);
    }

    // ICockpitHost.CreateMarkdownView arrived in host 0.7.0, and this plugin's manifest says so — but the host only
    // enforces minHostVersion from 1.0 onwards, so an older cockpit loads this plugin and then has no such member.
    // Falling back to what the contract's own default renders keeps that host usable rather than emptying the panel
    // on every selection (AC-304). MissingMemberException rather than MissingMethodException alone: the same absence
    // is what both report, and only that absence is caught — a parser fault or a null still travels.
    private Control _DescriptionView(string description)
    {
        try
        {
            return _RenderMarkdown(description);
        }
        catch (MissingMemberException)
        {
            return new SelectableTextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
        }
    }

    // The seam call sits in its own method, uninlined, so the missing member is resolved while this frame is on the
    // stack and the caller's handler is the one that catches it. Inlined into the try above, the runtime could raise
    // the resolution failure as that method is prepared — before its own handler is live — and the fallback would
    // never run on the host it exists for.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Control _RenderMarkdown(string description) => _host.CreateMarkdownView(description);

    // Add to prompt only makes sense with a live session; it stays put and just goes inert with a tooltip
    // explaining why, rather than disappearing and letting the fixed row jump (AC-297) — New session is the route
    // offered in its place. Re-read on every selection, and again the moment New session hands back a pane: that
    // pane is the very session the button was missing (AC-292).
    private void _UpdateInjectAvailability()
    {
        _inject.IsEnabled = _actions.HasActiveSession;
        ToolTip.SetTip(_inject, _actions.HasActiveSession
            ? "Inject this issue's prompt into the active session."
            : "No active session — start one, or use New session.");
    }

    // The result of an action, tied to the issue it was produced for — see _detailStatusFor.
    private void _SetDetailStatus(YouTrackIssue issue, string text)
    {
        _detailStatus.Text = text;
        _detailStatusFor = issue.IdReadable;
    }

    private Control _BuildChip(string text) => new Border
    {
        Background = _Brush("CockpitSecondaryBgBrush"),
        BorderBrush = _Brush("CockpitHairlineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 2),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 11 },
    };

    // Hand the selected issue to Autopilot's CEO planning round (AC-174): the CEO drafts a plan from the issue (its
    // title and description as the source), the operator approves it once, then it runs autonomously.
    private async Task _PlanInAutopilotAsync(YouTrackIssue issue)
    {
        var data = new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = issue.IdReadable,
            ["title"] = issue.Summary,
            ["description"] = issue.Description ?? string.Empty,
            ["project"] = issue.Project,
            ["url"] = _BuildIssueUrl(issue),
        };

        await _host.SendIntent("autopilot", "plan", data);
    }

    // What this issue's project allows, read per selection: until it is known, Set state stays disabled rather
    // than being offered and then refused.
    private async Task _LoadFieldsAsync(YouTrackIssue issue)
    {
        _fields = null;
        _setState.IsEnabled = false;

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
            var hasStart = fields.State is { } state && YouTrackWorkflow.FindStartTarget(state) is not null;
            var hasTargets = fields.State?.AvailableTargets.Count > 0;
            _setState.IsEnabled = hasStart || hasTargets;
        }
        catch (Exception exception)
        {
            // Only onto an empty line. This runs by itself on every selection and answers a question the operator
            // did not ask, so it must not land on top of the result of something they did ask for — Add to prompt
            // reporting what it injected, say, which this used to replace a moment later.
            if (token == _fieldsToken && _detailStatusFor is null)
            {
                _SetDetailStatus(issue, $"Could not read this issue's states: {exception.Message}");
            }
        }
    }

    // Start = the three steps a ticket is picked up with: move it to in progress, put the token owner's name on
    // it, and tie it to the session the work will happen in — a state mutation, not a session action, which is
    // why it lives as "Start work" inside the Set state menu (AC-297) rather than as its own button.
    private async Task _StartAsync(YouTrackIssue issue)
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance
            || _fields is not { State: { } state } fields
            || YouTrackWorkflow.FindStartTarget(state) is not { } target)
        {
            return;
        }

        _setState.IsEnabled = false;
        try
        {
            var previous = state.CurrentValue ?? string.Empty;
            var startResult = await _workflow.StartAsync(instance, issue, fields, target, CancellationToken.None);
            _stateChanges.Moved(instance, issue, previous, target, _host.Sessions.ActiveSessionWorkingDirectory);

            // _LinkToActiveSession reports its own outcome; without combining the two, its message silently
            // replaced Start's own result and the operator never saw it (AC-299 bug 1).
            var linkResult = _LinkToActiveSession(issue);
            _SetDetailStatus(issue, string.IsNullOrEmpty(linkResult) ? startResult : $"{startResult} {linkResult}");
            await _LoadIssuesAsync();
        }
        catch (Exception exception)
        {
            _SetDetailStatus(issue, $"Could not start {issue.IdReadable}: {exception.Message}");
            _setState.IsEnabled = true;
        }
    }

    private void _ShowStateMenu()
    {
        if (_grid.SelectedItem is not YouTrackIssue issue || _fields is not { } fields)
        {
            return;
        }

        var items = new List<Control>();

        // Start work sits above the board's own targets, set apart by a separator: it is not a value the project
        // defines but the fixed first move on a ticket (AC-297) — the reason "Start" was free to become New
        // session's name.
        if (fields.State is { } state && YouTrackWorkflow.FindStartTarget(state) is not null)
        {
            var startItem = new MenuItem { Header = "Start work" };
            startItem.Click += async (_, _) => await _StartAsync(issue);
            items.Add(startItem);
            items.Add(new Separator());
        }

        if (fields.State is { } stateField)
        {
            foreach (var target in stateField.AvailableTargets)
            {
                var item = new MenuItem { Header = target };
                item.Click += async (_, _) => await _SetStateAsync(issue, target);
                items.Add(item);
            }
        }

        // No empty-menu check: _setState is only ever armed when this issue has a start target or a board target,
        // which is exactly when one of the two blocks above puts something in the list (see _LoadFieldsAsync).
        var menu = new ContextMenu { PlacementTarget = _setState, ItemsSource = items };
        menu.Open(_setState);
    }

    private async Task _SetStateAsync(YouTrackIssue issue, string target)
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance || _fields?.State is not { } state)
        {
            return;
        }

        _setState.IsEnabled = false;
        try
        {
            await _client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, state, target, CancellationToken.None);
            _stateChanges.Moved(instance, issue, state.CurrentValue ?? string.Empty, target, _host.Sessions.ActiveSessionWorkingDirectory);
            _SetDetailStatus(issue, $"{issue.IdReadable} → {target}.");
            await _LoadIssuesAsync();
        }
        catch (Exception exception)
        {
            _SetDetailStatus(issue, $"Could not move {issue.IdReadable}: {exception.Message}");
            _setState.IsEnabled = true;
        }
    }

    // New session (AC-298): hands the same rendered prompt Add to prompt and the preview already use to the
    // cockpit's own New-session dialog, prefilled with this issue's id as the session name. The operator still
    // sees and confirms every field there — nothing starts and the ticket's own state is not touched until they
    // press Start; on cancel, nothing is linked either.
    private async Task _StartNewSessionAsync()
    {
        if (_grid.SelectedItem is not YouTrackIssue issue || _instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return;
        }

        var prefill = new NewSessionPrefill(
            InitialPrompt: PromptTemplate.Render(_settings.Template, issue, _BuildIssueUrl(issue)),
            SessionName: issue.IdReadable);

        // The New-session dialog is modal to the main window, not to this one, so nothing but this button stops a
        // second press from opening a second dialog — with its own onStarted, and its own session. It stays inert
        // until the dialog the operator already has in front of them is gone (AC-292).
        _newSession.IsEnabled = false;
        try
        {
            await _host.ShowNewSessionDialogAsync(
                prefill,
                onStarted: paneId =>
                {
                    _LinkIssue(paneId, instance, issue);

                    // That pane is a live session, which is what Add to prompt was waiting for.
                    _UpdateInjectAvailability();
                    _SetDetailStatus(issue, $"Started a new session for {issue.IdReadable}, linked to it.");
                },
                onCancelled: () => _SetDetailStatus(issue, "New session cancelled."));
        }
        catch (Exception exception)
        {
            _SetDetailStatus(issue, $"Could not open the New-session dialog: {exception.Message}");
        }
        finally
        {
            _newSession.IsEnabled = true;
        }
    }

    // The one place that actually calls SessionIssueLinks.Link — shared by "Link to session" (the active pane)
    // and New session's onStarted callback (the pane it just created), so the two do not each keep their own copy.
    // The working directory travels with the link: a flow that cuts a branch or a worktree when a ticket is picked
    // is given the path to do it in, instead of an empty string (AC-292).
    private void _LinkIssue(string paneId, YouTrackInstance instance, YouTrackIssue issue) =>
        _links.Link(paneId, new LinkedIssue(instance, issue), _host.Sessions.ActiveSessionWorkingDirectory);

    // Ties the issue to the session pane that is selected right now, which is the one the header item showing it
    // sits in — the dialog itself belongs to no session. Returns the resulting message rather than setting
    // _detailStatus directly, so a caller that already has something to report (Start work's own result) can
    // combine both into one line instead of one overwriting the other (AC-299 bug 1).
    private string _LinkToActiveSession(YouTrackIssue issue)
    {
        if (_instanceSelector.SelectedItem is not YouTrackInstance instance)
        {
            return string.Empty;
        }

        if (_host.Sessions.ActivePaneId is not { Length: > 0 } paneId)
        {
            return "No active session to link this issue to.";
        }

        _LinkIssue(paneId, instance, issue);
        return $"{issue.IdReadable} linked to the active session.";
    }

    // Conditional entries live only here, never as toolbar buttons that would appear and disappear (AC-297): Plan
    // in Autopilot only when the plugin is installed and listening for the intent, Link to session only with an
    // active pane to link to. Open in browser and Copy prompt need no such gate — the calls behind them already
    // guard a missing issue or an empty render.
    private void _ShowOverflowMenu()
    {
        if (_grid.SelectedItem is not YouTrackIssue issue)
        {
            return;
        }

        var items = new List<Control>();

        if (_host.CanSendIntent("autopilot", "plan"))
        {
            var planItem = new MenuItem { Header = "Plan in Autopilot" };
            planItem.Click += async (_, _) => await _PlanInAutopilotAsync(issue);
            items.Add(planItem);
        }

        if (_host.Sessions.ActivePaneId is { Length: > 0 })
        {
            var linkItem = new MenuItem { Header = "Link to session" };
            linkItem.Click += (_, _) => _SetDetailStatus(issue, _LinkToActiveSession(issue));
            items.Add(linkItem);
        }

        var openItem = new MenuItem { Header = "Open in browser" };
        openItem.Click += (_, _) => _OpenInBrowser();
        items.Add(openItem);

        var copyItem = new MenuItem { Header = "Copy prompt" };
        copyItem.Click += async (_, _) => await _CopyPromptAsync();
        items.Add(copyItem);

        var menu = new ContextMenu { PlacementTarget = _overflow, ItemsSource = items };
        menu.Open(_overflow);
    }

    private void _AddToPrompt(YouTrackIssue? issue)
    {
        if (issue is null)
        {
            _SetStatus("Select an issue first.");
            return;
        }

        if (!_actions.HasActiveSession)
        {
            _SetDetailStatus(issue, "No active session — use Copy to put the prompt on the clipboard.");
            return;
        }

        _ = _actions.InjectIntoActiveSessionAsync(PromptTemplate.Render(_settings.Template, issue, _BuildIssueUrl(issue)));
        _SetDetailStatus(issue, $"Added issue {issue.IdReadable} to the active session's prompt.");
    }

    private async Task _CopyPromptAsync()
    {
        if (_grid.SelectedItem is not YouTrackIssue issue || string.IsNullOrEmpty(_renderedPrompt))
        {
            return;
        }

        await _actions.SetClipboardTextAsync(_renderedPrompt);
        _SetDetailStatus(issue, "Prompt copied to the clipboard.");
    }

    private void _OpenInBrowser()
    {
        if (_grid.SelectedItem is not YouTrackIssue issue)
        {
            return;
        }

        if (YouTrackBrowser.Open(_BuildIssueUrl(issue)) is { } failure)
        {
            _SetDetailStatus(issue, failure);
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
