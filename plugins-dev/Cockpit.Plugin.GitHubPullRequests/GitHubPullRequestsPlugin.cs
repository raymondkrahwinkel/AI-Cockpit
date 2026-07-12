using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Plugin #41, mirroring the GitHub Issues plugin (#14) for pull requests: it registers a settings view
/// (opened from the plugin manager's gear — GitHub CLI vs single-repo, and the editable prompt template)
/// and an inline side-menu section, always visible under the session list, showing up to 5 open pull
/// requests (across all your repos via <c>gh</c>, or one repo over HTTP) plus a button opening a dialog
/// with every open PR. Clicking a pull request — in the section or the dialog — injects the rendered
/// template into the active session so the agent opens and reviews it, falling back to the clipboard when
/// there is no active session. Its settings live in the host's per-plugin storage, so
/// <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class GitHubPullRequestsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-pull-requests",
        DisplayName: "GitHub Pull Requests",
        Version: "1.5.0",
        Author: "Cockpit",
        Description: "Shows up to 5 of your open GitHub pull requests inline under the session list, refreshing both on a timer and the instant a session opens/merges/closes a PR (it watches session output for a pull url or a merged/closed line), via the gh CLI — the PRs you opened across all your repos, including org repos, or a single repo over HTTP — plus a dialog with an \"Assigned to me\" filter. Left-click a PR to drop a review prompt, or right-click for a menu (add to prompt / open in browser). The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubPullRequestsSettings(host.Storage);

        host.AddSettings(() => new GitHubPullRequestsSettingsControl(settings));
        host.AddSideMenuSection("Open PRs", () => new GitHubPullRequestsSideSectionControl(settings, host));
    }

    public void Dispose()
    {
    }
}
