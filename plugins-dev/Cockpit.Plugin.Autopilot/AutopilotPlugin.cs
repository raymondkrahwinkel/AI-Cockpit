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

        // The history of settled runs (persistent): a run that finishes leaves the live surface, so
        // it is recorded here to be shown in the history section rather than vanishing.
        var history = new AutopilotRunHistory(host.Storage);

        // The template store (AC-189): the operator's own templates and their edits of the plugin/builtin ones, persisted
        // like the queue and history above. It follows the same IPluginStorage pattern; List() merges the passed-in
        // in-memory plugin registrations (host.RegisteredAutopilotTemplates) with those persisted user/override templates
        // into the one list both the settings beheer-UI and the plan-flow picker read from.
        var templates = new AutopilotTemplateStore(host.Storage);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so the
        // CEO-profile picker can list the cockpit's profiles and offer each one's models, and the template store so the
        // Templates section lists/creates/edits/resets the combined templates.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host, templates));

        // The CEO's plan-emit tool during the planning round (AC-174): live only while planning, and pane-scoped so only
        // the bound CEO session may set the plan. The workspace body briefs the CEO to call it; approving submits the plan.
        // Internal-only (AC-204): the run's own agents scope to these endpoints by name (McpServers), so they must
        // stay mountable — but a normal operator must never see or tick them in the New-session/profile MCP selection,
        // nor have them fan into an unrelated no-selection session while a run is live.
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planController), isEnabled: () => planController.Phase == AutopilotPlanPhase.Planning, isInternal: true);

        // The autonomous run's report channel (AC-174): a step agent signals done, a run's CEO validator reports its
        // verdict — both pane-scoped, routed by the manager to whichever run owns the caller pane. Live while any run is
        // executing; dark when none is.
        _ = host.AddMcpEndpoint(AutopilotRunTools.EndpointName, new AutopilotRunTools(host, manager), isEnabled: () => manager.Active.Count > 0, isInternal: true);

        // The CEO validator's own tools (AC-174): validate a step, keep the source issue in sync. A
        // separate endpoint from the step agents' one above, so a step agent is never handed the CEO's tools — tighter
        // least-privilege, and a weaker local model is not distracted into calling a validate/tracker tool. Same
        // pane-scoping and live-only gating; the CEO validator session is given this endpoint, the step agents are not.
        _ = host.AddMcpEndpoint(AutopilotCeoTools.EndpointName, new AutopilotCeoTools(host, manager), isEnabled: () => manager.Active.Count > 0, isInternal: true);

        // The issues a refusal has already been written onto, so clicking a backlog item twice does not leave the same
        // paragraph twice. Lives for the app's lifetime — a comment is only worth writing once per issue per session.
        var commented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The CEO-flow trigger (AC-174): a tracker's "Plan in Autopilot" hands the item to the CEO planning round with
        // its source to draft from.
        host.RegisterIntentHandler("plan", async intent =>
        {
            var run = AutopilotRun.FromIntent(intent);

            if (!_RequireCeoProfile(host, settings))
            {
                return new Dictionary<string, string> { ["status"] = "no-ceo-profile", ["issue"] = run.IssueId };
            }

            // The stage gate (AC-345), ahead of the CEO's own scoping judgement: what the tracker says beats what the
            // ticket text claims about itself.
            if (AutopilotReadyGate.Decide(run.Title, run.Stage, settings.ExecutableStage(run.Tracker)) is { IsAllowed: false } refusal)
            {
                await _RefuseAsync(host, intent, run, refusal.Reason, commented);
                return new Dictionary<string, string> { ["status"] = "not-ready", ["issue"] = run.IssueId };
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
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot.plan", "Autopilot (CEO)", context => new AutopilotPlanWorkspaceBody(host, context, settings, planController, manager, queue, history, templates))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });

        // Open the Autopilot workspace from the side menu: just add it if it is not open yet and
        // navigate to it — it does not force a planning round. From the surface the operator starts a run with New run
        // (which is where the CEO-profile guard now lives), so the workspace and its history are reachable without a
        // profile set. A triggered run still opens straight into a planning round through the "plan" intent above.
        host.AddSideMenuButton("Autopilot (CEO)", () => _ = host.OpenWorkspaceAsync("workspace.autopilot.plan"));
    }

    public void Dispose()
    {
    }

    // A planning round needs a CEO profile: without one the host falls back to whatever the first configured profile is
    // — which may be a local/plugin model that cannot plan. Rather than start a round that quietly
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

    // A refused start says so twice: to the operator who pressed the button, and on the issue itself, so the reason
    // survives the toast and is there for whoever next looks at the item.
    //
    // Only the tracker plugin that owns the issue gets that second half. The intent's payload names its own tracker and
    // issue id, and any installed plugin may send an intent — without this check, "refuse and comment" would hand every
    // plugin a way to write arbitrary text onto arbitrary issues with the operator's token, which is a capability
    // Autopilot did not have before. The host stamps the caller, so the two are compared and a mismatch gets the toast
    // only. Writing is also once per issue, and best-effort: a tracker that is down or read-only costs the note, never
    // the refusal, which already stands.
    private static async Task _RefuseAsync(ICockpitHost host, PluginIntent intent, AutopilotRun run, string reason, HashSet<string> commented)
    {
        host.ShowToast(reason, PluginToastSeverity.Warning, "Open settings", () => _ = host.ShowSettingsAsync());

        if (string.IsNullOrWhiteSpace(run.IssueId) || !string.Equals(intent.CallerPluginId, run.Tracker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!commented.Add($"{run.Tracker}/{run.IssueId}"))
        {
            return;
        }

        var provider = host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, run.Tracker, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return;
        }

        try
        {
            _ = await provider.PostCommentAsync(run.IssueId, reason);
        }
        catch (Exception)
        {
            // Fail-soft, as the run coordinator's tracker writes are.
        }
    }
}
