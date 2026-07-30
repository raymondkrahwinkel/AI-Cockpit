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

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The "GitHub Issues" dialog opened from the left-menu button: a repository filter, a label filter, a search box,
/// and a sortable <see cref="DataGrid"/> of open issues (across all repos in GitHub CLI mode, or one repo in HTTP
/// mode) on the left, and a details panel on the right — number, title, a repository chip, a rendered
/// description, a fixed action toolbar and a collapsible preview of the prompt it would produce (with a copy
/// button). The repository filter is populated from the distinct <see cref="GitHubIssue.Repository"/> values in
/// the loaded issues plus an "All" entry; it filters the grid client-side. The label filter is populated from the
/// labels of the repositories involved (AC-519) — not from the loaded issues, which would repeat the gap the
/// YouTrack status filter had — and narrows the fetch itself, since filtering client-side over a page GitHub may
/// have capped would silently miss whatever was cut off. "Add to prompt" injects into the active session; "New
/// session" (mirroring the YouTrack dialog) hands the same prompt to the cockpit's own New-session dialog instead.
/// Built in code; the DataGrid theme is provided app-wide by the host.
/// </summary>
internal sealed class GitHubIssuesDialogControl : UserControl
{
    private const string AllRepositoriesOption = "All";
    private const string AllLabelsOption = "All labels";

    private readonly GitHubIssuesSettings _settings;
    private readonly ICockpitHost _host;
    private readonly ICockpitActions _actions;
    private readonly SessionIssueLinks _links;
    private readonly GitHubIssuesClient _http = new();
    private readonly GitHubGhClient _gh = new();

    private readonly ComboBox _repoFilter;
    private readonly ComboBox _labelFilter;
    private readonly CheckBox _assignedToMe;
    private readonly TextBox _search;

    /// <summary>The repository the session's project is linked to (AC-317). Null until asked for, empty string once asked and there was none — so it is asked exactly once.</summary>
    private string? _linkedRepository;

    // Whether the repo dropdown has been populated at least once — see _PopulateRepoFilter for why this, and not a
    // null check on the ComboBox's own SelectedItem, is what tells the first population from a later one. Found
    // during AC-519 review: _repoFilter is constructed with SelectedIndex = 0, and a ComboBox resolves that to a
    // real, non-null SelectedItem synchronously, before _LoadAsync ever runs — so "SelectedItem as string ?? ..."
    // always took the left side and _linkedRepository (AC-317) was never actually consulted. Confirmed with a
    // regression test before this field was added (RepoFilter_PreselectsTheLinkedProjectsRepository_OnFirstPopulation).
    private bool _repoOptionsPopulated;

    /// <summary>
    /// The label the settings' <see cref="GitHubIssuesSettings.InProgressLabel"/> names (AC-519), resolved once —
    /// the label filter's one chance to open on it, the same way <see cref="_linkedRepository"/> gets the repository
    /// filter's. After the first population the operator's own choice persists. Empty once resolved and there was
    /// none, so it is asked exactly once.
    /// </summary>
    private string? _preferredLabel;

    // Whether the label dropdown has been populated at least once — see _PopulateLabelFilter for why this, and not
    // a null check on the ComboBox's own SelectedItem, is what tells the first population from a later one.
    private bool _labelOptionsPopulated;

    // Set while a fetch itself repopulates the label dropdown's ItemsSource/SelectedItem, so that assignment does
    // not read back as the operator choosing a label and triggering a second fetch.
    private bool _suppressLabelFilterReload;

    // The window-level status line, along the bottom edge of the dialog: fetch/load/refresh state and the guard
    // messages ("no repository set") that fire before any issue is even selected. Always present; what an action
    // on a selected issue did is reported by _detailStatus, inside the panel that issue is shown in, so the two
    // sit in different places and never carry the same message.
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
    private readonly Button _overflow;
    private readonly ContentControl _detailBody;
    private readonly SelectableTextBlock _promptPreview;

    // The detail panel's own status line: the outcome of an action taken on the selected issue (Link to session,
    // Copy, Add to prompt, New session). Only ever visible while the panel itself is, so it never repeats what
    // _status already said.
    private readonly TextBlock _detailStatus;

    private IReadOnlyList<GitHubIssue> _all = [];
    private string _renderedPrompt = string.Empty;

    // Which issue the line in _detailStatus is about. A result belongs to the issue it was produced for, not to
    // the grid event that happened to be in flight: a reload raises SelectionChanged twice — once on the empty
    // grid, once back on the same issue — and clearing on every selection change wiped the message before the
    // operator could read it (AC-292, the same defect the YouTrack dialog had).
    private string? _detailStatusFor;

    public GitHubIssuesDialogControl(GitHubIssuesSettings settings, ICockpitHost host, SessionIssueLinks links)
    {
        _settings = settings;
        _host = host;
        _actions = host.Actions;
        _links = links;

        _repoFilter = new ComboBox
        {
            Name = "repoFilter",
            ItemsSource = new List<string> { AllRepositoriesOption },
            SelectedIndex = 0,
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _repoFilter.SelectionChanged += (_, _) => _ApplyFilter();

        // Unlike the repository filter, a label narrows the fetch server-side (gh's "label:x" search term, or the
        // REST labels= param) — the whole point being that filtering must reach past whatever GitHub capped the
        // first page at, which client-side filtering over that page could never do (AC-519).
        _labelFilter = new ComboBox
        {
            Name = "labelFilter",
            ItemsSource = new List<string> { AllLabelsOption },
            SelectedIndex = 0,
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolTip.SetTip(_labelFilter, "Filter by label — from the repositories themselves, not just the loaded issues, and applied on GitHub's side so it is not limited to the first page.");
        _labelFilter.SelectionChanged += async (_, _) =>
        {
            if (_suppressLabelFilterReload)
            {
                return;
            }

            await _LoadAsync(forceRefresh: true);
        };

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

        _status = new TextBlock { Name = "status", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

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
        DockPanel.SetDock(_labelFilter, Dock.Left);
        DockPanel.SetDock(_assignedToMe, Dock.Left);
        topBar.Children.Add(refresh);
        topBar.Children.Add(_repoFilter);
        topBar.Children.Add(_labelFilter);
        topBar.Children.Add(_assignedToMe);
        topBar.Children.Add(_search);

        // Details panel (right). Number + title, with the issue's url moved off the text and onto a single icon
        // button — it used to appear spelled out twice (the meta line and "Open in browser"'s own label); now
        // only the rendered prompt still carries it, because that copy is the literal text a session receives.
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

        // A single chip, replacing the old "{repo} · #{number} · {url}" meta line: Repository is the only field
        // a GitHubIssue carries beyond number/title/body/url — there is no status chip to add, since a GitHub
        // issue has no status field and this dialog only ever lists open ones.
        _detailChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };

        // Without this the button's own explanation of why it is inert never appears: Avalonia shows no tooltip
        // on a disabled control unless asked to.
        ToolTip.SetShowOnDisabled(_inject, true);
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubIssue);

        _newSession = new Button { Content = "New session" };
        _newSession.Click += async (_, _) => await _StartNewSessionAsync();

        _overflow = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.DotsHorizontal, Width = 16, Height = 16 },
            Padding = new Thickness(8, 4),
        };
        ToolTip.SetTip(_overflow, "More actions");
        _overflow.Click += (_, _) => _ShowOverflowMenu();

        // A fixed row: the same controls, in the same order, whether or not Autopilot is installed or a session
        // is active — only the overflow menu's contents vary. GitHub has no "Set state" equivalent (a workflow
        // step already covers assign-and-label — see GitHubWorkflowSteps), so there is no third toolbar button
        // between New session and the overflow the way YouTrack's Set state sits.
        // The whole toolbar lives inside the detail panel, which is hidden until an issue is selected — which is
        // why the handlers behind these buttons read the selection and simply exit if it is somehow empty, rather
        // than telling the operator to select an issue they have plainly already selected.
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _inject, _newSession, _overflow },
        };

        // Description gets its own scroll area rather than sharing one with the prompt preview below it, so a
        // long body does not push the preview and status line out of view together with it. The width cap does
        // nothing at the default 3:2 split — the details column is nowhere near 680 there — it is the ceiling for
        // an operator who drags the splitter over, so the text stays a readable measure instead of running the
        // full width of the dialog.
        _detailBody = new ContentControl();
        var descriptionScroll = new ScrollViewer
        {
            Name = "descriptionScroll",
            Content = new Border { MaxWidth = 680, Margin = new Thickness(0, 10, 0, 0), Child = _detailBody },
        };

        // Deliberately not wrapped: the preview shows the prompt exactly as it will be sent, and re-flowing it
        // would misrepresent a text whose own line breaks are part of what goes out. Long lines scroll sideways
        // instead (see promptScroll). Wrapping would lay out fine — the height cap below is what keeps the
        // preview from crowding out the body; this is a readability choice, not a technical constraint.
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

        // Collapsed by default: the preview only matters right before Add to prompt or Copy, so it should not
        // cost vertical room on every selection. Toggle and copy are separate hit targets, deliberately — copying
        // should not also flip the disclosure.
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

        // The preview quotes the whole body back inside the rendered template, so it is always taller than the
        // body it repeats. Docked at the bottom of the panel with nothing to bound it, it took every pixel and
        // left the body none. It gets a fixed reading height and scrolls within it: the body is what the panel is
        // for, the preview is a check on what Add to prompt is about to send.
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

        // A GridSplitter between list and details so the operator can trade width between the two — useful now
        // that the dialog itself opens wider (1280×860). 3:2 rather than 2:1 in the list's favour: the details
        // column carries the toolbar, chip and description, the list only needs enough for Title to read.
        var splitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns,
            Background = _Brush("CockpitHairlineBrush"),
        };
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

        _ = _LoadAsync(forceRefresh: false);
    }

    private void _SetStatus(string text) => _status.Text = text;

    private async Task _LoadAsync(bool forceRefresh)
    {
        _SetStatus("Loading…");
        _loading.IsVisible = true;

        try
        {
            if (!_settings.UseGitHubCli && (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo)))
            {
                _SetStatus("No repository set, and the GitHub CLI is off.");
                return;
            }

            var assignedToMe = _assignedToMe.IsChecked == true;

            // The label list is a filter aid, not the issue list itself — a repo the label lookup cannot reach must
            // not block the dialog from showing issues; the issue fetch below hits the same wall and reports it if
            // the problem is real.
            IReadOnlyList<string> labelOptions;
            try
            {
                labelOptions = _settings.UseGitHubCli
                    ? await _gh.ListRepositoryLabelsAsync(_settings.GhOwner, CancellationToken.None)
                    : await _http.GetRepositoryLabelsAsync(_settings.Owner, _settings.Repo, _settings.Token, CancellationToken.None);
            }
            catch
            {
                labelOptions = [];
            }

            _PopulateLabelFilter(labelOptions);
            var label = _labelFilter.SelectedItem as string is { Length: > 0 } selectedLabel && selectedLabel != AllLabelsOption
                ? selectedLabel
                : null;

            _all = _settings.UseGitHubCli
                ? await _gh.SearchOpenIssuesAsync(_settings.GhOwner, assignedToMe, forceRefresh, CancellationToken.None, label is null ? null : GitHubGhClient.LabelSearchTerm(label))
                : await _http.GetOpenIssuesAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe, CancellationToken.None, label);

            // AC-317: what the session's own project says it lives in, resolved once so the first population can
            // open on it. After that the filter keeps whatever the operator chose, link or no link.
            _linkedRepository ??= await _host.GetProjectFieldValueAsync(GitHubRepositoryField.Key) ?? string.Empty;

            _PopulateRepoFilter();
            _ApplyFilter();
            _ReportLoaded();
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

    // What a successful load reports — pulled out of _LoadAsync so the boundary worth proving precisely (a result
    // landing at exactly the page limit) is reachable without a live fetch (AC-519).
    private void _ReportLoaded()
    {
        var baseline = $"{_all.Count} open issue(s). Click one for details, or double-click to add it to the prompt.";
        var limit = _settings.UseGitHubCli ? GitHubGhClient.IssueSearchLimit : GitHubIssuesClient.IssuePageLimit;

        // A result of exactly the page size might have more behind it — or, rarely, might be the whole truth (a repo
        // with precisely that many open issues). There is no cheap way to tell the two apart without a second,
        // narrower request, so this warns on both; over-warning on the rare exact match is the safer of the two
        // wrong answers; a label filter is offered as the reliable way to see past it.
        _SetStatus(_all.Count == limit
            ? $"{baseline} The list may be incomplete at exactly {limit} — filter by label for the reliable set."
            : baseline);
    }

    // Rebuilds the repository dropdown from the distinct repositories in the freshly loaded issues, keeping
    // the previous selection if it is still present (otherwise falls back to "All"). On the first population that
    // is where the project's own link (AC-317) gets its one chance to be the answer — a repository the operator
    // linked on purpose, not a preference this dialog then keeps re-imposing. _repoOptionsPopulated, not a null
    // check on SelectedItem, is what tells first from later — see that field for why.
    private void _PopulateRepoFilter()
    {
        var previousSelection = _repoOptionsPopulated
            ? _repoFilter.SelectedItem as string
            : (string.IsNullOrWhiteSpace(_linkedRepository) ? null : _linkedRepository);
        _repoOptionsPopulated = true;
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

    // Rebuilds the label dropdown from the repositories' own labels (AC-519), keeping the previous selection if it
    // is still on offer. On the first population that is where the operator's own "in progress" label
    // (_preferredLabel) gets its one chance to be the answer — reusing the settings' existing notion of "what
    // counts as in progress" rather than a second setting for the same thing. _labelOptionsPopulated, not a null
    // check on SelectedItem, is what tells first from later: the ComboBox resolves SelectedIndex 0 to "All labels"
    // the moment it is constructed, well before any load runs, so SelectedItem is never actually null here. Selecting
    // here is done with the reload it triggers suppressed: this call is itself part of a reload, not the operator
    // choosing.
    private void _PopulateLabelFilter(IReadOnlyList<string> labels)
    {
        _preferredLabel ??= _settings.InProgressLabel;
        var previousSelection = _labelOptionsPopulated
            ? _labelFilter.SelectedItem as string
            : (string.IsNullOrEmpty(_preferredLabel) ? null : _preferredLabel);
        _labelOptionsPopulated = true;

        var options = new List<string> { AllLabelsOption };
        options.AddRange(labels);

        _suppressLabelFilterReload = true;
        try
        {
            _labelFilter.ItemsSource = options;
            _labelFilter.SelectedItem = previousSelection is not null && options.Contains(previousSelection)
                ? previousSelection
                : AllLabelsOption;
        }
        finally
        {
            _suppressLabelFilterReload = false;
        }
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

        // Every rebuild goes through here — a keystroke in the filter, a repository-filter change, the "Assigned
        // to me" toggle and the reload behind Refresh — and every one of them hands the grid a brand-new
        // collection, which drops its selection outright: it does not go looking through the new items for one
        // that compares equal. So the issue the operator was reading is put back by identity, here, rather than
        // at each call site (AC-292).
        var previousSelection = _grid.SelectedItem as GitHubIssue;
        var items = new ObservableCollection<GitHubIssue>(filtered);
        _grid.ItemsSource = items;
        if (IssueSelection.Restore(items, previousSelection?.Repository, previousSelection?.Number) is { } match)
        {
            _grid.SelectedItem = match;
        }
    }

    private void _ShowDetail(GitHubIssue? issue)
    {
        if (issue is null)
        {
            // The result line is deliberately left alone: an empty selection is also what a grid rebuild passes
            // through on its way back to the same issue, and the panel holding that line is hidden here anyway.
            _detailContent.IsVisible = false;
            _detailPlaceholder.IsVisible = true;
            return;
        }

        if (!string.Equals(_detailStatusFor, _IdentityOf(issue), StringComparison.Ordinal))
        {
            _detailStatus.Text = string.Empty;
            _detailStatusFor = null;
        }

        // Everything this panel is about to show is built before any of it is shown, and a failure while building it
        // empties the panel rather than leaving the last issue's content in place. The order is the point: the
        // heading used to be swapped first, so anything that threw in between left this issue's number and title
        // standing over the previous issue's body — and over the prompt "Add to prompt" injects. Emptying matters for
        // the same reason: the grid has already moved to this issue, so a panel still holding the previous one would
        // offer that one's prompt for injection under this one's selection. The exception itself is not swallowed —
        // it is not this dialog's to interpret (AC-304).
        Control description;
        string prompt;
        Control chip;
        try
        {
            description = _DescriptionView(string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body);
            prompt = _RenderPrompt(issue);
            chip = _BuildChip(issue.Repository);
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
        _detailId.Text = $"#{issue.Number}";
        _detailTitle.Text = issue.Title;

        _detailChips.Children.Clear();
        _detailChips.Children.Add(chip);

        _detailBody.Content = description;
        _renderedPrompt = prompt;
        _promptPreview.Text = _renderedPrompt;

        _UpdateInjectAvailability();
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
    // explaining why, rather than disappearing and letting the fixed row jump — New session is the route offered
    // in its place. Re-read on every selection, and again the moment New session hands back a pane: that pane is
    // the very session the button was missing (AC-292).
    private void _UpdateInjectAvailability()
    {
        _inject.IsEnabled = _actions.HasActiveSession;
        ToolTip.SetTip(_inject, _actions.HasActiveSession
            ? "Inject this issue's prompt into the active session."
            : "No active session — start one, or use New session.");
    }

    // The result of an action, tied to the issue it was produced for — see _detailStatusFor. A number alone is
    // not an identity here: it is only unique within a repository, and CLI mode lists every repo an owner has.
    private void _SetDetailStatus(GitHubIssue issue, string text)
    {
        _detailStatus.Text = text;
        _detailStatusFor = _IdentityOf(issue);
    }

    private static string _IdentityOf(GitHubIssue issue) => $"{issue.Repository}#{issue.Number}";

    // Border.tag — the theme's shape for a label that classifies the thing beside it, rather than one more
    // hand-written copy of that same shape.
    private Control _BuildChip(string text) => new Border
    {
        Classes = { "tag" },
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text },
    };

    // Hand the selected issue to Autopilot's CEO planning round (AC-174): the CEO drafts a plan from the issue (its
    // title and body as the source), the operator approves it once, then it runs autonomously.
    private async Task _PlanInAutopilotAsync(GitHubIssue issue) =>
        await _host.SendIntent("autopilot", "plan", PlanIntentPayload(issue, _IdentityOf(issue)));

    /// <summary>
    /// What "Plan in Autopilot" hands over. Kept a pure builder off the control so the payload — in particular the
    /// stage Autopilot's start gate keys on (AC-345) — is asserted without a live dialog.
    /// </summary>
    internal static Dictionary<string, string> PlanIntentPayload(GitHubIssue issue, string identity) => new()
    {
        ["tracker"] = "github-issues",
        ["issue"] = identity,
        ["title"] = issue.Title,
        ["description"] = issue.Body ?? string.Empty,
        ["repository"] = issue.Repository,
        ["url"] = issue.Url,
        // GitHub has no stage column, so its labels are what the start gate reads — one per line, because a label may
        // contain a comma but never a newline. Sent raw; the gate does the judging.
        ["stage"] = string.Join("\n", issue.Labels),
    };

    private string _RenderPrompt(GitHubIssue issue)
    {
        var parts = issue.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        return PromptTemplate.Render(_settings.Template, issue, owner, repo);
    }

    // New session: hands the same rendered prompt Add to prompt and the preview already use to the cockpit's own
    // New-session dialog, prefilled with this issue's repository and number as the session name. The operator still
    // sees and confirms every field there — nothing starts until they press Start; on cancel, nothing is linked either.
    private async Task _StartNewSessionAsync()
    {
        if (_grid.SelectedItem is not GitHubIssue issue)
        {
            return;
        }

        var prefill = new NewSessionPrefill(
            InitialPrompt: _RenderPrompt(issue),
            SessionName: SessionLabel.Name(issue))
        {
            // AC-419: the cockpit project the operator linked to this repository (AC-317) is preselected, instead of
            // the dialog opening on "No project" while already knowing which issue it is for. The issue's own
            // repository rather than the dialog's filter — in the cross-repo view the filter names no single one.
            // gh can return an issue without its repository (the same shape SessionLabel.Name falls back for), and a
            // link with nothing to match on is not one worth sending.
            LinkedProject = string.IsNullOrWhiteSpace(issue.Repository)
                ? null
                : new ProjectLink(GitHubRepositoryField.Key, issue.Repository),
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
                    _LinkIssue(paneId, issue);

                    // That pane is a live session, which is what Add to prompt was waiting for.
                    _UpdateInjectAvailability();
                    _SetDetailStatus(issue, $"Started a new session for #{issue.Number}, linked to it.");
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
    // The working directory travels with the link: a flow that cuts a branch or a worktree when an issue is picked
    // is given the path to do it in, instead of an empty string (AC-292).
    private void _LinkIssue(string paneId, GitHubIssue issue) =>
        _links.Link(paneId, issue, _host.Sessions.ActiveSessionWorkingDirectory);

    // Ties the issue to the session pane that is selected right now, and says what came of it. The pane is the
    // one the operator has in front of them — the dialog itself belongs to no session.
    private void _LinkToActiveSession(GitHubIssue issue)
    {
        if (_host.Sessions.ActivePaneId is not { Length: > 0 } paneId)
        {
            _SetDetailStatus(issue, "No active session to link this issue to.");
            return;
        }

        _LinkIssue(paneId, issue);
        _SetDetailStatus(issue, $"#{issue.Number} linked to the active session.");
    }

    // Conditional entries live only here, never as toolbar buttons that would appear and disappear: Plan in
    // Autopilot only when the plugin is installed and listening for the intent, Link to session only with an
    // active pane to link to. Open in browser and Copy prompt need no such gate — the calls behind them already
    // guard a missing issue or an empty render.
    private void _ShowOverflowMenu()
    {
        if (_grid.SelectedItem is not GitHubIssue issue)
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
            linkItem.Click += (_, _) => _LinkToActiveSession(issue);
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

    private void _AddToPrompt(GitHubIssue? issue)
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

        _ = _actions.InjectIntoActiveSessionAsync(_RenderPrompt(issue));
        _SetDetailStatus(issue, $"Added issue #{issue.Number} to the active session's prompt.");
    }

    private async Task _CopyPromptAsync()
    {
        if (_grid.SelectedItem is not GitHubIssue issue || string.IsNullOrEmpty(_renderedPrompt))
        {
            return;
        }

        await _actions.SetClipboardTextAsync(_renderedPrompt);
        _SetDetailStatus(issue, "Prompt copied to the clipboard.");
    }

    private void _OpenInBrowser()
    {
        if (_grid.SelectedItem is not GitHubIssue issue)
        {
            return;
        }

        // A launch that does not happen is said out loud rather than swallowed — the operator otherwise clicks
        // "Open in browser" and gets nothing at all, with no way to tell that from a slow browser.
        if (GitHubBrowser.Open(issue.Url) is { } failure)
        {
            _SetDetailStatus(issue, failure);
        }
    }

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : new FontFamily("Cascadia Mono, Consolas, monospace");

    /// <summary>The host's geometry token, so a plugin's box rounds like the app's other boxes.</summary>
    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
