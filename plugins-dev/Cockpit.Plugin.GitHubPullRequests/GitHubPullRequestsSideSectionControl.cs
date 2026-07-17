using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The inline accordion section registered via <see cref="ICockpitHost.AddSideMenuSection"/>, always visible under the
/// session list: the open pull requests, each a row that puts the review prompt into the active session (or, with no
/// session open, on the clipboard), and a link to the full <see cref="GitHubPullRequestsDialogControl"/> dialog.
/// <para>
/// Rebuilt from the approved mockup. It used to be two stacked lists — "Review requested" above your own open PRs —
/// with a heading only on the first, so the two read as one list in which a single row inexplicably had an "Open"
/// button. Now it is <em>one</em> list: a pull request waiting on you carries an amber stripe and an amber number,
/// which says the same thing without a second list to say it in. Each row wears its actions only while the pointer is
/// on it, so a list of things to read stays a list of things to read.
/// </para>
/// <para>
/// A pull request can be <em>ignored</em> (right-click): the long-lived ones that live in a todo somewhere and do not
/// need to be in front of you every day. Ignoring hides it and says so — the count stays visible and puts them back
/// with one click, because a thing that disappears with no way back is a thing you stop trusting the list about.
/// </para>
/// </summary>
internal sealed class GitHubPullRequestsSideSectionControl : UserControl
{
    // Auto-refresh so PRs appear/disappear on their own as they are opened, merged or closed (Raymond's ask)
    // without the operator clicking ⟳. A quiet, force-refreshing background poll — its interval is comfortably
    // above the gh client's own 60s cache TTL, and it only runs while the section is on screen.
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>How old the remembered list may be and still be worth showing while the fresh one loads.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    // On top of the periodic poll, the section refreshes near-instantly when it sees a PR signal in session
    // output (a pull url, a merged/closed line) via the read/observe surface. A short debounce coalesces the
    // burst of lines a single `gh pr create` prints into one refresh, and waits out the gh-side propagation
    // so the just-changed PR is actually reflected by the time we re-query.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(3);

    private readonly GitHubPullRequestsSettings _settings;
    private readonly ICockpitHost _host;
    private readonly PullRequestFeed _feed = new();
    private readonly DispatcherTimer _autoRefresh;
    private readonly DispatcherTimer _signalRefresh;

    private readonly TextBlock _counts;
    private readonly TextBlock _waiting;
    private readonly Button _ignoredToggle;
    private readonly TextBlock _status;
    private readonly StackPanel _rows;
    private readonly ProgressBar _loading = LoadingBar.Build();

    // What the last load produced, so ignoring one (or showing the ignored ones) re-renders without re-querying
    // GitHub: neither is news from GitHub, and a round trip to redraw a list you already have is a stall.
    private IReadOnlyList<GitHubPullRequest> _loaded = [];
    private IReadOnlySet<string> _reviewRequested = new HashSet<string>(StringComparer.Ordinal);
    private bool _showIgnored;

    public GitHubPullRequestsSideSectionControl(GitHubPullRequestsSettings settings, ICockpitHost host)
    {
        _settings = settings;
        _host = host;

        _counts = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextSecondaryBrush") };
        _waiting = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitStatusWaitingBrush"),
            IsVisible = false,
        };

        // The ignored ones are not gone, they are set aside — and the count is the way back.
        _ignoredToggle = new Button
        {
            Classes = { "Subtle" },
            FontSize = 11,
            Padding = new Thickness(4, 1),
            IsVisible = false,
            Foreground = _Brush("CockpitTextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ignoredToggle.Click += (_, _) =>
        {
            _showIgnored = !_showIgnored;
            _Render();
        };

        var refresh = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.Refresh, Width = 12, Height = 12 },
            Classes = { "Subtle" },
            FontSize = 12,
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        ToolTip.SetTip(refresh, "Refresh");
        refresh.Click += async (_, _) => await _LoadAsync(forceRefresh: true);

        var countsRow = new DockPanel { Margin = new Thickness(2, 0, 0, 2) };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(_ignoredToggle, Dock.Right);
        countsRow.Children.Add(refresh);
        countsRow.Children.Add(_ignoredToggle);
        countsRow.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _counts,
                new TextBlock { Text = "·", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextFaintBrush"), IsVisible = false },
                _waiting,
            },
        });

        // The middle dot only earns its place when there is something on both sides of it.
        var separator = (TextBlock)((StackPanel)countsRow.Children[^1]).Children[1];
        _waiting.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                separator.IsVisible = _waiting.IsVisible;
            }
        };

        _rows = new StackPanel { Spacing = 1 };

        _status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextFaintBrush"),
            IsVisible = false,
        };

        // A quiet link, not a filled accent button: this section is a list, and the button that led away from it was
        // the loudest thing in it.
        var viewAll = new Button
        {
            Content = "View all open PRs  →",
            Classes = { "Subtle" },
            FontSize = 12,
            Padding = new Thickness(2, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        viewAll.Click += (_, _) => _ = _host.ShowDialogAsync(
            "GitHub Pull Requests",
            () => new GitHubPullRequestsDialogControl(_settings, _host),
            1040,
            700);

        // Under the counts, above the list: this section refreshes on a timer and whenever a session touches a pull
        // request, so it is often working while nobody asked it to. Without this the list simply looks stale — or,
        // on the first load, empty.
        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 4,
            Children = { countsRow, _loading, _status, _rows, viewAll },
        };

        // Re-fetch with the just-saved settings (owner/repo, token, gh-CLI toggle) instead of leaving this
        // already-built section showing data loaded under the old configuration until an app restart (#52).
        host.OnSettingsSaved(() => _ = _LoadAsync(forceRefresh: true));

        // Yesterday's list, now, while today's is on its way. Fetching takes seconds — every gh query is a process
        // spawn and a round trip — and an empty panel for those seconds does not read as "loading", it reads as
        // "no open pull requests", which is a lie the operator acts on.
        _ShowCached();
        _ = _LoadAsync(forceRefresh: false);

        _autoRefresh = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefresh.Tick += (_, _) => _ = _LoadAsync(forceRefresh: true, quiet: true);

        // Smart refresh: watch session output for a PR signal and refresh once the debounce settles. The
        // observer marshals OutputProduced to the UI thread, so touching the timer here is safe.
        _signalRefresh = new DispatcherTimer { Interval = SignalDebounce };
        _signalRefresh.Tick += (_, _) =>
        {
            _signalRefresh.Stop();
            _ = _LoadAsync(forceRefresh: true, quiet: true);
        };
    }

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        // A single create/merge can print the url several times; (re)start the debounce so the burst collapses
        // to one refresh a moment after the last line.
        if (PullRequestSignalDetector.ContainsSignal(output.Text))
        {
            _signalRefresh.Stop();
            _signalRefresh.Start();
        }
    }

    // The section is always on screen while the plugin is loaded, so tie the poll to attach/detach: it runs
    // while visible and stops (no orphaned gh spawns) once the pane goes away.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _autoRefresh.Start();
        _host.Sessions.OutputProduced += _OnSessionOutput;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _autoRefresh.Stop();
        _signalRefresh.Stop();
        _host.Sessions.OutputProduced -= _OnSessionOutput;
    }

    private async Task _LoadAsync(bool forceRefresh, bool quiet = false)
    {
        // A background poll that nobody asked for does not announce itself: the bar is for a load the operator is
        // waiting on (the first one, or a Refresh they clicked), not for the timer ticking behind their back.
        _loading.IsVisible = !quiet;
        try
        {
            var result = await _feed.LoadAsync(_settings, forceRefresh, CancellationToken.None);

            // The HTTP mode talks to one repository; with none set there is nothing to query, and an empty list
            // here would read as "no open pull requests" rather than "not configured".
            if (result.RepositoryMissing)
            {
                _Say("No repository set, and the GitHub CLI is off — open the settings above.");
                return;
            }

            // A review request is news this section acts on — it announces the new ones (a toast) and stripes
            // them — where the feed only reports them. In HTTP mode the feed finds none, and skipping the
            // announce there leaves the seen-set untouched rather than clearing it.
            _reviewRequested = result.ReviewRequested.Select(pullRequest => pullRequest.Url).ToHashSet(StringComparer.Ordinal);
            if (_settings.UseGitHubCli)
            {
                _AnnounceArrivals(result.ReviewRequested);
            }

            _loaded = result.PullRequests;

            _Say(null);
            _Render();
            _RememberForNextTime();
        }
        catch (Exception exception)
        {
            // A quiet background poll that fails keeps the last good list rather than flashing an error; an
            // explicit (manual/settings) load surfaces it.
            if (!quiet)
            {
                _Say($"Could not load pull requests: {exception.Message}");
            }
        }
        finally
        {
            // In a finally: a bar still moving after a failure says the thing is still coming, which is the one
            // message it must never send.
            _loading.IsVisible = false;
        }
    }

    /// <summary>The list from last time, drawn at once. Nothing is shown if it is older than a day: a list that stale is misinformation, not a head start.</summary>
    private void _ShowCached()
    {
        try
        {
            if (_settings.CachedAt is not { } at || DateTimeOffset.UtcNow - at > CacheLifetime)
            {
                return;
            }

            if (JsonSerializer.Deserialize<List<GitHubPullRequest>>(_settings.CachedPullRequests) is { Count: > 0 } cached)
            {
                _loaded = cached;
                _Render();
            }
        }
        catch (Exception)
        {
            // A cache written by an older version of this plugin, in a shape this one does not read. Not worth a
            // word to the operator: the fresh list is already on its way.
        }
    }

    /// <summary>Keeps what was loaded, so the next start has something to show while it fetches.</summary>
    private void _RememberForNextTime()
    {
        try
        {
            _settings.CachedPullRequests = JsonSerializer.Serialize(_loaded);
            _settings.CachedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception)
        {
            // A cache that cannot be written costs the next start a few seconds. It is not worth failing a load over.
        }
    }

    /// <summary>Draws what was last loaded, under the operator's own choices: what is ignored, and whether the ignored are shown.</summary>
    private void _Render()
    {
        var ignored = _settings.IgnoredPullRequests;
        var ignoredRepositories = _settings.IgnoredRepositories;

        // Two ways to be set aside — this one pull request, or everything from this repository. They behave the
        // same everywhere afterwards, so the operator never has to remember which of the two they used.
        bool IsIgnored(GitHubPullRequest pullRequest) =>
            ignored.Contains(pullRequest.Url) || ignoredRepositories.Contains(pullRequest.Repository);

        var showing = _loaded
            .Where(pullRequest => _showIgnored || !IsIgnored(pullRequest))
            .Take(_settings.MaxItems)
            .ToList();

        _rows.Children.Clear();
        foreach (var pullRequest in showing)
        {
            _rows.Children.Add(_BuildRow(pullRequest, isIgnored: IsIgnored(pullRequest)));
        }

        var open = _loaded.Count(pullRequest => !IsIgnored(pullRequest));
        var waiting = _loaded.Count(pullRequest => !IsIgnored(pullRequest) && _reviewRequested.Contains(pullRequest.Url));
        var ignoredHere = _loaded.Count(IsIgnored);

        _counts.Text = open == 1 ? "1 open" : $"{open} open";
        _waiting.Text = waiting == 1 ? "1 waiting on you" : $"{waiting} waiting on you";
        _waiting.IsVisible = waiting > 0;

        _ignoredToggle.IsVisible = ignoredHere > 0;
        _ignoredToggle.Content = _showIgnored ? $"{ignoredHere} ignored — hide" : $"{ignoredHere} ignored";
        ToolTip.SetTip(_ignoredToggle, _showIgnored ? "Hide the pull requests you set aside" : "Show the pull requests you set aside");

        if (_loaded.Count == 0)
        {
            _Say("No open pull requests.");
        }
        else if (showing.Count == 0)
        {
            _Say("Every open pull request is ignored — the count above puts them back.");
        }
    }

    // One row: number and title on a line of their own (trimmed, with the whole title on hover), the repository
    // under it, and the two actions — which appear only while the pointer is on the row.
    private Control _BuildRow(GitHubPullRequest pullRequest, bool isIgnored)
    {
        var isWaiting = _reviewRequested.Contains(pullRequest.Url);

        var number = new TextBlock
        {
            Text = $"#{pullRequest.Number}",
            FontSize = 11,
            FontFamily = _MonoFont(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isWaiting ? _Brush("CockpitStatusWaitingBrush") : _Brush("CockpitTextFaintBrush"),
        };

        var title = new TextBlock
        {
            Text = pullRequest.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = isIgnored ? TextDecorations.Strikethrough : null,
        };

        var line = new DockPanel();
        DockPanel.SetDock(number, Dock.Left);
        number.Margin = new Thickness(0, 0, 6, 0);
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
            await _InjectAsync(pullRequest);
        };

        var openInBrowser = _RowAction(MaterialIconKind.OpenInNew, "Open in the browser");
        openInBrowser.Click += (_, e) =>
        {
            e.Handled = true;
            _OpenInBrowser(pullRequest.Url);
        };

        // Present but invisible until hovered: a row that grows a pair of buttons under the pointer would push its own
        // text sideways as you read it.
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
            Opacity = isIgnored ? 0.45 : 1,
            Content = content,
        };
        ToolTip.SetTip(row, pullRequest.Title);
        row.Click += async (_, _) => await _InjectAsync(pullRequest);
        row.PointerEntered += (_, _) => actions.Opacity = 1;
        row.PointerExited += (_, _) => actions.Opacity = 0;
        row.ContextMenu = _RowMenu(pullRequest, isIgnored);

        // The stripe is what a review request has instead of a list of its own.
        return new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = isWaiting ? _Brush("CockpitStatusWaitingBrush") : Brushes.Transparent,
            Child = row,
        };
    }

    private ContextMenu _RowMenu(GitHubPullRequest pullRequest, bool isIgnored)
    {
        var addToPrompt = new MenuItem { Header = "Add to prompt" };
        addToPrompt.Click += async (_, _) => await _InjectAsync(pullRequest);

        var openInBrowser = new MenuItem { Header = "Open in browser" };
        openInBrowser.Click += (_, _) => _OpenInBrowser(pullRequest.Url);

        var ignore = new MenuItem { Header = isIgnored ? "Show again" : "Ignore this pull request" };
        ToolTip.SetTip(ignore, isIgnored
            ? "Put this pull request back in the list"
            : "Set this one aside — for the ones that stay open and live in a todo somewhere");
        ignore.Click += (_, _) => _SetIgnored(pullRequest, !isIgnored);

        // A repository whose pull requests are never your business keeps opening new ones, so ignoring them one by
        // one is a chore that never ends.
        var repositoryIgnored = _settings.IgnoredRepositories.Contains(pullRequest.Repository);
        var ignoreRepository = new MenuItem
        {
            Header = repositoryIgnored
                ? $"Show {pullRequest.Repository} again"
                : $"Ignore everything in {pullRequest.Repository}",
        };
        ignoreRepository.Click += (_, _) => _SetRepositoryIgnored(pullRequest.Repository, !repositoryIgnored);

        return new ContextMenu
        {
            ItemsSource = new Control[] { addToPrompt, openInBrowser, new Separator(), ignore, ignoreRepository },
        };
    }

    private void _SetRepositoryIgnored(string repository, bool ignored)
    {
        var repositories = _settings.IgnoredRepositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ignored)
        {
            repositories.Add(repository);
        }
        else
        {
            repositories.Remove(repository);
        }

        _settings.IgnoredRepositories = repositories;
        _Render();
    }

    private void _SetIgnored(GitHubPullRequest pullRequest, bool ignored)
    {
        var urls = _settings.IgnoredPullRequests.ToHashSet(StringComparer.Ordinal);
        if (ignored)
        {
            urls.Add(pullRequest.Url);
        }
        else
        {
            urls.Remove(pullRequest.Url);
        }

        _settings.IgnoredPullRequests = urls;

        // Ignoring the last one you could see while the ignored are hidden would leave an empty list with no
        // explanation; showing them keeps what just happened on screen.
        _Render();
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

    // A review request that was already waiting when the plugin first looked is not news, so the first load
    // only primes the seen-set (it has no stored one yet) and stays quiet. After that, every request that was
    // not there last time is announced once.
    private void _AnnounceArrivals(IReadOnlyList<GitHubPullRequest> reviewRequested)
    {
        var seen = _settings.SeenReviewRequests;
        var inbox = ReviewRequestInbox.Reconcile(reviewRequested, seen ?? new HashSet<string>(StringComparer.Ordinal));
        _settings.SeenReviewRequests = inbox.Seen;

        if (seen is null || !_settings.NotifyOnReviewRequests)
        {
            return;
        }

        var ignored = _settings.IgnoredPullRequests;
        foreach (var pullRequest in inbox.Arrived.Where(pullRequest => !ignored.Contains(pullRequest.Url)))
        {
            _host.ShowToast(
                $"Review requested — #{pullRequest.Number} {pullRequest.Title} ({pullRequest.Repository})",
                PluginToastSeverity.Information,
                "Open in browser",
                () => _OpenInBrowser(pullRequest.Url));
        }
    }

    /// <summary>The one line this section has for what it cannot show: an error, or an empty list. Absent when there is nothing to say.</summary>
    private void _Say(string? message)
    {
        _status.Text = message ?? string.Empty;
        _status.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void _OpenInBrowser(string? url) => PullRequestActions.OpenInBrowser(_host, url);

    private Task _InjectAsync(GitHubPullRequest pullRequest) => PullRequestActions.InjectAsync(_host, _settings, pullRequest);

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static FontFamily _MonoFont() =>
        Application.Current?.TryFindResource("CockpitMonoFont", out var value) == true && value is FontFamily font
            ? font
            : FontFamily.Default;
}
