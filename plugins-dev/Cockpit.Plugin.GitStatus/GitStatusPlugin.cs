using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Git status (#1): a left-menu button opening a dialog that shows the branch / uncommitted / unpushed /
/// ahead-behind status of your configured repositories, and drops a status summary into the active session
/// on click. The repository list is managed from the plugin manager's gear (settings) and persisted in the
/// host's per-plugin storage, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class GitStatusPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "git-status",
        DisplayName: "Git status",
        Version: "1.4.2",
        Author: "Cockpit",
        Description: "An inline panel that follows the active session — the branch / uncommitted / unpushed status of the repo it is working in, refreshing when the session switches or runs a git command — plus a button/dialog for the same across all your configured repositories. Click to drop a status summary into the session.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new GitStatusSettings(host.Storage);
        host.AddSettings(() => new GitStatusSettingsControl(settings));
        // In each session's own header rather than in the sidebar: the git state describes the repo that one
        // session works in, and a sidebar section following "whichever session is selected" says nothing about
        // the other panes on screen.
        host.AddSessionHeaderItem(session => new GitStatusHeaderControl(host, session));

        // What a flow can do with git (#69): cut a branch, commit, push. Nothing that can throw away work — no force,
        // no reset, no deleting branches.
        foreach (var step in GitWorkflowSteps.All())
        {
            host.AddWorkflowStep(step);
        }
        host.AddSideMenuButton(
            "Git status",
            () => _ = host.ShowDialogAsync("Git status", () => new GitStatusDialogControl(settings, host.Actions), 780, 540));
    }

    public void Dispose()
    {
    }
}
