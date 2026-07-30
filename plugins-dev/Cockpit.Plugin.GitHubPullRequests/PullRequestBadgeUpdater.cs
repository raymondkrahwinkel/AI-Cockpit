using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The side-menu launcher this plugin registers instead of <c>AddSideMenuSection</c> (AC-517): a knop with a live
/// "N / M" badge (AC-516) that opens <see cref="GitHubPullRequestsDialogControl"/> on click, reusing the section's
/// old "pull-requests" <c>singleInstanceKey</c> so a second click refocuses rather than stacking a second window.
/// Built once in <see cref="GitHubPullRequestsPlugin.Initialize"/> and disposed with the plugin — unlike the
/// section it replaces, nothing here depends on a control being attached to the visual tree, so the badge, the
/// instant-on-signal refresh, and the "review requested" toast all keep working while the left menu shows
/// something else entirely.
/// </summary>
internal sealed class PullRequestBadgeUpdater : IDisposable
{
    // On top of the shared PullRequestRefreshSource's own background poll, a short debounce coalesces the burst
    // of lines a single `gh pr create` prints into one refresh — moved verbatim from the old side section/widget.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(3);

    private readonly ICockpitHost _host;
    private readonly GitHubPullRequestsSettings _settings;
    private readonly PullRequestRefreshSource _refreshSource;
    private readonly DispatcherTimer _signalRefresh;

    // Deliberately object, not SideMenuButtonBadge: that type and AddSideMenuButtonWithBadge are both AC-516
    // additions to Cockpit.Plugins.Abstractions, and what the CLR resolves at runtime is the HOST's own copy of
    // that assembly — not whatever this plugin was compiled against. A field (or any unguarded local) typed
    // SideMenuButtonBadge forces the JIT to resolve that type the moment ANY method touching it is compiled,
    // including this class's own constructor — on an older host that throws TypeLoadException before ever
    // reaching a try/catch, the same failure the minHostVersion gate exists to prevent but cannot be trusted alone
    // to catch. Keeping the field untyped and touching the real type only inside dedicated, NoInlining-marked
    // methods — each called from inside its own try — is what actually isolates the failure to a catchable call.
    private object? _badge;

    public PullRequestBadgeUpdater(ICockpitHost host, GitHubPullRequestsSettings settings, PullRequestRefreshSource refreshSource)
    {
        _host = host;
        _settings = settings;
        _refreshSource = refreshSource;

        try
        {
            _badge = _RegisterBadge(host, settings);
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException)
        {
            // An older host's copy of Cockpit.Plugins.Abstractions has neither AddSideMenuButtonWithBadge nor the
            // SideMenuButtonBadge type it returns. minHostVersion is supposed to keep this plugin from loading
            // there at all, but that gate lives in the host's own loader, not here — if it is ever bypassed, this
            // is what stands between one missing member and the whole plugin (settings, widget, merged-PR watcher)
            // disappearing with it, the way AC-500's ABI break could have.
            _badge = null;
        }

        _refreshSource.Updated += _OnUpdated;
        _host.Sessions.OutputProduced += _OnSessionOutput;

        _signalRefresh = new DispatcherTimer { Interval = SignalDebounce };
        _signalRefresh.Tick += (_, _) =>
        {
            _signalRefresh.Stop();
            _ = _refreshSource.RefreshAsync(forceRefresh: true);
        };

        _Apply(_refreshSource.Current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object _RegisterBadge(ICockpitHost host, GitHubPullRequestsSettings settings) =>
        host.AddSideMenuButtonWithBadge("Open PRs", () => _ = host.ShowDialogAsync(
            "GitHub Pull Requests",
            () => new GitHubPullRequestsDialogControl(settings, host),
            "pull-requests",
            width: 1040,
            height: 700));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _SetCounts(object badge, int? primary, int? secondary)
    {
        var typed = (SideMenuButtonBadge)badge;
        typed.Primary = primary;
        typed.Secondary = secondary;
    }

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        if (PullRequestSignalDetector.ContainsSignal(output.Text))
        {
            _signalRefresh.Stop();
            _signalRefresh.Start();
        }
    }

    private void _OnUpdated(object? sender, PullRequestFeedSnapshot snapshot) => _Apply(snapshot);

    /// <summary>
    /// Sets the badge's counters and announces newly-arrived review requests. Badge updates need no thread
    /// marshalling — <see cref="SideMenuButtonBadge"/> is built for a background-thread writer, the host marshals
    /// itself on <see cref="SideMenuButtonBadge.Changed"/> — but <see cref="ICockpitHost.ShowToast"/> is not, and
    /// the section this replaces only ever called it from a UI-thread <c>Dispatcher.UIThread.Post</c> continuation.
    /// </summary>
    private void _Apply(PullRequestFeedSnapshot snapshot)
    {
        var result = snapshot.Result;

        if (_badge is { } badge)
        {
            try
            {
                if (snapshot.FetchedAt is null || result.RepositoryMissing)
                {
                    // Nothing has loaded yet (ever), or nothing is configured to load — both are "not yet known",
                    // never a guessed zero.
                    _SetCounts(badge, primary: null, secondary: null);
                }
                else
                {
                    var (primary, secondary) = PullRequestBadgeCounts.Compute(result, _settings.IgnoredPullRequests, _settings.IgnoredRepositories);
                    _SetCounts(badge, primary, secondary);
                }
            }
            catch (Exception exception) when (exception is MissingMethodException or MissingMemberException or TypeLoadException)
            {
                // The registration call above succeeded (or this host was never actually old to begin with in a
                // test double), but a later touch of the type still cannot resolve it — treat it the same as a
                // failed registration rather than let a snapshot update take the rest of the plugin down.
                _badge = null;
            }
        }

        // Never against snapshot.FetchedAt is null: the constructor's own priming call (_Apply(_refreshSource.Current))
        // runs before the refresh source's first real fetch has ever landed, since the badge — unlike the section it
        // replaces — exists from the moment the plugin loads rather than only once the UI renders it. Announcing
        // against that placeholder Empty snapshot would prime the seen-set on nothing, so the actual first fetch's
        // review requests (which really are pre-existing, not new) would then all read as "just arrived".
        if (_settings.UseGitHubCli && snapshot.FetchedAt is not null)
        {
            Dispatcher.UIThread.Post(() => _AnnounceArrivals(result.ReviewRequested));
        }
    }

    /// <summary>
    /// A review request that was already waiting when the plugin first looked is not news, so the first load only
    /// primes the seen-set (it has no stored one yet) and stays quiet. After that, every request that was not there
    /// last time is announced once. Moved verbatim from the old side section (AC-517) — the persisted
    /// <see cref="GitHubPullRequestsSettings.SeenReviewRequests"/> gate is what keeps this correct across restarts
    /// and across the section's removal, not anything about being attached to a view.
    /// </summary>
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
                () => PullRequestActions.OpenInBrowser(_host, pullRequest.Url));
        }
    }

    public void Dispose()
    {
        _refreshSource.Updated -= _OnUpdated;
        _host.Sessions.OutputProduced -= _OnSessionOutput;
        _signalRefresh.Stop();
    }
}
