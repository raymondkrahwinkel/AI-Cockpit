using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Autopilot (AC-94/AC-174): the operator-triggered "issue → merge-ready PR" plugin. The CEO plans the work, the
/// operator approves it once, then an autonomous run drives each step — embedding an isolated session per step,
/// validating it against its acceptance, and settling merge-ready or blocked. A tracker's "Plan in Autopilot" hands an
/// issue to the CEO with its source to draft from.
/// </summary>
public sealed class AutopilotPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "autopilot",
        DisplayName: "Autopilot",
        // In lockstep with plugin.json's version: the manifest gates loading, this shows in the plugin list.
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Operator-triggered \"issue → merge-ready PR\" pipeline: the CEO plans, you approve once, it runs autonomously.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new AutopilotSettings(host.Storage);

        // The planning controller (AC-174): the CEO's live draft during one planning round. Planning is decoupled from
        // executing — a frozen plan goes to the run queue, and runs execute on their own controllers — so the operator
        // can plan a new run while others run.
        var planController = new AutopilotPlanController();

        // The queue of approved runs (persistent) and the manager that runs them up to the concurrency cap.
        var queue = new AutopilotRunQueue(host.Storage);
        var manager = new AutopilotRunManager(queue, settings);

        // The history of settled runs (persistent, Raymond 2026-07-22): a run that finishes leaves the live surface, so
        // it is recorded here to be shown in the history section rather than vanishing.
        var history = new AutopilotRunHistory(host.Storage);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so the
        // CEO-profile picker can list the cockpit's profiles and offer each one's models.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host));

        // The CEO's plan-emit tool during the planning round (AC-174): live only while planning, and pane-scoped so only
        // the bound CEO session may set the plan. The workspace body briefs the CEO to call it; approving submits the plan.
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planController), isEnabled: () => planController.Phase == AutopilotPlanPhase.Planning);

        // The autonomous run's report channel (AC-174): a step agent signals done, a run's CEO validator reports its
        // verdict — both pane-scoped, routed by the manager to whichever run owns the caller pane. Live while any run is
        // executing; dark when none is.
        _ = host.AddMcpEndpoint(AutopilotRunTools.EndpointName, new AutopilotRunTools(host, manager), isEnabled: () => manager.Active.Count > 0);

        // The CEO-flow trigger (AC-174): a tracker's "Plan in Autopilot" hands the item to the CEO planning round with
        // its source to draft from.
        host.RegisterIntentHandler("plan", async intent =>
        {
            var run = AutopilotRun.FromIntent(intent);

            if (!_RequireCeoProfile(host, settings))
            {
                return new Dictionary<string, string> { ["status"] = "no-ceo-profile", ["issue"] = run.IssueId };
            }

            // Refused while a run is already live (BeginPlanning returns false) so a second trigger cannot overwrite it;
            // the caller is told it is busy rather than a new run silently replacing the running one.
            if (!planController.BeginPlanning(AutopilotPlan.Empty(AutopilotPlanSource.FromRun(run), run.Title)))
            {
                await host.OpenWorkspaceAsync("workspace.autopilot.plan");
                return new Dictionary<string, string> { ["status"] = "busy", ["issue"] = run.IssueId };
            }

            await host.OpenWorkspaceAsync("workspace.autopilot.plan");
            return new Dictionary<string, string> { ["status"] = "planning", ["issue"] = run.IssueId };
        });

        // The CEO plan-flow surface (AC-174/AC-175): the pipeline as blocks with, later, the running step's session.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot.plan", "Autopilot (CEO)", context => new AutopilotPlanWorkspaceBody(host, context, settings, planController, manager, queue, history))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });

        // Open the Autopilot workspace from the side menu (Raymond 2026-07-22): just add it if it is not open yet and
        // navigate to it — it does not force a planning round. From the surface the operator starts a run with New run
        // (which is where the CEO-profile guard now lives), so the workspace and its history are reachable without a
        // profile set. A triggered run still opens straight into a planning round through the "plan" intent above.
        host.AddSideMenuButton("Autopilot (CEO)", () => _ = host.OpenWorkspaceAsync("workspace.autopilot.plan"));
    }

    public void Dispose()
    {
    }

    // A planning round needs a CEO profile: without one the host falls back to whatever the first configured profile is
    // — which may be a local/plugin model that cannot plan (Raymond 2026-07-21). Rather than start a round that quietly
    // misbehaves, tell the operator and offer the settings where they pick one. Returns whether a profile is set.
    private static bool _RequireCeoProfile(ICockpitHost host, AutopilotSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CeoProfileLabel()))
        {
            return true;
        }

        host.ShowToast(
            "Set a CEO profile in the Autopilot settings before planning.",
            PluginToastSeverity.Warning,
            "Open settings",
            () => _ = host.ShowSettingsAsync());
        return false;
    }
}
