using System.Text.Json;
using Material.Icons;
using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubPullRequests.UI;

// The UI part of GitHub Pull Requests (AC-1396): the settings view, the side-menu badge and its dialog, the
// session banner, the dashboard widget and the dock-rail panel. The backend part, GitHubPullRequestsPlugin, keeps
// everything that talks to GitHub and the badge's counting, and answers this part over the plugin's channel.
public sealed class GitHubPullRequestsUi : ICockpitPluginUi, IDisposable
{
    private PullRequestBadge? _badge;

    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new GitHubPullRequestsSettings(host.Storage);

        // AC-802: the PR/CI status banner under a session's transcript — a per-checkout `gh pr view`, unrelated to
        // the shared "your open PRs" feed the badge, dialog and widget read.
        host.AddSessionBanner(session => new SessionPullRequestBannerControl(host, session));

        host.AddSettings(() => new GitHubPullRequestsSettingsControl(host, settings));

        // A settings change (owner, watched repos, the CLI toggle) can change what the next fetch should even ask
        // for — one forced refresh of the shared feed, rather than every view repeating it for itself.
        host.OnSettingsSaved(() => _RefreshAfterSave(host));

        // Replaces the old always-visible AddSideMenuSection (AC-517): a launcher button with a live badge.
        _badge = new PullRequestBadge(host, settings);

        // The same list as a Dashboard pane (#AC-18): the badge above shows only a count, this is for a workspace
        // given over to seeing the list itself. The id keeps a "widgets." prefix and is persisted with every placed
        // instance, so it is an API surface — changing it would orphan widgets on dashboards people have arranged.
        host.AddWidget(new WidgetRegistration("widgets.github-pull-requests", "GitHub Pull Requests", context => new GitHubPullRequestsWidget(settings, host, context))
        {
            IconKind = MaterialIconKind.SourcePull,
            Description = "Your open pull requests, with a configurable count.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 8,
            CreateConfigView = context => new GitHubPullRequestsWidgetSettingsView(context),
        });

        // AC-960: the same list, reachable as a dock-rail panel too, next to the badge and its dialog.
        PullRequestDockPanelRegistrar.Register(host, settings);
    }

    public void Dispose() => _badge?.Dispose();

    private static async void _RefreshAfterSave(ICockpitUiHost host)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new PullRequestRefreshRequest(ForceRefresh: true), GitHubPullRequestsChannel.Json);
            await host.Channel.InvokeAsync(GitHubPullRequestsChannel.Refresh, payload);
        }
        catch (Exception)
        {
            // The feed keeps its own failure as LastError, which every view shows on the FeedUpdated that follows.
        }
    }
}
