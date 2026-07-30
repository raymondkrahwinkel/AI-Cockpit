using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The dashboard-workspace view of your open pull requests (#AC-18): the same list the always-on side-menu
/// section shows — number, title, repository, an amber stripe on the ones waiting for your review, left-click
/// to drop a review prompt, right-click for the menu — placed as a resizable pane. It reads the same
/// <see cref="PullRequestRefreshSource"/> the side section subscribes to (AC-515) and the same connection/
/// repository settings, so the two never disagree about what is open; what it adds is a per-pane "how many to
/// show", because a dashboard pane is sized by hand.
/// </summary>
/// <remarks>
/// Built in <c>Initialize</c> where the full <see cref="ICockpitHost"/> is in scope, so the closure hands it the
/// host (to inject prompts and open dialogs), this instance's <see cref="IWidgetContext"/> (its own count, its
/// refresh signal), and the plugin's one shared <see cref="PullRequestRefreshSource"/> — every widget instance
/// (a dashboard can hold more than one) reads the same source rather than polling for itself. Pull requests the
/// operator has set aside in the section are hidden here too — ignoring is a decision about a PR, not about one
/// surface — but the widget does not offer the ignore action itself, or announce review-request arrivals;
/// curation and the toast both stay with the side section.
/// </remarks>
internal sealed class GitHubPullRequestsWidget : UserControl
{
    // On top of the shared PullRequestRefreshSource's own background poll, a short debounce coalesces the burst
    // of lines a single `gh pr create` prints into one refresh.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(3);

    private readonly GitHubPullRequestsSettings _settings;
    private readonly ICockpitHost _host;
    private readonly IWidgetContext _context;
    private readonly PullRequestRefreshSource _source;
    private readonly DispatcherTimer _signalRefresh;

    private readonly TextBlock _counts;
    private readonly TextBlock _stale;
    private readonly TextBlock _status;
    private readonly StackPanel _rows;
    private readonly ProgressBar _loading = LoadingBar.Build();

    private IReadOnlyList<GitHubPullRequest> _loaded = [];
    private IReadOnlySet<string> _reviewRequested = new HashSet<string>(StringComparer.Ordinal);

    public GitHubPullRequestsWidget(GitHubPullRequestsSettings settings, ICockpitHost host, IWidgetContext context, PullRequestRefreshSource source)
    {
        _settings = settings;
        _host = host;
        _context = context;
        _source = source;

        // No refresh button of its own: the pane already wears a ↻, which reaches this through
        // RefreshRequested (below). A second one inside the pane would be two controls for one gesture.
        _counts = new TextBlock { FontSize = 11, Margin = new Thickness(2, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextSecondaryBrush") };

        // Same faint styling as the side section's marker — visible, not an alarm (IL#9 / AC-515 criterion 6).
        _stale = new TextBlock
        {
            Text = "showing an older list",
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextFaintBrush"),
            IsVisible = false,
        };

        _rows = new StackPanel { Spacing = 1 };

        _status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextFaintBrush"),
            IsVisible = false,
        };

        var viewAll = new Button
        {
            Content = "View all open PRs  →",
            Classes = { "Subtle" },
            FontSize = 12,
            Padding = new Thickness(2, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        // Same window as the side-section's "View all": two entry points, one dialog.
        viewAll.Click += (_, _) => _ = _host.ShowDialogAsync(
            "GitHub Pull Requests",
            () => new GitHubPullRequestsDialogControl(_settings, _host),
            "pull-requests",
            width: 1040,
            height: 700);

        // The list scrolls: a pane can be short and its count can be twenty, so the rows own a scroller rather
        // than pushing the "View all" link off the bottom of the pane.
        Content = new DockPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                new StackPanel { [DockPanel.DockProperty] = Dock.Top, Spacing = 4, Children = { _counts, _stale, _loading, _status } },
                new Border { [DockPanel.DockProperty] = Dock.Bottom, Child = viewAll },
                new ScrollViewer { Content = _rows },
            },
        };

        // The pane's ↻ asks the shared source for a forced refresh — the same source the side section and every
        // other widget instance read from, so "how many to show" changing here does not mean re-fetching for
        // itself; a count change just re-renders on the next Updated.
        context.RefreshRequested += (_, _) => _ = _RefreshAsync(forceRefresh: true);

        _signalRefresh = new DispatcherTimer { Interval = SignalDebounce };
        _signalRefresh.Tick += (_, _) =>
        {
            _signalRefresh.Stop();
            _ = _RefreshAsync(forceRefresh: true, quiet: true);
        };

        // Yesterday's list (or nothing, on the very first run ever), now. Never a wait: Current is always the last
        // known answer.
        _ApplySnapshot(_source.Current);
    }

    private int _MaxItems() =>
        (_context.Storage.Get<GitHubPullRequestsWidgetConfig>(GitHubPullRequestsWidgetConfig.StorageKey)
            ?? GitHubPullRequestsWidgetConfig.Default).Sanitized().MaxItems;

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        if (PullRequestSignalDetector.ContainsSignal(output.Text))
        {
            _signalRefresh.Stop();
            _signalRefresh.Start();
        }
    }

    private void _OnSourceUpdated(object? sender, PullRequestFeedSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => _ApplySnapshot(snapshot));

    // The background poll lives on the shared source now and runs regardless of any pane being on screen; what is
    // tied to attach/detach here is only this instance's own subscription — unsubscribing on detach matters more
    // for a widget than the always-on side section, since a pane can be removed from a dashboard entirely.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _source.Updated += _OnSourceUpdated;
        _ApplySnapshot(_source.Current);
        _context.Sessions.OutputProduced += _OnSessionOutput;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _source.Updated -= _OnSourceUpdated;
        _signalRefresh.Stop();
        _context.Sessions.OutputProduced -= _OnSessionOutput;
    }

    /// <summary>What the pane's ↻ and the signal-refresh debounce both do: ask the shared source, and only a non-quiet caller reports a failure.</summary>
    private async Task _RefreshAsync(bool forceRefresh, bool quiet = false)
    {
        _loading.IsVisible = !quiet;
        try
        {
            var ran = await _source.RefreshAsync(forceRefresh);
            if (!quiet && ran && _source.LastError is { } error)
            {
                _Say($"Could not load pull requests: {error.Message}");
            }
        }
        finally
        {
            _loading.IsVisible = false;
        }
    }

    /// <summary>Draws one snapshot from the shared source. See the side section's own copy of this reasoning — the two surfaces read the same source but never announce arrivals themselves; curation stays with the persistent list.</summary>
    private void _ApplySnapshot(PullRequestFeedSnapshot snapshot)
    {
        var result = snapshot.Result;
        if (result.RepositoryMissing)
        {
            _stale.IsVisible = false;
            _Say("No repository set, and the GitHub CLI is off — open the plugin's settings.");
            return;
        }

        _reviewRequested = result.ReviewRequested.Select(pullRequest => pullRequest.Url).ToHashSet(StringComparer.Ordinal);
        _loaded = result.PullRequests;
        var fetchedAt = snapshot.FetchedAt;
        _stale.IsVisible = fetchedAt is { } at && DateTimeOffset.UtcNow - at > PullRequestRefreshSource.StaleAfter;

        _Say(null);
        _Render();

        if (fetchedAt is null && _loaded.Count == 0)
        {
            _loading.IsVisible = _source.LastError is null;
            _Say(_source.LastError is { } error ? $"Could not load pull requests: {error.Message}" : "Loading pull requests…");
        }
    }

    private void _Render()
    {
        var ignored = _settings.IgnoredPullRequests;
        var ignoredRepositories = _settings.IgnoredRepositories;

        bool IsIgnored(GitHubPullRequest pullRequest) =>
            ignored.Contains(pullRequest.Url) || ignoredRepositories.Contains(pullRequest.Repository);

        var open = _loaded.Where(pullRequest => !IsIgnored(pullRequest)).ToList();
        var showing = open.Take(_MaxItems()).ToList();

        _rows.Children.Clear();
        foreach (var pullRequest in showing)
        {
            _rows.Children.Add(_BuildRow(pullRequest));
        }

        var waiting = open.Count(pullRequest => _reviewRequested.Contains(pullRequest.Url));
        _counts.Text = waiting > 0
            ? $"{(open.Count == 1 ? "1 open" : $"{open.Count} open")} · {(waiting == 1 ? "1 waiting on you" : $"{waiting} waiting on you")}"
            : open.Count == 1 ? "1 open" : $"{open.Count} open";

        if (_loaded.Count > 0 && open.Count == 0)
        {
            _Say("Every open pull request is ignored — manage them from the Open PRs section.");
        }
        else if (_loaded.Count == 0)
        {
            _Say("No open pull requests.");
        }
        else
        {
            _Say(null);
        }
    }

    // One row: number and title on a line, the repository under it, an "open in browser" action that appears on
    // hover; left-click drops the review prompt, right-click opens the menu. The amber stripe is what a review
    // request wears instead of a list of its own.
    private Control _BuildRow(GitHubPullRequest pullRequest)
    {
        var isWaiting = _reviewRequested.Contains(pullRequest.Url);

        var number = new TextBlock
        {
            Text = $"#{pullRequest.Number}",
            FontSize = 11,
            FontFamily = _MonoFont(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isWaiting ? _Brush("CockpitStatusWaitingBrush") : _Brush("CockpitTextFaintBrush"),
            Margin = new Thickness(0, 0, 6, 0),
        };

        var title = new TextBlock
        {
            Text = pullRequest.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = new DockPanel();
        DockPanel.SetDock(number, Dock.Left);
        line.Children.Add(number);
        line.Children.Add(title);

        var repository = new TextBlock
        {
            Text = isWaiting ? $"{pullRequest.Repository} · waiting on your review" : pullRequest.Repository,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextFaintBrush"),
        };

        var addToPrompt = _RowAction(MaterialIconKind.ContentCopy, "Add to the prompt");
        addToPrompt.Click += async (_, e) =>
        {
            e.Handled = true;
            await PullRequestActions.InjectAsync(_host, _settings, pullRequest);
        };

        var openInBrowser = _RowAction(MaterialIconKind.OpenInNew, "Open in the browser");
        openInBrowser.Click += (_, e) =>
        {
            e.Handled = true;
            PullRequestActions.OpenInBrowser(_host, pullRequest.Url);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { addToPrompt, openInBrowser },
        };

        var content = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        content.Children.Add(actions);
        content.Children.Add(new StackPanel { Spacing = 1, Children = { line, repository } });

        var row = new Button
        {
            Classes = { "Subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(7, 5),
            Content = content,
        };
        ToolTip.SetTip(row, pullRequest.Title);
        row.Click += async (_, _) => await PullRequestActions.InjectAsync(_host, _settings, pullRequest);
        row.PointerEntered += (_, _) => actions.Opacity = 1;
        row.PointerExited += (_, _) => actions.Opacity = 0;
        row.ContextMenu = _RowMenu(pullRequest);

        return new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = isWaiting ? _Brush("CockpitStatusWaitingBrush") : Brushes.Transparent,
            Child = row,
        };
    }

    private ContextMenu _RowMenu(GitHubPullRequest pullRequest)
    {
        var addToPrompt = new MenuItem { Header = "Add to prompt" };
        addToPrompt.Click += async (_, _) => await PullRequestActions.InjectAsync(_host, _settings, pullRequest);

        var openInBrowser = new MenuItem { Header = "Open in browser" };
        openInBrowser.Click += (_, _) => PullRequestActions.OpenInBrowser(_host, pullRequest.Url);

        return new ContextMenu { ItemsSource = new Control[] { addToPrompt, openInBrowser } };
    }

    private static Button _RowAction(MaterialIconKind icon, string tip)
    {
        var button = new Button
        {
            Content = new MaterialIcon { Kind = icon, Width = 11, Height = 11 },
            Classes = { "Subtle" },
            FontSize = 11,
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);

        return button;
    }

    private void _Say(string? message)
    {
        _status.Text = message ?? string.Empty;
        _status.IsVisible = !string.IsNullOrEmpty(message);
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : FontFamily.Default;
}
