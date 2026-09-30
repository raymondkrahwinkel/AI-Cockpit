using Avalonia.Threading;
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

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so
        // the CEO-profile picker can list profiles/models, and the template store for the Templates section.
        host.AddSettings(() => new AutopilotSettingsControl(parts.Settings, parts.Host, host, parts.Templates));

        // Open the Autopilot workspace from the side menu — it does not force a planning round. The operator
        // starts a run with New run (where the CEO-profile guard now lives), so history stays reachable without
        // a profile set. A triggered run still opens straight into planning via the backend's "plan" intent.
        host.AddSideMenuButton("Autopilot", () => _ = host.OpenWorkspaceAsync(AutopilotChannel.PlanWorkspaceId));

        // What the backend part wants shown arrives as events, since it has no window of its own.
        host.Channel.Subscribe(AutopilotChannel.OpenPlan, evt => host.OpenWorkspaceAsync(AutopilotChannel.PlanWorkspaceId));
        host.Channel.Subscribe(AutopilotChannel.OpenSettings, evt => Dispatcher.UIThread.Post(() => _ = host.ShowSettingsAsync()));

        // The CEO plan-flow surface (AC-174/AC-175): the pipeline as blocks with, later, the running step's session.
        host.AddWorkspaceType(new WorkspaceTypeRegistration(AutopilotChannel.PlanWorkspaceId, "Autopilot", context => new AutopilotPlanWorkspaceBody(parts.Host, host, context, parts.Settings, parts.Plan, parts.Manager, parts.Queue, parts.History, parts.Templates))
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

// The events the backend part publishes on the plugin's channel for the UI part to show.
internal static class AutopilotChannel
{
    public const string PlanWorkspaceId = "workspace.autopilot.plan";

    public const string OpenPlan = "open-plan";

    public const string OpenSettings = "open-settings";
}
