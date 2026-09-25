using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

// Plugin #41, mirroring the GitHub Issues plugin (#14) for pull requests: a left-menu launcher button carrying a
// live "N / M" badge (AC-517 — your own open PR count next to how many are waiting on your review), opening a
// dialog with every open PR, plus a Dashboard widget, a dock-rail panel and a session banner. Its settings live in
// the host's per-plugin storage, so `ConfigureServices` is empty.
//
// AC-1396: the backend part. It keeps everything that talks to GitHub — the shared refresh source, the dialog's
// query, the session banner's `gh pr view`, the merge watcher, the MCP tool — and the badge's counting, and answers
// the UI part (GitHubPullRequestsUi) over the plugin's channel. Everything with a window moved there.
public sealed class GitHubPullRequestsPlugin : ICockpitPlugin
{
    private readonly List<IDisposable> _handlers = [];
    private readonly PullRequestFeed _feed = new();
    private readonly SessionPullRequestStatusClient _sessionClient = new();

    private MergedPullRequestWatcher? _merged;
    private PullRequestRefreshSource? _refreshSource;
    private PullRequestBadgeUpdater? _badgeUpdater;

    public PluginMetadata Metadata { get; } = new(
        Id: "github-pull-requests",
        DisplayName: "GitHub Pull Requests",
        Author: "Cockpit",
        Description: "Shows how many open GitHub pull requests are yours in the left menu — a button with a live \"N / M\" badge, your own open PR count next to how many are waiting on your review — refreshing both on a timer and the instant a session opens/merges/closes a PR (it watches session output for a pull url or a merged/closed line), via the gh CLI — the PRs you opened across all your repos, including org repos, or a single repo over HTTP. Clicking it opens a dialog listing every open PR in a searchable, sortable grid with an \"Assigned to me\" filter, plus a Dashboard widget and a dock-rail panel each showing the same list as a resizable pane with its own item count; left-click a PR to drop a review prompt, or right-click for a menu (add to prompt / open in browser). A pull request that starts waiting for your review raises a toast with an \"Open in browser\" button. The prompt template is editable in settings. Also offers a get_pr_status MCP tool so agent sessions and the assistant can ask for one PR's checks/mergeable/reviews/title without polling GitHub themselves.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // Opening a pull request from a flow, and the trigger for one being merged (#69) — the two ends of the day the
        // git steps describe.
        foreach (var step in PullRequestWorkflowSteps.All(host))
        {
            host.AddWorkflowStep(step);
        }

        _merged = new MergedPullRequestWatcher(host);

        var settings = new GitHubPullRequestsSettings(host.Storage);
        host.Storage.Remove("refreshSourceSnapshot");
        host.Storage.Remove("cachedPullRequests");

        // AC-818: get_pr_status over MCP — checks/mergeable/reviews/title for one PR, cached briefly so several
        // sessions waiting on the same PR share one `gh` call. Reuses this plugin's own gh-CLI client, not a
        // second GitHub client.

        // AC-869: internal — the host auto-mounts it per git-repo session or the assistant, hidden otherwise.
        _ = host.AddMcpEndpoint("cockpit-github-pull-requests", new GitHubPullRequestsMcpTools(new GitHubPrGhClient()), isEnabled: () => settings.McpEnabled, isInternal: true);

        // One refresh source per plugin instance (AC-515): it polls in the background regardless of which view is on
        // screen, and every one of them reads it rather than fetching for itself — now over the channel (AC-1396).
        var refreshSource = new PullRequestRefreshSource(host, settings);
        _refreshSource = refreshSource;
        refreshSource.Updated += (_, snapshot) => host.Channel.Publish(
            GitHubPullRequestsChannel.FeedUpdated,
            _Serialize(new PullRequestFeedState(snapshot, refreshSource.LastError?.Message)));

        var badgeUpdater = new PullRequestBadgeUpdater(host.Channel, host.Sessions, settings, refreshSource);
        _badgeUpdater = badgeUpdater;

        _handlers.Add(host.Channel.Handle(GitHubPullRequestsChannel.Feed, (_, _) =>
            Task.FromResult(_Serialize(new PullRequestFeedState(refreshSource.Current, refreshSource.LastError?.Message)))));
        _handlers.Add(host.Channel.Handle(GitHubPullRequestsChannel.Refresh, async (payload, _) =>
        {
            var request = _Deserialize<PullRequestRefreshRequest>(payload);
            var ran = await refreshSource.RefreshAsync(request.ForceRefresh);
            return _Serialize(new PullRequestRefreshAnswer(ran, ran ? refreshSource.LastError?.Message : null));
        }));
        _handlers.Add(host.Channel.Handle(GitHubPullRequestsChannel.OpenPullRequests, async (payload, cancellationToken) =>
        {
            var request = _Deserialize<OpenPullRequestsRequest>(payload);
            return _Serialize(await _feed.LoadOpenAsync(settings, request.AssignedToMe, request.ForceRefresh, cancellationToken));
        }));
        _handlers.Add(host.Channel.Handle(GitHubPullRequestsChannel.SessionPullRequest, async (payload, cancellationToken) =>
        {
            var request = _Deserialize<SessionPullRequestRequest>(payload);
            return _Serialize(await _sessionClient.GetOpenPullRequestAsync(request.WorkingDirectory, cancellationToken));
        }));
        _handlers.Add(host.Channel.Handle(GitHubPullRequestsChannel.BadgeCounts, (_, _) =>
            Task.FromResult(_Serialize(badgeUpdater.Counts))));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
        _merged?.Dispose();
        _badgeUpdater?.Dispose();
        _refreshSource?.Dispose();
    }

    private static JsonElement _Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, GitHubPullRequestsChannel.Json);

    private static T _Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(GitHubPullRequestsChannel.Json)
            ?? throw new ArgumentException($"The request carries no {typeof(T).Name}.", nameof(payload));
}
