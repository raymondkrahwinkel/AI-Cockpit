using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Autopilot (AC-94): the operator-triggered "issue → merge-ready PR" plugin. This build (AC-149) is the skeleton
/// — it contributes a full-surface "Autopilot" workspace type (its shell) and the settings foundation. The
/// trigger, the run start, the done-gates and the tracker channel arrive in later Autopilot sub-tickets.
/// </summary>
public sealed class AutopilotPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "autopilot",
        DisplayName: "Autopilot",
        // In lockstep with plugin.json's version: the manifest gates loading, this shows in the plugin list.
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Operator-triggered \"issue → merge-ready PR\" pipeline. This build is the workspace shell and settings foundation.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new AutopilotSettings(host.Storage);
        var runs = new AutopilotRunController(settings);

        // The CEO plan-flow (AC-174), growing up alongside the gate-based flow above rather than replacing it in one move.
        var planRuns = new AutopilotPlanController();

        // Drives an approved plan's autonomous run: embeds each step's agent, awaits its done-report, has the CEO
        // validate it. Shared with the run's MCP tools (below) and the workspace body (which starts it on approval).
        var coordinator = new AutopilotRunCoordinator(host, planRuns);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so the
        // CEO-profile picker can list the cockpit's profiles and offer each one's models.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host));

        // The run's own agent reports its done-gate outcomes back through this in-process endpoint (AC-153); it is dark
        // when no run is active, and each tool binds to the run only when the verified caller pane is its session.
        _ = host.AddMcpEndpoint("cockpit-autopilot", new AutopilotMcpTools(host, runs), isEnabled: () => runs.Current is not null);

        // The CEO's plan-emit tool during the planning round (AC-174): live only while planning, and pane-scoped so only
        // the bound CEO session may set the plan. The workspace body briefs the CEO to call it; the operator approves the
        // plan in the UI to freeze it and start the run.
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planRuns), isEnabled: () => planRuns.Phase == AutopilotPlanPhase.Planning);

        // The autonomous run's report channel (AC-174): a step agent signals done, the CEO reports its validation
        // verdict — both pane-scoped. Enabled from planning through the run so the CEO session (started in the planning
        // round) carries the validate tool, and step agents (started during the run) carry the done tool; dark once
        // the run settles.
        _ = host.AddMcpEndpoint(AutopilotRunTools.EndpointName, new AutopilotRunTools(host, coordinator), isEnabled: () => planRuns.Phase is AutopilotPlanPhase.Planning or AutopilotPlanPhase.Running or AutopilotPlanPhase.AwaitingOperator);

        // The trigger's receiving half (AC-150) plus the opstart flow (AC-151): a tracker's "Start in Autopilot" sends
        // this intent; Autopilot records the point, surfaces its workspace, runs a short scoping judgment, and either
        // parks the point (refused) or advances it to running — the body then embeds the isolated session.
        host.RegisterIntentHandler("start", async intent =>
        {
            var run = AutopilotRun.FromIntent(intent);
            runs.BeginScoping(run);
            await host.OpenWorkspaceAsync("workspace.autopilot");

            var verdict = await _ScopeAsync(host, settings, run);

            // Scoping can take a while; if a newer point took over the surface meanwhile, its own start drives it —
            // this late verdict must not flip that run.
            if (!runs.IsCurrent(run))
            {
                return new Dictionary<string, string> { ["status"] = "superseded", ["issue"] = run.IssueId };
            }

            if (!verdict.IsWorkable)
            {
                runs.Refuse(verdict.Reason);
                return new Dictionary<string, string> { ["status"] = "refused", ["issue"] = run.IssueId, ["reason"] = verdict.Reason };
            }

            // Advance to running: the workspace body embeds the isolated session, confirms the autonomous run with the
            // operator over it (AC-152), and briefs the agent only on approval.
            runs.MarkRunning();
            return new Dictionary<string, string> { ["status"] = "started", ["issue"] = run.IssueId };
        });

        // The CEO-flow trigger (AC-174): a tracker's "Plan in Autopilot" hands the item to the CEO planning round with
        // its source to draft from, rather than the gate-based scoping flow above. The two grow side by side until the
        // gate-based "start" flow is retired onto this one (convergence).
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

        // The full-surface workspace. The type id is a frozen API surface — it is persisted on every Autopilot
        // workspace, so changing it would orphan saved ones. The body reads the started run from the shared controller;
        // the run pipeline and its embedded session land in later sub-tickets.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot", "Autopilot", context => new AutopilotWorkspaceBody(host, context, settings, runs))
        {
            IconKind = MaterialIconKind.RobotOutline,
            Description = "Run an issue to a merge-ready PR — the pipeline, its live session and the done-gate on one surface.",
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

    // Scoping (decision #3): delegate a short judgment to the configured profile and read its verdict. No profile set
    // → the point starts unjudged; a delegation error → it starts anyway, since the operator asked for it explicitly
    // and a broken gate must not swallow their intent.
    private static async Task<ScopingVerdict> _ScopeAsync(ICockpitHost host, AutopilotSettings settings, AutopilotRun run)
    {
        var profile = settings.ScopingProfileLabel();
        if (string.IsNullOrWhiteSpace(profile))
        {
            return ScopingVerdict.Workable;
        }

        try
        {
            var answer = await host.Actions.DelegateAsync(profile, _BuildScopingPrompt(run), timeout: TimeSpan.FromMinutes(2));
            return ScopingVerdict.Parse(answer);
        }
        catch (Exception)
        {
            return ScopingVerdict.Workable;
        }
    }

    private static string _BuildScopingPrompt(AutopilotRun run)
    {
        var description = run.Data.GetValueOrDefault("description", string.Empty);
        return $"""
            You are scoping a work item for an automated "issue to merge-ready PR" run. Decide whether it is workable
            as-is: clear enough, small enough, and with a discernible acceptance. Refuse it only when it is too large,
            too vague, or has no clear acceptance to work towards.

            Answer on the FIRST line with exactly one of:
            WORKABLE
            REFUSE: <one short sentence naming what is missing>

            Item ({run.Tracker} {run.IssueId}): {run.Title}
            {description}
            """;
    }
}
