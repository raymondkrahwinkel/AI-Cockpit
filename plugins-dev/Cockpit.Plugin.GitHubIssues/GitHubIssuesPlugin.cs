using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Example plugin (#14) proving the contract end-to-end: it contributes an Options tab (repository, token,
/// editable prompt template) and a left-menu section listing the repo's open issues, where clicking an
/// issue injects the rendered template into the active session so the agent opens and reviews it. It needs
/// no services of its own — its settings live in the host's per-plugin storage — so
/// <see cref="ConfigureServices"/> is empty and everything is wired in <see cref="Initialize"/>.
/// </summary>
public sealed class GitHubIssuesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-issues",
        DisplayName: "GitHub Issues",
        Version: "1.0.0",
        Author: "Cockpit",
        Description: "Shows a repository's open issues in the left menu; clicking one asks the agent to open and review it. The prompt template is editable in Options.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitHubIssuesSettings(host.Storage);
        var client = new GitHubIssuesClient();

        host.AddOptionsTab("GitHub Issues", () => new GitHubIssuesOptionsControl(settings));
        host.AddSideMenuSection("GitHub Issues", () => new GitHubIssuesPanelControl(settings, client, host.Actions));
    }

    public void Dispose()
    {
    }
}
