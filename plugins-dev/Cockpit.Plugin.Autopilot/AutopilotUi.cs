using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// AC-1398: the UI part — the plan-flow workspace, which places the sessions the backend holds by pane id. It still
// takes the backend's objects in-process (AutopilotPlugin.Parts); AC-1418 moves it to UI/ and onto the channel.
public sealed class AutopilotUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var parts = AutopilotPlugin.Parts ?? throw new InvalidOperationException("InitializeUi ran before Initialize.");

        // The CEO plan-flow surface (AC-174/AC-175): the pipeline as blocks with, later, the running step's session.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot.plan", "Autopilot", context => new AutopilotPlanWorkspaceBody(parts.Host, host, context, parts.Settings, parts.Plan, parts.Manager, parts.Queue, parts.History, parts.Templates))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });
    }
}

// What the backend part built that the UI part's workspace still reads in-process until AC-1418.
internal sealed record AutopilotParts(
    ICockpitHost Host,
    AutopilotSettings Settings,
    AutopilotPlanController Plan,
    AutopilotRunManager Manager,
    AutopilotRunQueue Queue,
    AutopilotRunHistory History,
    AutopilotTemplateStore Templates);
