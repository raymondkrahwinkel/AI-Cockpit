using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.YouTrack;

// The "YouTrack Issues" dialog opened from the side-menu button (#48): an instance selector (which of the
// configured `YouTrackInstance`s to query), a project filter (plus "All", populated from the
// instance's admin API with a silent fallback to the projects already present in the fetched issues), a state
// filter (plus "All", populated the same way — from the selected project's own status field via the admin API,
// so a stage that never made it into the first `MaxResults` issues is still offered, AC-518 — with a
// silent fallback to the already-fetched issues' State/Stage value when there is no single project to ask, or
// the call fails) and a search box, driving a sortable `DataGrid` of open issues on the left plus a
// details panel on the right — summary, chips, a rendered description, a fixed action toolbar and a collapsible
// preview of the prompt it would produce (with a copy button). Switching instance or project re-fetches (a
// different instance is a different server; a specific project narrows the server-side query); switching state
// re-fetches too when the field behind it is known (server-side, so a stage is never limited to whatever page
// already loaded), otherwise re-filters client-side the same as before AC-518; typing a search term always only
// re-filters the already-fetched list, client-side. Every fetch is itself capped at `MaxResults`; a
// result landing at exactly that count says so in the status line rather than silently reading as the whole
// list (AC-518 follow-up, mirrors GitHub Issues' own notice, AC-519). "Add to prompt" injects into the active
// session; "New session" (AC-298) hands the same prompt to the cockpit's own New-session dialog instead. Built
// in code; the DataGrid theme is provided app-wide by the host.
internal sealed class YouTrackDialogControl : UserControl
{
    private const string AllOption = "All";

    // internal, not private: AC-518 follow-up's boundary test asserts against this constant itself, not a round
    // number above it — the notice in _ReportLoaded only makes sense read against the exact value this fetches.
    internal const int MaxResults = 100;

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

    // The status field the current project resolved to (_ResolveStateFieldAsync), e.g. "State" or "Stage" — null
    // when there is no single project to ask ("All projects", #48) or the admin lookup failed. Known, it lets
    // _LoadIssuesAsync scope a chosen state server-side instead of only ever seeing whatever page already loaded
    // (AC-518); unknown, the state filter falls back to the pre-AC-518 behaviour, client-side over the loaded rows.
    private string? _stateFieldName;

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

    // Guards _SetStateOptions's own ItemsSource/SelectedItem assignment the same way _isSyncingProjectFilter
    // guards the project filter's: rebuilding the dropdown (from _ResolveStateFieldAsync, or the rows-fallback
    // in _LoadIssuesAsync) is not the operator choosing a state, but both assignments are real ComboBox
    // mutations and each fires SelectionChanged on its own. Without this, _OnStateFilterChangedAsync read
    // _stateFieldName as already resolved and took its reload branch for both — a redundant, unfiltered fetch
    // racing the explicit load that follows in the same caller, with no reentrancy guard or generation token to
    // say which one is stale (AC-518 adversarial review). The explicit _LoadIssuesAsync call every caller of
    // _ResolveStateFieldAsync/_SetStateOptions already makes is the only fetch this dropdown rebuild should ever
    // cause.
    private bool _isPopulatingStateOptions;

    // Guards _WidenSearchIfTruncatedAsync against overlapping requests: Enter and LostFocus can both fire for the
    // same "operator is done typing" moment (pressing Enter usually also moves focus away), and without this a
    // second call could race the first one's grid/status update.
    private bool _isWideningSearch;

    // Stamped on every _LoadIssuesAsync call and checked again once its fetch returns (same idiom as
    // _fieldsToken/_LoadFieldsAsync): belt-and-braces alongside _isPopulatingStateOptions above — that guard
    // closes the one known source of a redundant fetch, this one makes sure that ANY overlapping fetch, from
    // whatever caller, can no longer have its response applied once a later call has superseded it. Without it,
    // whichever of two in-flight fetches merely happened to respond last would win, even if it started first and
    // now answers a filter the operator has since changed away from.
    private int _loadToken;

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
        _stateFilter.SelectionChanged += async (_, _) => await _OnStateFilterChangedAsync();

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

        // Widening past the loaded page (AC-518 follow-up) fires on Enter or on losing focus — never on
        // TextChanged. Gating it behind "the operator is done for now" rather than every keystroke is what keeps
        // this from becoming an HTTP call per letter while they are still typing (the gh-CLI-style call-storm this
        // deliberately avoids); _ApplyFilter above still re-filters client-side on every keystroke as it always did.
        _search.KeyDown += async (_, keyEventArgs) =>
        {
            if (keyEventArgs.Key == Key.Enter)
            {
                await _WidenSearchIfTruncatedAsync();
            }
        };
        _search.LostFocus += async (_, _) => await _WidenSearchIfTruncatedAsync();

        _status = new TextBlock { Name = "status", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

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
            CornerRadius = _Radius("CockpitControlRadius", 9),
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
            CornerRadius = _Radius("CockpitControlRadius", 9),
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

        // AC-317/AC-548/AC-884: routed through YouTrackProjectField.ResolvePreferredTagsAsync, the one resolution
        // this dialog and the session picker both call. This filter is a single ComboBox selection, so a project
        // linked to more than one prefix preselects "All" rather than silently picking the first.
        var preferredTags = await YouTrackProjectField.ResolvePreferredTagsAsync(
            _host, paneId: null, instance.DefaultProjectTag, CancellationToken.None);

        _isSyncingProjectFilter = true;
        _projectFilter.ItemsSource = options;
        _projectFilter.SelectedItem = preferredTags is [var onlyTag]
            ? options.FirstOrDefault(option => string.Equals(option.Tag, onlyTag, StringComparison.OrdinalIgnoreCase)) ?? AllProjectOption
            : AllProjectOption;
        _isSyncingProjectFilter = false;

        await _ResolveStateFieldAsync((_projectFilter.SelectedItem as YouTrackProjectOption)?.Tag);
        await _LoadIssuesAsync();
    }

    private async Task _OnProjectChangedAsync()
    {
        if (_isSyncingProjectFilter)
        {
            return;
        }

        await _ResolveStateFieldAsync((_projectFilter.SelectedItem as YouTrackProjectOption)?.Tag);
        await _LoadIssuesAsync();
    }

    private async Task _OnStateFilterChangedAsync()
    {
        if (_isPopulatingStateOptions)
        {
            return;
        }

        if (_stateFieldName is not null)
        {
            // A known status field lets the next fetch scope to exactly this value server-side (AC-518), rather
            // than only ever filtering whatever page of up-to-MaxResults issues already loaded.
            await _LoadIssuesAsync();
            return;
        }

        _ApplyFilter();
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

        // Stamped before the fetch and checked again once it returns (_fieldsToken's own idiom, below): a second
        // overlapping call to this method — from any caller, for any reason — bumps this past what the first
        // call captured, so a slower, older fetch can no longer apply its result once a newer one has started
        // (AC-518 adversarial review). _isPopulatingStateOptions above closes the one known source of a redundant
        // call; this is the backstop for any other one.
        var token = ++_loadToken;

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
            var selectedState = _stateFilter.SelectedItem as string;

            // _stateFieldName known (_ResolveStateFieldAsync) means a chosen state can be scoped server-side —
            // "all issues in this state", not just whichever of them fit in the first MaxResults (AC-518). Unknown
            // (a failed lookup, or "All projects", #48), the state filter stays client-side over whatever page
            // comes back, exactly as before this fix — see _ApplyFilter and the fallback populate below.
            var extraFilter = _stateFieldName is { } fieldName && !string.IsNullOrEmpty(selectedState) && selectedState != AllOption
                ? $"#Unresolved {_QuotedFieldName(fieldName)}: {{{selectedState}}}"
                : null;

            var fetched = await _client.GetOpenIssuesAsync(instance.InstanceUrl, instance.Token, projectTag is { } tag ? [tag] : null, extraFilter, _assignedToMe.IsChecked == true, MaxResults, CancellationToken.None);
            if (token != _loadToken)
            {
                // Superseded while the fetch was in flight — applying this now would let a stale, older request
                // overwrite whatever the newer, current one already showed or is still about to (AC-518).
                return;
            }

            _all = fetched;
            if (_stateFieldName is null)
            {
                _SetStateOptions(_DistinctRowStates());
            }

            _ApplyFilter();
            _ReportLoaded();
        }
        catch (Exception exception)
        {
            if (token == _loadToken)
            {
                _SetStatus($"Could not load issues: {exception.Message}");
            }
        }
        finally
        {
            if (token == _loadToken)
            {
                // In a finally: a bar still moving after a failure says the thing is still coming, which is the
                // one message it must never send. Guarded the same as above — a superseded call's finally must
                // not hide the loading bar out from under a newer call that is still genuinely in flight.
                _loading.IsVisible = false;
            }
        }
    }

    // Whether the freshly loaded page might not be the whole truth — landing at exactly MaxResults could mean
    // there is more behind it, or (rarely) that this project/state/search scope has precisely that many open
    // issues; there is no cheap way to tell the two apart without a second, narrower request. One detection
    // feeding two things (AC-518 follow-up): the truncation notice below, and _WidenSearchIfTruncatedAsync's own
    // decision to look past the loaded page — not two independently maintained copies of the same "== MaxResults".
    private bool _LoadedListMightBeTruncated => _all.Count == MaxResults;

    // What a successful load reports — pulled out of _LoadIssuesAsync so the boundary worth proving precisely (a
    // result landing at exactly MaxResults) is reachable without a live fetch (AC-518 follow-up). Mirrors
    // GitHubIssuesDialogControl._ReportLoaded (AC-519) — same wording, same reasoning, on this dialog's own cap.
    private void _ReportLoaded()
    {
        var baseline = $"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.";

        // Over-warning on the rare exact match (a project with precisely MaxResults open issues) is the safer of
        // the two wrong answers; the state filter (AC-518) is offered as the reliable way to see past it.
        _SetStatus(_LoadedListMightBeTruncated
            ? $"{baseline} The list may be incomplete at exactly {MaxResults} — filter by state for the reliable set."
            : baseline);
    }

    // Looks past the loaded page when it might be truncated (AC-518 follow-up, Raymond: "als er meer dan 100 zijn,
    // moet het alsnog doorzoekbaar en vindbaar zijn"). A non-truncated load is already the whole truth — whatever
    // _ApplyFilter found client-side, hits or a real zero, is complete and correct, so a server call would add
    // nothing. A TRUNCATED load's client-side hits are not proof of completeness either, though — there may be
    // more matches beyond the loaded MaxResults that _ApplyFilter simply never saw — so this widens whenever the
    // load might be truncated, regardless of how many local hits already show. Checking local-hit-count instead
    // of load-truncation was this fix's own first, wrong draft: a term with 5 local hits inside a truncated page
    // looks like a complete answer and is not one.
    private async Task _WidenSearchIfTruncatedAsync()
    {
        if (_isWideningSearch)
        {
            return;
        }

        var query = _search.Text?.Trim();
        if (string.IsNullOrEmpty(query) || !_LoadedListMightBeTruncated)
        {
            return;
        }

        if (_instanceSelector.SelectedItem is not YouTrackInstance instance
            || string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token))
        {
            return;
        }

        var localHitCount = (_grid.ItemsSource as ObservableCollection<YouTrackIssue>)?.Count ?? 0;
        _isWideningSearch = true;
        _SetStatus("Searching the server past the loaded page…");
        _loading.IsVisible = true;
        try
        {
            var projectTag = (_projectFilter.SelectedItem as YouTrackProjectOption)?.Tag;
            var selectedState = _stateFilter.SelectedItem as string;
            var searchTerm = BuildSearchTerm(_stateFieldName, selectedState, query);

            var results = await _client.GetOpenIssuesAsync(instance.InstanceUrl, instance.Token, projectTag is { } tag ? [tag] : null, searchTerm, _assignedToMe.IsChecked == true, MaxResults, CancellationToken.None);

            if (results.Count > 0)
            {
                _grid.ItemsSource = new ObservableCollection<YouTrackIssue>(results);
                _SetStatus($"{results.Count} issue(s) found on the server beyond the loaded page, matching \"{query}\".");
                return;
            }

            // The server found nothing more: if the operator was already looking at local hits, leave the grid as
            // it is — a wider search that adds nothing is not grounds to take away what was already found. An
            // empty grid stays empty either way.
            _SetStatus(localHitCount > 0
                ? $"No further matches on the server for \"{query}\" beyond the {localHitCount} already shown."
                : $"No open issues found on the server matching \"{query}\".");
        }
        catch (Exception exception)
        {
            // Leaves the grid exactly as _ApplyFilter last left it — a failed widen must not make the view emptier
            // or more confusing than the plain client-side filter already was (never worse than today).
            _SetStatus($"Could not search the server: {exception.Message}");
        }
        finally
        {
            _isWideningSearch = false;
            _loading.IsVisible = false;
        }
    }

    // The server-side widen-search term (AC-518 follow-up): `#Unresolved` stays — this dialog shows open
    // work, a deliberate choice kept from #48/AC-518, not something a free-text search should see past — plus the
    // active state (when the field is known, so a search cannot surface issues from a stage the state filter
    // itself excludes — the same two-truths mistake AC-518's own state filter guarded against) plus the free
    // text as a double-quoted phrase, YouTrack's own literal-phrase syntax: a term containing a colon, brace, or
    // other query character then reads as text instead of being parsed as one. An embedded backslash or double
    // quote is escaped the same way a quoted string commonly is; unlike the rest of this query shape, that
    // specific escaping is not verified against a live YouTrack (flagged, same as the isResolved uncertainty
    // earlier in this ticket).
    internal static string BuildSearchTerm(string? stateFieldName, string? selectedState, string query)
    {
        var stateTerm = stateFieldName is { } fieldName && !string.IsNullOrEmpty(selectedState) && selectedState != AllOption
            ? $" {_QuotedFieldName(fieldName)}: {{{selectedState}}}"
            : string.Empty;

        var escapedQuery = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"#Unresolved{stateTerm} \"{escapedQuery}\"";
    }

    // A YouTrack query attribute name only needs curly braces when it contains a space — a bare "State: {Ready}"
    // already parses fine (StateFieldNames' common case, and every query this dialog has sent until now), but a
    // two-word field like EJ's "Kanban State" would otherwise read as two tokens ("Kanban" as its own bare word,
    // then "State: {Ready}" attached to the second) rather than the one field:value pair intended. The value
    // (selectedState, above) is always braced regardless — that half of this query shape was never the bug this
    // guards; only the field name was ever left unquoted.
    private static string _QuotedFieldName(string fieldName) =>
        fieldName.Contains(' ', StringComparison.Ordinal) ? $"{{{fieldName}}}" : fieldName;

    // Asks the selected project's own status field for every value it allows (YouTrackClient.GetProjectStateFieldAsync,
    // AC-518) — the same admin-API pattern _OnInstanceChangedAsync already uses for the project filter, and called at
    // the same cadence: once per project/instance change, not on every reload, since a project's field configuration
    // does not vary between one issue-fetch and the next.
    private async Task _ResolveStateFieldAsync(string? projectTag)
    {
        _stateFieldName = null;

        if (string.IsNullOrWhiteSpace(projectTag) || _instanceSelector.SelectedItem is not YouTrackInstance instance
            || string.IsNullOrWhiteSpace(instance.InstanceUrl) || string.IsNullOrWhiteSpace(instance.Token))
        {
            // "All projects" (#48): no single project to ask, and different projects on one instance need not even
            // share the same status field — merging them would take one admin call per project for a dropdown that
            // mixes fields with different meanings. _LoadIssuesAsync's own fallback (distinct-of-rows) covers this,
            // same as before this ticket (AC-518 step 6).
            return;
        }

        var (fieldName, values) = await _client.GetProjectStateFieldAsync(instance.InstanceUrl, instance.Token, projectTag, CancellationToken.None);
        if (values.Count == 0)
        {
            // A failed call, or a project whose status field YouTrackFieldParser does not recognize — leaves
            // _stateFieldName null, so _LoadIssuesAsync's rows-fallback takes over once issues are back, and the
            // dropdown is never emptier than it was before this fix (AC-518 step 2/3).
            return;
        }

        _stateFieldName = fieldName;
        _SetStateOptions(values);
    }

    // The values the freshly loaded rows carry — the pre-AC-518 dropdown source, kept as the fallback for a project
    // whose status field could not be resolved, or "All projects" (#48).
    private List<string> _DistinctRowStates() => _all
        .Select(issue => issue.State)
        .Where(state => !string.IsNullOrEmpty(state))
        .Select(state => state!)
        .ToList();

    // Rebuilds the state dropdown from a set of values, keeping the previous selection if it is still present
    // (otherwise falls back to "All").
    private void _SetStateOptions(IEnumerable<string> states)
    {
        var previousSelection = _stateFilter.SelectedItem as string;
        var options = new List<string> { AllOption };
        options.AddRange(states
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(state => state, StringComparer.OrdinalIgnoreCase));

        // Both assignments below are real ComboBox mutations and each fires SelectionChanged on its own —
        // rebuilding the dropdown is not the operator choosing a state, so _isPopulatingStateOptions tells
        // _OnStateFilterChangedAsync to ignore both (AC-518 adversarial review).
        _isPopulatingStateOptions = true;
        try
        {
            _stateFilter.ItemsSource = options;
            _stateFilter.SelectedItem = previousSelection is not null && options.Contains(previousSelection)
                ? previousSelection
                : AllOption;
        }
        finally
        {
            _isPopulatingStateOptions = false;
        }
    }

    private void _ApplyFilter()
    {
        var query = _search.Text?.Trim();
        IEnumerable<YouTrackIssue> filtered = _all;

        // Only filters here when _stateFieldName is unknown: once _LoadIssuesAsync has already scoped the fetch to
        // exactly this field and value server-side, filtering again client-side risks disagreeing with it (a value
        // that does not string-compare identically) rather than confirming it — the double-truth AC-518 step 4 warns
        // about. Unknown, this is the only filtering the chosen state ever gets, same as before this fix.
        if (_stateFieldName is null)
        {
            var selectedState = _stateFilter.SelectedItem as string;
            if (!string.IsNullOrEmpty(selectedState) && selectedState != AllOption)
            {
                filtered = filtered.Where(issue => string.Equals(issue.State, selectedState, StringComparison.OrdinalIgnoreCase));
            }
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

    // Border.tag — the theme's shape for a label that classifies the thing beside it, rather than one more
    // hand-written copy of that same shape.
    private Control _BuildChip(string text) => new Border
    {
        Classes = { "tag" },
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text },
    };

    // Hand the selected issue to Autopilot's CEO planning round (AC-174): the CEO drafts a plan from the issue (its
    // title and description as the source), the operator approves it once, then it runs autonomously.
    private async Task _PlanInAutopilotAsync(YouTrackIssue issue) =>
        await _host.SendIntent("autopilot", "plan", PlanIntentPayload(issue, _BuildIssueUrl(issue)));

    // What "Plan in Autopilot" hands over. Kept a pure builder off the control so the payload — in particular the
    // stage Autopilot's start gate keys on (AC-345) — is asserted without a live dialog.
    internal static Dictionary<string, string> PlanIntentPayload(YouTrackIssue issue, string url) => new()
    {
        ["tracker"] = "youtrack",
        ["issue"] = issue.IdReadable,
        ["title"] = issue.Summary,
        ["description"] = issue.Description ?? string.Empty,
        ["project"] = issue.Project,
        ["url"] = url,
        // What stage a person put it on, so the start gate can key on that rather than on what the description claims
        // about itself. Sent raw — judging it is the gate's job, not the tracker's.
        ["stage"] = issue.State ?? string.Empty,
    };

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
            SessionName: issue.IdReadable)
        {
            // AC-419: the dialog knows the issue, so it can know the project — the cockpit project the operator linked
            // to this YouTrack project (AC-317) is preselected instead of them picking it by hand every time. The
            // issue's own project rather than the grid's filter: the filter can be on "All", and it is the issue being
            // started that decides.
            //
            // The link stores only the short name, so two configured instances that each host a project AC are
            // indistinguishable here. Accepted rather than worked around: YouTrackProjectField.BuildOptionsAsync
            // already collapses same-named projects across instances into one option, so the stored value is
            // instance-less by design and there is nothing to match an instance against. The cost of being wrong is
            // small and visible — a preselected project the operator can see and change before Start.
            // An "All projects" query on a response without project.shortName leaves the issue with no project at all
            // (YouTrackClient._ExtractProject), and a link with nothing to match on is not one worth sending.
            LinkedProject = string.IsNullOrWhiteSpace(issue.Project)
                ? null
                : new ProjectLink(YouTrackProjectField.Key, issue.Project),
        };

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

    // The host's geometry token, so a plugin's box rounds like the app's other boxes.
    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

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
