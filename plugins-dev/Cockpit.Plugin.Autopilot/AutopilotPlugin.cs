using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// Autopilot (AC-94/AC-174): the operator-triggered "issue → merge-ready PR" plugin. The CEO plans the work, the
// operator approves it once, then an autonomous run drives each step — embedding an isolated session per step,
// validating it, and settling merge-ready or blocked. A tracker's "Plan in Autopilot" hands the issue to the CEO.
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

        // The planning controller (AC-174): the CEO's live draft during one planning round. Planning is decoupled
        // from executing — a frozen plan goes to the run queue and runs execute on their own controllers — so the
        // operator can plan a new run while others run.
        var planController = new AutopilotPlanController();

        // The queue of approved runs (persistent) and the manager that runs them up to the concurrency cap.
        var queue = new AutopilotRunQueue(host.Storage);
        var manager = new AutopilotRunManager(queue, settings);

        // The history of settled runs (persistent): a run that finishes leaves the live surface, so
        // it is recorded here to be shown in the history section rather than vanishing.
        var history = new AutopilotRunHistory(host.Storage);

        // The template store (AC-189): the operator's own templates and their edits of the plugin/builtin ones,
        // persisted like the queue and history above. List() merges the in-memory plugin registrations with the
        // persisted user/override templates into the one list the settings UI and the plan-flow picker read from.
        var templates = new AutopilotTemplateStore(host.Storage);

        // The gear next to the plugin in the manager opens this — the global-level settings. Handed the host so
        // the CEO-profile picker can list profiles/models, and the template store for the Templates section.
        host.AddSettings(() => new AutopilotSettingsControl(settings, host, templates));

        // The CEO's plan-emit tool during the planning round (AC-174): live only while planning, pane-scoped so
        // only the bound CEO session may set the plan. Internal-only (AC-204): the run's own agents scope to it
        // by name, but a normal operator must never see or tick it in the New-session/profile MCP selection.
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planController, settings), isEnabled: () => planController.Phase == AutopilotPlanPhase.Planning, isInternal: true);

        // The autonomous run's report channel (AC-174): a step agent signals done, a run's CEO validator reports
        // its verdict — both pane-scoped, routed by the manager to whichever run owns the caller pane. Live while
        // any run is executing.
        _ = host.AddMcpEndpoint(AutopilotRunTools.EndpointName, new AutopilotRunTools(host, manager), isEnabled: () => manager.Active.Count > 0, isInternal: true);

        // The CEO validator's own tools (AC-174): validate a step, keep the source issue in sync. A separate
        // endpoint from the step agents' one above, for least-privilege — a step agent never gets the CEO's tools.
        // Same pane-scoping and live-only gating.
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

            // AC-346: before the stage gate below, find out whether the clicked item is an epic. Same caller/tracker
            // guard as _RefuseAsync's tracker-write below — only the tracker plugin that owns the item triggers that
            // read/write. A non-epic item costs one link lookup and falls through unchanged (ResolveAsync returns NotEpic).
            if (!string.IsNullOrWhiteSpace(run.IssueId) && string.Equals(intent.CallerPluginId, run.Tracker, StringComparison.OrdinalIgnoreCase)
                && host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, run.Tracker, StringComparison.OrdinalIgnoreCase)) is { } provider)
            {
                // The repository the merge check runs git in (AC-346 review): not known yet here, so this uses the
                // same fallback AutopilotWorkingDirectory.Resolve uses. If neither resolves, AutopilotEpicRunner
                // turns the unknown merge status into a paused chain with a comment, not a silent restart.
                var repositoryDirectory = host.Sessions.ActiveSessionWorkingDirectory is { Length: > 0 } active
                    ? active
                    : Directory.GetCurrentDirectory();

                var epicOutcome = await AutopilotEpicRunner.ResolveAsync(
                    provider,
                    run,
                    settings.ExecutableStage(run.Tracker),
                    new GitEpicSubMergeChecker(repositoryDirectory),
                    CancellationToken.None);

                switch (epicOutcome.Kind)
                {
                    case AutopilotEpicOutcomeKind.Paused:
                        await _PauseEpicAsync(provider, run.IssueId, epicOutcome.PausedSubId, epicOutcome.Reason!);
                        return new Dictionary<string, string> { ["status"] = "epic-paused", ["issue"] = run.IssueId, ["sub"] = epicOutcome.PausedSubId ?? string.Empty };
                    case AutopilotEpicOutcomeKind.Complete:
                        return new Dictionary<string, string> { ["status"] = "epic-complete", ["issue"] = run.IssueId };
                    case AutopilotEpicOutcomeKind.Ready:
                        // Replace the clicked epic with the sub the epic-runner picked — everything below plans that
                        // sub exactly as if it had been clicked directly, including the (already-passed) Ready gate.
                        run = epicOutcome.Run!;
                        break;
                    case AutopilotEpicOutcomeKind.NotEpic:
                    default:
                        break;
                }
            }

            // AC-346: a sub the epic-runner picked may already be mid-run. Without this, a second click could
            // start a second worktree and PR on the same ticket. Scoped to tracker+issue; never blocks a
            // deliberate replan of a sub that already settled — only an in-flight run counts.
            if (_HasRunInFlight(planController, queue, manager, run.Tracker, run.IssueId))
            {
                return new Dictionary<string, string> { ["status"] = "already-running", ["issue"] = run.IssueId };
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
        host.AddWorkspaceType(new WorkspaceTypeRegistration("workspace.autopilot.plan", "Autopilot", context => new AutopilotPlanWorkspaceBody(host, context, settings, planController, manager, queue, history, templates))
        {
            IconKind = MaterialIconKind.RobotHappyOutline,
            Description = "The CEO plans the work, you approve it once, then it runs autonomously — the pipeline on one surface.",
        });

        // Open the Autopilot workspace from the side menu — it does not force a planning round. The operator
        // starts a run with New run (where the CEO-profile guard now lives), so history stays reachable without
        // a profile set. A triggered run still opens straight into planning via the "plan" intent above.
        host.AddSideMenuButton("Autopilot", () => _ = host.OpenWorkspaceAsync("workspace.autopilot.plan"));
    }

    public void Dispose()
    {
    }

    // A planning round needs a CEO profile: without one the host falls back to whatever the first configured
    // profile is, which may be a local/plugin model that cannot plan. Tell the operator and offer settings
    // instead of starting a round that quietly misbehaves. Returns whether a profile is set.
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

    // A refused start says so twice: the toast, and a comment on the issue. Only the tracker plugin that owns
    // the issue gets that second half — without this check, any plugin could write onto arbitrary issues. The
    // host stamps the caller, so a mismatch gets the toast only. Writing is once per issue and best-effort.
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

    // AC-346: the epic-runner's chain paused — a sub not Ready, a nested epic, or an undeterminable/unreadable
    // status. Written onto the epic, not the sub, since the operator clicked the epic. Best effort, like every
    // other tracker write here — a failed comment doesn't change that the chain paused.
    private static async Task _PauseEpicAsync(ITrackerProvider provider, string epicId, string? subId, string reason)
    {
        var text = subId is { Length: > 0 }
            ? $"Autopilot paused this epic's chain at {subId}: {reason}"
            : $"Autopilot paused this epic's chain: {reason}";

        try
        {
            _ = await provider.PostCommentAsync(epicId, text);
        }
        catch (Exception)
        {
            // Fail-soft, as the run coordinator's tracker writes are.
        }
    }

    // AC-346: whether a run on this exact tracker+issue is already in flight — not yet settled — across the
    // shared planning draft, the queue, and the manager's active runs. A settled run never counts; only a
    // genuinely still-running one blocks a second start, so a deliberate replan is never refused by this guard.
    internal static bool _HasRunInFlight(AutopilotPlanController planController, AutopilotRunQueue queue, AutopilotRunManager manager, string tracker, string issueId)
    {
        bool Matches(AutopilotPlanSource? source) =>
            source is not null
            && string.Equals(source.Tracker, tracker, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.IssueId, issueId, StringComparison.OrdinalIgnoreCase);

        if (planController.Phase is AutopilotPlanPhase.Planning or AutopilotPlanPhase.Running or AutopilotPlanPhase.AwaitingOperator
            && Matches(planController.Plan?.Source))
        {
            return true;
        }

        if (queue.Items.Any(plan => Matches(plan.Source)))
        {
            return true;
        }

        return manager.Active.Any(coordinator => Matches(coordinator.Plan?.Source));
    }
}
