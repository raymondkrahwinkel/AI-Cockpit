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

        // The CEO plan-flow controller (AC-174): the single run's state, from the planning round through the autonomous run.
        var planRuns = new AutopilotPlanController();

        // Drives an approved plan's autonomous run: embeds each step's agent, awaits its done-report, has the CEO
        // validate it. Shared with the run's MCP tools (below) and the workspace body (which starts it on approval).
        var coordinator = new AutopilotRunCoordinator(host, planRuns);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so the
        // CEO-profile picker can list the cockpit's profiles and offer each one's models.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host));

        // The CEO's plan-emit tool during the planning round (AC-174): live only while planning, and pane-scoped so only
        // the bound CEO session may set the plan. The workspace body briefs the CEO to call it; the operator approves the
        // plan in the UI to freeze it and start the run.
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planRuns), isEnabled: () => planRuns.Phase == AutopilotPlanPhase.Planning);

        // The autonomous run's report channel (AC-174): a step agent signals done, the CEO reports its validation
        // verdict — both pane-scoped. Enabled from planning through the run so the CEO session (started in the planning
        // round) carries the validate tool, and step agents (started during the run) carry the done tool; dark once
        // the run settles.
        _ = host.AddMcpEndpoint(AutopilotRunTools.EndpointName, new AutopilotRunTools(host, coordinator), isEnabled: () => planRuns.Phase is AutopilotPlanPhase.Planning or AutopilotPlanPhase.Running or AutopilotPlanPhase.AwaitingOperator);

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
            if (!planRuns.BeginPlanning(AutopilotPlan.Empty(AutopilotPlanSource.FromRun(run), run.Title)))
            {
                await host.OpenWorkspaceAsync("workspace.autopilot.plan");
                return new Dictionary<string, string> { ["status"] = "busy", ["issue"] = run.IssueId };
            }

            await host.OpenWorkspaceAsync("workspace.autopilot.plan");
            return new Dictionary<string, string> { ["status"] = "planning", ["issue"] = run.IssueId };
        });

        // The CEO plan-flow surface (AC-174/AC-175): the pipeline as blocks with, later, the running step's session.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot.plan", "Autopilot (CEO)", context => new AutopilotPlanWorkspaceBody(host, context, settings, planRuns, coordinator))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });

        // Start Autopilot with only the CEO (AC-174): an empty plan opens the planning round, the CEO builds the steps
        // from the conversation, and one approval starts the autonomous run. A triggered run enters the same round with
        // a source to draft from (the "plan" intent above).
        host.AddSideMenuButton("Autopilot (CEO)", () =>
        {
            if (!_RequireCeoProfile(host, settings))
            {
                return;
            }

            // BeginPlanning refuses (returns false) while a run is live, so a second click cannot reset it; either way we
            // surface the workspace — freshly planning, or the run already in flight.
            _ = planRuns.BeginPlanning(AutopilotPlan.Empty(source: null, goal: string.Empty));
            _ = host.OpenWorkspaceAsync("workspace.autopilot.plan");
        });
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
