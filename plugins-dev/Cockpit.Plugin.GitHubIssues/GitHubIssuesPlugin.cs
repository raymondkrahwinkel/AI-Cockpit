using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Example plugin (#14) proving the contract end-to-end: it registers a settings view (opened from the
/// plugin manager's gear — GitHub CLI vs single-repo, and the editable prompt template) and a left-menu
/// button that opens a dialog listing open issues (across all your repos via <c>gh</c>, or one repo over
/// HTTP), where selecting one injects the rendered template into the active session so the agent opens and
/// reviews it. Its settings live in the host's per-plugin storage, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class GitHubIssuesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-issues",
        DisplayName: "GitHub Issues",
        Version: "1.2.0",
        Author: "Cockpit",
        Description: "Browse open GitHub issues across your repos (via the gh CLI) or one repo, with an \"Assigned to me\" filter, and drop a prompt asking the agent to open and review one. The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubIssuesSettings(host.Storage);

        host.AddSettings(() => new GitHubIssuesSettingsControl(settings));
        host.AddSideMenuButton(
            "GitHub Issues",
            () => _ = host.ShowDialogAsync("GitHub Issues", () => new GitHubIssuesDialogControl(settings, host.Actions), 1040, 700));
    }

    public void Dispose()
    {
    }
}
