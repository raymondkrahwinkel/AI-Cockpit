using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubActions;

/// <summary>
/// GitHub Actions CI status (AC-52): adds an indicator to each session's header showing the latest workflow-run status
/// of the branch that session is working in — green pass, red fail, amber running — click to open the run on GitHub.
/// Completes the GitHub set (Issues + Pull Requests + Git status → + CI). Uses the machine's existing <c>gh</c> login;
/// no local state, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class GitHubActionsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "github-actions",
        DisplayName: "GitHub Actions",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Shows the GitHub Actions status of the branch a session is working in, in that session's header: "
            + "a coloured icon (green pass, red fail, amber running) for the latest workflow run on the current branch, "
            + "with the workflow/event/time on hover — click to open the run on GitHub. Requires the gh CLI installed and "
            + "authenticated on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — the header indicator reads gh on demand per session.
    }

    public void Initialize(ICockpitHost host)
    {
        // In each session's own header rather than the sidebar: CI status describes the branch that one session is on,
        // the same reasoning the git-status badge follows.
        host.AddSessionHeaderItem(session => new CiStatusHeaderControl(session));
    }

    public void Dispose()
    {
    }
}
