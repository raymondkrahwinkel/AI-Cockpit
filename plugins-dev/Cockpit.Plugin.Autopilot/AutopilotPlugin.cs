using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Tracking;
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
        _ = host.AddMcpEndpoint(AutopilotPlanTools.EndpointName, new AutopilotPlanTools(host, planController, settings), isEnabled: () => planController.Phase == AutopilotPlanPhase.Planning, isInternal: true);

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

            // AC-346: before the stage gate below, find out whether the clicked item is an epic (has "parent for"
            // children) rather than a single issue. Same caller/tracker guard as _RefuseAsync's tracker-write below —
            // an epic click reads the epic's own links and, on a pause, writes a comment onto it, so only the tracker
            // plugin that owns the item gets to trigger that read/write, never an arbitrary caller naming someone
            // else's issue. A non-epic item (children.Count == 0) costs one link lookup and falls straight through to
            // the unchanged single-issue path below — ResolveAsync returns NotEpic and run is never replaced.
            if (!string.IsNullOrWhiteSpace(run.IssueId) && string.Equals(intent.CallerPluginId, run.Tracker, StringComparison.OrdinalIgnoreCase)
                && host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, run.Tracker, StringComparison.OrdinalIgnoreCase)) is { } provider)
            {
                // The repository the merge check runs git in (AC-346 review): the operator's chosen directory is not
                // known yet at this point (no plan/session exists until planning starts), so this uses the same
                // fallback tier AutopilotWorkingDirectory.Resolve does for that same case — the active session's
                // directory — with the cockpit's own working directory as the very last resort, never the only source.
                // Should neither resolve to a real repository, GitEpicSubMergeChecker.RefreshAsync leaves its state
                // empty and IsMerged answers null for every sub, which AutopilotEpicRunner turns into a paused chain
                // with a comment rather than silently treating every sub as unmerged and restarting from the first one.
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

            // AC-346: a sub the epic-runner picked may already be mid-run — approved and executing (manager.Active),
            // staged behind others (queue), or still on the shared planning draft (planController, not yet approved).
            // Without this, a second click on the epic while its current sub is merge-ready but not yet merged (or
            // MaxConcurrentRuns allows more than one run at once) would start a second worktree and a second PR on the
            // very same ticket. Scoped to tracker+issue rather than only epic subs — the same double-click risk exists
            // for a plain issue — but never blocks a deliberate replan of a sub that already settled: only an
            // in-flight (not yet settled) run counts.
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

        // Open the Autopilot workspace from the side menu: just add it if it is not open yet and
        // navigate to it — it does not force a planning round. From the surface the operator starts a run with New run
        // (which is where the CEO-profile guard now lives), so the workspace and its history are reachable without a
        // profile set. A triggered run still opens straight into a planning round through the "plan" intent above.
        host.AddSideMenuButton("Autopilot", () => _ = host.OpenWorkspaceAsync("workspace.autopilot.plan"));
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

    // AC-346: the epic-runner's chain paused — a sub not Ready, a nested epic, an undeterminable merge status, or the
    // epic's own link structure could not be read (subId is null only for that last case, before any sub is even
    // known). Written onto the epic, not the sub, since it is the epic the operator clicked and the epic's comment
    // trail is where the DoD wants "which sub, and why" to be readable without opening the sub. Best effort, like
    // every other tracker write here — a comment that fails to land does not change that the chain already paused;
    // the caller's returned status already says so.
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

    // AC-346: whether a run on this exact tracker+issue is already in flight — not yet settled — across every place
    // one can currently be: the shared planning draft (still being shaped or awaiting approval), the queue (approved,
    // waiting for a free slot), and the active runs a manager is executing right now. A settled run (merge-ready,
    // blocked, stopped) never counts — only a genuinely still-running one blocks a second start, so a deliberate
    // replan of something that already finished is never refused by this guard.
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
