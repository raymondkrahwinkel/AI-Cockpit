using System.Text.Json;
using Avalonia.Threading;
using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using static Cockpit.Plugin.Autopilot.AutopilotChannelContract;

namespace Cockpit.Plugin.Autopilot;

// AC-1398: the UI part — the plan-flow workspace, which places the sessions the backend holds by pane id. AC-1418:
// it reaches the backend's runs, plan, queue, history and templates over the plugin's channel only.
public sealed class AutopilotUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The settings are loose keys in the plugin's storage, the same slice the backend part reads.
        var settings = new AutopilotSettings(host.Storage);
        var client = new AutopilotChannelClient(host.Channel);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so
        // the CEO-profile picker can list profiles/models, and the channel for the Templates section.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host, client));

        // Open the Autopilot workspace from the side menu — it does not force a planning round. The operator
        // starts a run with New run (where the CEO-profile guard now lives), so history stays reachable without
        // a profile set. A triggered run still opens straight into planning via the backend's "plan" intent.
        host.AddSideMenuButton("Autopilot", () => _ = host.OpenWorkspaceAsync(PlanWorkspaceId));

        // The backend has no selected session; the directory of the one this window has selected is what its epic
        // merge check runs git in, so it is told on every change.
        void ReportActiveDirectory() => _ = host.Channel.InvokeAsync(ActiveDirectory, JsonSerializer.SerializeToElement(host.ActiveSessionWorkingDirectory ?? string.Empty));
        host.ActiveSessionChanged += (_, _) => ReportActiveDirectory();
        ReportActiveDirectory();

        // What the backend part wants shown arrives as events, since it has no window of its own.
        host.Channel.Subscribe(OpenPlan, evt => host.OpenWorkspaceAsync(PlanWorkspaceId));
        host.Channel.Subscribe(OpenSettings, evt => Dispatcher.UIThread.Post(() => _ = host.ShowSettingsAsync()));

        // The CEO plan-flow surface (AC-174/AC-175): the pipeline as blocks with, later, the running step's session.
        host.AddWorkspaceType(new WorkspaceTypeRegistration(PlanWorkspaceId, "Autopilot", context => new AutopilotPlanWorkspaceBody(host, context, settings, client))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });
    }
}
