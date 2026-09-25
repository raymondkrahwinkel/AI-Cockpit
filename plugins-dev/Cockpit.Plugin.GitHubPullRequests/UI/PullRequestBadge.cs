using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubPullRequests.UI;

// The side-menu launcher (AC-517) with its live "N / M" badge (AC-516). AC-1396: the window half of what
// PullRequestBadgeUpdater used to be — the backend counts and picks the new review requests, and this shows its
// BadgeChanged event: the counts on the badge, a toast per arrival.
internal sealed class PullRequestBadge : IDisposable
{
    private readonly ICockpitUiHost _host;
    private readonly GitHubPullRequestsSettings _settings;
    private readonly SideMenuButtonBadge _badge;
    private readonly IDisposable _badgeChanged;

    // Guards `_events` with the badge write, so the startup ask cannot overwrite a newer event's counts with the
    // older answer it was waiting on. Events arrive on the backend's publishing thread, the answer on this one.
    private readonly Lock _gate = new();
    private long _events;

    // UI thread only (_Announce runs there): the startup answer can carry an arrival an event also delivered.
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);

    public PullRequestBadge(ICockpitUiHost host, GitHubPullRequestsSettings settings)
    {
        _host = host;
        _settings = settings;
        _badge = host.AddSideMenuButtonWithBadge("Open PRs", _OpenDialog);
        _badgeChanged = host.Channel.Subscribe(GitHubPullRequestsChannel.BadgeChanged, _OnBadgeChanged);
        _ShowCurrent();
    }

    public void Dispose() => _badgeChanged.Dispose();

    private void _OnBadgeChanged(PluginChannelEvent channelEvent)
    {
        if (channelEvent.Payload.Deserialize<PullRequestBadgeState>(GitHubPullRequestsChannel.Json) is not { } state)
        {
            return;
        }

        lock (_gate)
        {
            _events++;
            _SetCounts(state);
        }

        // The badge takes a background writer (the host marshals its Changed itself); a toast does not.
        if (state.Arrived.Count > 0)
        {
            Dispatcher.UIThread.Post(() => _Announce(state.Arrived));
        }
    }

    // What the backend counted before this part subscribed, and the arrivals it could not announce to anybody yet.
    private async void _ShowCurrent()
    {
        try
        {
            long before;
            lock (_gate)
            {
                before = _events;
            }

            var answer = await _host.Channel.InvokeAsync(GitHubPullRequestsChannel.BadgeCounts, GitHubPullRequestsChannel.NoPayload);
            if (answer.Deserialize<PullRequestBadgeState>(GitHubPullRequestsChannel.Json) is not { } state)
            {
                return;
            }

            lock (_gate)
            {
                if (_events == before)
                {
                    _SetCounts(state);
                }
            }

            if (state.Arrived.Count > 0)
            {
                Dispatcher.UIThread.Post(() => _Announce(state.Arrived));
            }
        }
        catch (Exception)
        {
            // A badge left "not yet known" until the next event is the honest fallback; nothing to report.
        }
    }

    private void _SetCounts(PullRequestBadgeState state)
    {
        _badge.Primary = state.Mine;
        _badge.Secondary = state.ReviewRequested;
    }

    private void _Announce(IReadOnlyList<GitHubPullRequest> arrived)
    {
        foreach (var pullRequest in arrived.Where(pullRequest => _announced.Add(pullRequest.Url)))
        {
            _host.ShowToast(
                $"Review requested — #{pullRequest.Number} {pullRequest.Title} ({pullRequest.Repository})",
                PluginToastSeverity.Information,
                "Open in browser",
                () => PullRequestActions.OpenInBrowser(_host, pullRequest.Url));
        }
    }

    // The "pull-requests" key is the one the widget's "View all" shares, so a second click refocuses that window.
    private async void _OpenDialog()
    {
        try
        {
            await _host.ShowDialogAsync(
                "GitHub Pull Requests",
                () => new GitHubPullRequestsDialogControl(_settings, _host),
                "pull-requests",
                width: 1040,
                height: 700);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not open the pull requests: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}
