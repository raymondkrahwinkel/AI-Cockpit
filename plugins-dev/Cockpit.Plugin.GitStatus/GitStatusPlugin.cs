using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

// Git status (#1): a per-session header indicator (`GitStatusHeaderControl`) showing the branch /
// uncommitted / unpushed / ahead-behind status of the repo that session is working in, and dropping a status
// summary into that session on click. Everything it needs lives in the host's services already, so
// `ConfigureServices` is empty.
//
// AC-522 removed the plugin's other half — a left-menu button opening a dialog over a manually configured
// repository list, for watching a repo with no session open. Raymond judged that overbuilt for what the
// per-session indicator already covers; the workflow steps below are unrelated and stayed.
public sealed class GitStatusPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "git-status",
        DisplayName: "Git status",
        Author: "Cockpit",
        Description: "An inline panel that follows the active session — the branch / uncommitted / unpushed status of the repo it is working in, refreshing when the session switches or runs a git command. Click to drop the status summary into the session.");

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
        host.AddSessionHeaderItem(session => new GitStatusHeaderControl(host, session, settings));

        // What a flow can do with git (#69): cut a branch, commit, push. Nothing that can throw away work — no force,
        // no reset, no deleting branches.
        foreach (var step in GitWorkflowSteps.All())
        {
            host.AddWorkflowStep(step);
        }
    }

    public void Dispose()
    {
    }
}
