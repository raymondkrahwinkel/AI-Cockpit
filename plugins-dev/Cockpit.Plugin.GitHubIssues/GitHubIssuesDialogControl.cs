using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The "GitHub Issues" dialog opened from the left-menu button: a repository filter, a search box, and a
/// sortable <see cref="DataGrid"/> of open issues (across all repos in GitHub CLI mode, or one repo in HTTP
/// mode) on the left, and a details panel on the right — number, title, a repository chip, a rendered
/// description, a fixed action toolbar and a collapsible preview of the prompt it would produce (with a copy
/// button). The repository filter is populated from the distinct <see cref="GitHubIssue.Repository"/> values in
/// the loaded issues plus an "All" entry; it filters the grid client-side. "Add to prompt" injects into the
/// active session; "New session" (mirroring the YouTrack dialog) hands the same prompt to the cockpit's own
/// New-session dialog instead. Built in code; the DataGrid theme is provided app-wide by the host.
/// </summary>
internal sealed class GitHubIssuesDialogControl : UserControl
{
    private const string AllRepositoriesOption = "All";

    private readonly GitHubIssuesSettings _settings;
    private readonly ICockpitHost _host;
    private readonly ICockpitActions _actions;
    private readonly SessionIssueLinks _links;
    private readonly GitHubIssuesClient _http = new();
    private readonly GitHubGhClient _gh = new();

    private readonly ComboBox _repoFilter;
    private readonly CheckBox _assignedToMe;
    private readonly TextBox _search;

    // The window-level status line: fetch/load/refresh state, and the guard messages ("no repository set") that
    // fire before any issue is even selected. Once the detail panel is open, _detailStatus takes over reporting
    // what an action on that issue did — the two never cover the same ground.
    private readonly TextBlock _status;
    private readonly ProgressBar _loading = LoadingBar.Build();
    private readonly DataGrid _grid;

    private readonly TextBlock _detailPlaceholder;
    private readonly DockPanel _detailContent;
    private readonly TextBlock _detailId;
    private readonly TextBlock _detailTitle;
    private readonly Button _openLink;
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

    public GitHubIssuesDialogControl(GitHubIssuesSettings settings, ICockpitHost host, SessionIssueLinks links)
    {
        _settings = settings;
        _host = host;
        _actions = host.Actions;
        _links = links;

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

        // Details panel (right). Number + title, with the issue's url moved off the text and onto a single icon
        // button — it used to appear spelled out twice (the meta line and "Open in browser"'s own label); now
        // only the rendered prompt still carries it, because that copy is the literal text a session receives.
        _detailId = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.65 };
        _detailTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _openLink = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.OpenInNew, Width = 15, Height = 15 },
            Padding = new Thickness(5),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(_openLink, "Open in browser");
        _openLink.Click += (_, _) => _OpenInBrowser(_grid.SelectedItem as GitHubIssue);

        var titleRow = new DockPanel();
        DockPanel.SetDock(_openLink, Dock.Right);
        titleRow.Children.Add(_openLink);
        titleRow.Children.Add(new StackPanel { Children = { _detailId, _detailTitle } });

        // A single chip, replacing the old "{repo} · #{number} · {url}" meta line: Repository is the only field
        // a GitHubIssue carries beyond number/title/body/url — there is no status chip to add, since a GitHub
        // issue has no status field and this dialog only ever lists open ones.
        _detailChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        _inject = new Button { Content = "Add to prompt", Classes = { "Accent" } };
        _inject.Click += (_, _) => _AddToPrompt(_grid.SelectedItem as GitHubIssue);

        _newSession = new Button { Content = "New session" };
        _newSession.Click += (_, _) => _StartNewSession(_grid.SelectedItem as GitHubIssue);

        _overflow = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.DotsHorizontal, Width = 16, Height = 16 },
            Padding = new Thickness(8, 4),
        };
        ToolTip.SetTip(_overflow, "More actions");
        _overflow.Click += (_, _) => _ShowOverflowMenu(_grid.SelectedItem as GitHubIssue);

        // A fixed row: the same controls, in the same order, whether or not Autopilot is installed or a session
        // is active — only the overflow menu's contents vary. GitHub has no "Set state" equivalent (a workflow
        // step already covers assign-and-label — see GitHubWorkflowSteps), so there is no third toolbar button
        // between New session and the overflow the way YouTrack's Set state sits.
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _inject, _newSession, _overflow },
        };

        // Description gets its own scroll area rather than sharing one with the prompt preview below it, so a
        // long body does not push the preview and status line out of view together with it. Capped to a
        // readable measure rather than stretching edge-to-edge on the wider dialog.
        _detailBody = new ContentControl();
        var descriptionScroll = new ScrollViewer
        {
            Content = new Border { MaxWidth = 680, Margin = new Thickness(0, 10, 0, 0), Child = _detailBody },
        };

        _promptPreview = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
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

        var promptBlock = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4, 0, 0),
            Child = _promptPreview,
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

        // Captured before the fetch so the freshly reloaded list (a brand-new ObservableCollection<GitHubIssue>
        // each time — see _ApplyFilter) can be matched back to whichever issue was selected, by identity rather
        // than by the DataGrid finding an equal object on its own — it does not (the same defect YouTrack's
        // dialog had, AC-299 bug 2).
        var previousSelection = _grid.SelectedItem as GitHubIssue;
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
                    _SetStatus("No repository set, and the GitHub CLI is off.");
                    return;
                }

                _all = await _http.GetOpenIssuesAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe, CancellationToken.None);
            }

            _PopulateRepoFilter();
            _ApplyFilter();
            _RestoreSelection(previousSelection?.Repository, previousSelection?.Number);
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

    // See the field doc on _RestoreSelection's caller (_LoadAsync) for why this matches by Repository+Number
    // instead of relying on the DataGrid to keep a selection across an ItemsSource swap.
    private void _RestoreSelection(string? repository, int? number)
    {
        if (IssueSelection.Restore(_grid.ItemsSource as IEnumerable<GitHubIssue> ?? [], repository, number) is { } match)
        {
            _grid.SelectedItem = match;
        }
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
        _detailId.Text = $"#{issue.Number}";
        _detailTitle.Text = issue.Title;

        _detailChips.Children.Clear();
        _detailChips.Children.Add(_BuildChip(issue.Repository));

        _detailBody.Content = _host.CreateMarkdownView(string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body);
        _renderedPrompt = _RenderPrompt(issue);
        _promptPreview.Text = _renderedPrompt;

        // Add to prompt only makes sense with a live session; it stays put and just goes inert with a tooltip
        // explaining why, rather than disappearing and letting the fixed row jump — New session is the route
        // offered in its place.
        _inject.IsEnabled = _actions.HasActiveSession;
        ToolTip.SetTip(_inject, _actions.HasActiveSession
            ? "Inject this issue's prompt into the active session."
            : "No active session — start one, or use New session.");
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
    // title and body as the source), the operator approves it once, then it runs autonomously.
    private async Task _PlanInAutopilotAsync(GitHubIssue? issue)
    {
        if (issue is null)
        {
            return;
        }

        var data = new Dictionary<string, string>
        {
            ["tracker"] = "github-issues",
            ["issue"] = $"{issue.Repository}#{issue.Number}",
            ["title"] = issue.Title,
            ["description"] = issue.Body ?? string.Empty,
            ["repository"] = issue.Repository,
            ["url"] = issue.Url,
        };

        await _host.SendIntent("autopilot", "plan", data);
    }

    private string _RenderPrompt(GitHubIssue issue)
    {
        var parts = issue.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        return PromptTemplate.Render(_settings.Template, issue, owner, repo);
    }

    // New session: hands the same rendered prompt Add to prompt and the preview already use to the cockpit's own
    // New-session dialog, prefilled with this issue's number as the session name. The operator still sees and
    // confirms every field there — nothing starts until they press Start; on cancel, nothing is linked either.
    private void _StartNewSession(GitHubIssue? issue)
    {
        if (issue is null)
        {
            _SetStatus("Select an issue first.");
            return;
        }

        var prefill = new NewSessionPrefill(
            InitialPrompt: _RenderPrompt(issue),
            SessionName: $"#{issue.Number}");

        _ = _host.ShowNewSessionDialogAsync(
            prefill,
            onStarted: paneId =>
            {
                _LinkIssue(paneId, issue);
                _detailStatus.Text = $"Started a new session for #{issue.Number}, linked to it.";
            },
            onCancelled: () => _detailStatus.Text = "New session cancelled.");
    }

    // The one place that actually calls SessionIssueLinks.Link — shared by "Link to session" (the active pane)
    // and New session's onStarted callback (the pane it just created), so the two do not each keep their own copy.
    private void _LinkIssue(string paneId, GitHubIssue issue) => _links.Link(paneId, issue);

    // Ties the issue to the session pane that is selected right now. Returns the resulting message rather than
    // setting _detailStatus directly, so a future caller with something else to report could combine both into
    // one line instead of one overwriting the other (the same shape as YouTrack's Start work / link combination).
    private string _LinkToActiveSession(GitHubIssue? issue)
    {
        if (issue is null)
        {
            return string.Empty;
        }

        if (_host.Sessions.ActivePaneId is not { Length: > 0 } paneId)
        {
            return "No active session to link this issue to.";
        }

        _LinkIssue(paneId, issue);
        return $"#{issue.Number} linked to the active session.";
    }

    // Conditional entries live only here, never as toolbar buttons that would appear and disappear: Plan in
    // Autopilot only when the plugin is installed and listening for the intent, Link to session only with an
    // active pane to link to. Open in browser and Copy prompt need no such gate — the calls behind them already
    // guard a missing issue or an empty render.
    private void _ShowOverflowMenu(GitHubIssue? issue)
    {
        if (issue is null)
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
            linkItem.Click += (_, _) => _detailStatus.Text = _LinkToActiveSession(issue);
            items.Add(linkItem);
        }

        var openItem = new MenuItem { Header = "Open in browser" };
        openItem.Click += (_, _) => _OpenInBrowser(issue);
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
            _detailStatus.Text = "No active session — use Copy to put the prompt on the clipboard.";
            return;
        }

        _ = _actions.InjectIntoActiveSessionAsync(_RenderPrompt(issue));
        _detailStatus.Text = $"Added issue #{issue.Number} to the active session's prompt.";
    }

    private async Task _CopyPromptAsync()
    {
        if (string.IsNullOrEmpty(_renderedPrompt))
        {
            return;
        }

        await _actions.SetClipboardTextAsync(_renderedPrompt);
        _detailStatus.Text = "Prompt copied to the clipboard.";
    }

    private void _OpenInBrowser(GitHubIssue? issue)
    {
        if (issue is null || string.IsNullOrWhiteSpace(issue.Url))
        {
            _SetStatus("Select an issue first.");
            return;
        }

        GitHubBrowser.Open(issue.Url);
    }

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : new FontFamily("Cascadia Mono, Consolas, monospace");

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
