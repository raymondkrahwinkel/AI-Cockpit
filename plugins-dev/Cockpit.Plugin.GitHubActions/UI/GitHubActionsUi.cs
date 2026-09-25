using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions.UI;

// The UI part of GitHub Actions (AC-1394): the session-header indicator, the dashboard widget and the dock-rail
// panel. The backend part, GitHubActionsPlugin, keeps CiWorkflowRunClient and answers this part's questions over
// the plugin's channel.
public sealed class GitHubActionsUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // In each session's own header rather than the sidebar: CI status describes the branch that one session is
        // on, the same reasoning the git-status badge follows.
        host.AddSessionHeaderItem(session => new CiStatusHeaderControl(host, session));

        // AC-1065: the same status, as a list for a workspace given over to it.
        host.AddWidget(new WidgetRegistration("widgets.github-actions", "GitHub Actions", context => new CiWorkflowRunsWidget(context, host))
        {
            IconKind = MaterialIconKind.Cog,
            Description = "The branch's recent GitHub Actions runs, with a configurable count.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 8,
            CreateConfigView = context => new CiWorkflowRunsWidgetSettingsView(context),
        });

        // AC-1065: the same list, reachable as a dock-rail panel too, next to the header dot.
        CiWorkflowRunsDockPanelRegistrar.Register(host);
    }
}
