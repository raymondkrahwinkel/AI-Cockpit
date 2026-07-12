using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The inline accordion section registered via <see cref="ICockpitHost.AddSideMenuSection"/>, always visible
/// under the session list: up to <see cref="MaxItems"/> open pull requests (across all repos in GitHub CLI
/// mode, or one repo in HTTP mode), each a clickable row that renders the prompt template and injects it into
/// the active session — or, with no active session, copies it to the clipboard instead — plus a "View all
/// open PRs" button that opens the full <see cref="GitHubPullRequestsDialogControl"/> dialog.
/// </summary>
internal sealed class GitHubPullRequestsSideSectionControl : UserControl
{
    private const int MaxItems = 5;

    // Auto-refresh so PRs appear/disappear on their own as they are opened, merged or closed (Raymond's ask)
    // without the operator clicking ⟳. A quiet, force-refreshing background poll — its interval is comfortably
    // above the gh client's own 60s cache TTL, and it only runs while the section is on screen.
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(60);

    // On top of the periodic poll, the section refreshes near-instantly when it sees a PR signal in session
    // output (a pull url, a merged/closed line) via the read/observe surface. A short debounce coalesces the
    // burst of lines a single `gh pr create` prints into one refresh, and waits out the gh-side propagation
    // so the just-changed PR is actually reflected by the time we re-query.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(3);

    private readonly GitHubPullRequestsSettings _settings;
    private readonly ICockpitHost _host;
    private readonly GitHubPullRequestsClient _http = new();
    private readonly GitHubPrGhClient _gh = new();
    private readonly DispatcherTimer _autoRefresh;
    private readonly DispatcherTimer _signalRefresh;

    private readonly TextBlock _status;
    private readonly StackPanel _list;

    public GitHubPullRequestsSideSectionControl(GitHubPullRequestsSettings settings, ICockpitHost host)
    {
        _settings = settings;
        _host = host;

        _status = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };

        var refresh = new Button { Content = "⟳", FontSize = 11, Padding = new Thickness(6, 2) };
        refresh.Click += async (_, _) => await _LoadAsync(forceRefresh: true);

        var header = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(_status);

        var viewAll = new Button
        {
            Content = "View all open PRs",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "Accent" },
        };
        viewAll.Click += (_, _) => _ = _host.ShowDialogAsync(
            "GitHub Pull Requests",
            () => new GitHubPullRequestsDialogControl(_settings, _host.Actions),
            1040,
            700);

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 6,
            Children = { header, _list, viewAll },
        };

        // Re-fetch with the just-saved settings (owner/repo, token, gh-CLI toggle) instead of leaving this
        // already-built section showing data loaded under the old configuration until an app restart (#52).
        host.OnSettingsSaved(() => _ = _LoadAsync(forceRefresh: true));

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
        if (!quiet)
        {
            _status.Text = "Loading…";
        }

        try
        {
            IReadOnlyList<GitHubPullRequest> all;
            if (_settings.UseGitHubCli)
            {
                all = await _gh.SearchOpenPullRequestsAsync(_settings.GhOwner, assignedToMe: false, forceRefresh, CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_settings.Owner) || string.IsNullOrWhiteSpace(_settings.Repo))
                {
                    _status.Text = "Set a repository in settings, or turn on the GitHub CLI.";
                    return;
                }

                all = await _http.GetOpenPullRequestsAsync(_settings.Owner, _settings.Repo, _settings.Token, assignedToMe: false, CancellationToken.None);
            }

            _list.Children.Clear();
            foreach (var pullRequest in all.Take(MaxItems))
            {
                _list.Children.Add(_BuildRow(pullRequest));
            }

            _status.Text = all.Count switch
            {
                0 => "No open pull requests.",
                _ => $"{Math.Min(all.Count, MaxItems)} of {all.Count} open PR(s) — click to add to the prompt.",
            };
        }
        catch (Exception exception)
        {
            // A quiet background poll that fails keeps the last good list rather than flashing an error; an
            // explicit (manual/settings) load surfaces it.
            if (!quiet)
            {
                _status.Text = $"Could not load pull requests: {exception.Message}";
            }
        }
    }

    private Button _BuildRow(GitHubPullRequest pullRequest)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 4),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"#{pullRequest.Number} {pullRequest.Title}", FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = pullRequest.Repository, FontSize = 10, Opacity = 0.6 },
                },
            },
        };
        button.Click += async (_, _) => await _InjectAsync(pullRequest);

        // Right-click menu: the normal left-click action (add the review prompt), plus opening the PR in the
        // browser — the two things you most want to do with a PR from here.
        var addToPrompt = new MenuItem { Header = "Add to prompt" };
        addToPrompt.Click += async (_, _) => await _InjectAsync(pullRequest);
        var openInBrowser = new MenuItem { Header = "Open in browser" };
        openInBrowser.Click += (_, _) => _OpenInBrowser(pullRequest.Url);
        button.ContextMenu = new ContextMenu { ItemsSource = new[] { addToPrompt, openInBrowser } };

        return button;
    }

    private void _OpenInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            _status.Text = $"Opened PR in the browser.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not open the browser: {exception.Message}";
        }
    }

    private async Task _InjectAsync(GitHubPullRequest pullRequest)
    {
        var parts = pullRequest.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : _settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : _settings.Repo;
        var prompt = PromptTemplate.Render(_settings.Template, pullRequest, owner, repo);

        if (_host.Actions.HasActiveSession)
        {
            await _host.Actions.InjectIntoActiveSessionAsync(prompt);
            _status.Text = $"✓ Added PR #{pullRequest.Number} to the active session's prompt.";
        }
        else
        {
            await _host.Actions.SetClipboardTextAsync(prompt);
            _status.Text = $"✓ No active session — copied PR #{pullRequest.Number}'s prompt to the clipboard.";
        }
    }
}
