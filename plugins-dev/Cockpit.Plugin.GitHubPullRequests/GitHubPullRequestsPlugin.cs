using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests;

// Plugin #41, mirroring the GitHub Issues plugin (#14) for pull requests: it registers a settings view
// (opened from the plugin manager's gear — GitHub CLI vs single-repo, and the editable prompt template)
// and a left-menu launcher button carrying a live "N / M" badge (AC-517 — your own open PR count next to
// how many are waiting on your review), opening a dialog with every open PR. Clicking a pull request in
// the dialog injects the rendered template into the active session so the agent opens and reviews it,
// falling back to the clipboard when there is no active session. Its settings live in the host's
// per-plugin storage, so `ConfigureServices` is empty.
//
// AC-517 replaced this plugin's other half — an inline side-menu section always visible under the session
// list, showing up to a configurable number of pull requests inline. The dialog and its actions are
// unchanged; the always-visible list is now the Dashboard widget below, for a workspace given over to it.
public sealed class GitHubPullRequestsPlugin : ICockpitPlugin
{
    private MergedPullRequestWatcher? _merged;
    private PullRequestRefreshSource? _refreshSource;
    private PullRequestBadgeUpdater? _badgeUpdater;

    public PluginMetadata Metadata { get; } = new(
        Id: "github-pull-requests",
        DisplayName: "GitHub Pull Requests",
        Author: "Cockpit",
        Description: "Shows how many open GitHub pull requests are yours in the left menu — a button with a live \"N / M\" badge, your own open PR count next to how many are waiting on your review — refreshing both on a timer and the instant a session opens/merges/closes a PR (it watches session output for a pull url or a merged/closed line), via the gh CLI — the PRs you opened across all your repos, including org repos, or a single repo over HTTP. Clicking it opens a dialog listing every open PR in a searchable, sortable grid with an \"Assigned to me\" filter, plus a Dashboard widget showing the same list as a resizable pane with its own item count; left-click a PR to drop a review prompt, or right-click for a menu (add to prompt / open in browser). A pull request that starts waiting for your review raises a toast with an \"Open in browser\" button. The prompt template is editable in settings. Also offers a get_pr_status MCP tool so agent sessions and the assistant can ask for one PR's checks/mergeable/reviews/title without polling GitHub themselves.");

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

        // AC-802: the PR/CI status banner under a session's transcript — a per-checkout `gh pr view` (unrelated to
        // the refresh source below, which is the cross-repo "your open PRs" list this plugin's badge/dialog/widget
        // already share).
        host.AddSessionBanner(session => new SessionPullRequestBannerControl(session));

        var settings = new GitHubPullRequestsSettings(host.Storage);

        // AC-818: get_pr_status over MCP — checks/mergeable/reviews/title for one PR, cached briefly so several
        // sessions waiting on the same PR share one `gh` call. Reuses this plugin's own gh-CLI client, not a
        // second GitHub client.

        // AC-869: internal — the host auto-mounts it per git-repo session or the assistant, hidden otherwise.
        _ = host.AddMcpEndpoint("cockpit-github-pull-requests", new GitHubPullRequestsMcpTools(new GitHubPrGhClient()), isEnabled: () => settings.McpEnabled, isInternal: true);

        // One refresh source per plugin instance (AC-515): it polls in the background regardless of which of the
        // views below is on screen, and every one of them subscribes to it rather than fetching for itself — every
        // dashboard widget instance, and the side-menu badge (AC-517).
        _refreshSource = new PullRequestRefreshSource(host, settings);

        host.AddSettings(() => new GitHubPullRequestsSettingsControl(settings));

        // Replaces the old always-visible AddSideMenuSection (AC-517): a launcher button with a live badge,
        // opening the same dialog the section's "View all" and the widget's "View all" already shared.
        _badgeUpdater = new PullRequestBadgeUpdater(host, settings, _refreshSource);

        // The same list as a Dashboard pane (#AC-18): the badge above shows only a count, this is for a workspace
        // given over to seeing the list itself. The lambda closes over `host` so the widget can inject prompts and
        // open the dialog, and is handed each instance's own IWidgetContext for its per-pane count. The id keeps a
        // "widgets." prefix and is persisted with every placed instance, so it is an API surface — changing it would
        // orphan widgets on dashboards people have already arranged.
        host.AddWidget(new WidgetRegistration("widgets.github-pull-requests", "GitHub Pull Requests", context => new GitHubPullRequestsWidget(settings, host, context, _refreshSource))
        {
            IconKind = MaterialIconKind.SourcePull,
            Description = "Your open pull requests, with a configurable count.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 8,
            CreateConfigView = context => new GitHubPullRequestsWidgetSettingsView(context),
        });
    }

    public void Dispose()
    {
        _merged?.Dispose();
        _badgeUpdater?.Dispose();
        _refreshSource?.Dispose();
    }
}
