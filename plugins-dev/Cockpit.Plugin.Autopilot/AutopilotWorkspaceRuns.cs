using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// AC-1398: what an open Autopilot workspace runs, held by the backend part. While attached it is the manager's
// runner: each run embeds its sessions in that workspace by id and settles here into history, toasts and the epic
// chain. The workspace itself only reads the runs and the pane ids of their sessions.
internal sealed class AutopilotWorkspaceRuns
{
    private readonly ICockpitHost _host;
    private readonly string _workspaceId;
    private readonly AutopilotSettings _settings;
    private readonly AutopilotPlanController _plan;
    private readonly AutopilotRunManager _manager;
    private readonly AutopilotRunQueue _queue;
    private readonly AutopilotRunHistory _history;
    private readonly Func<Action, Task> _runOnUi;
    private readonly Func<AutopilotRun, Task<string?>> _startPlanningForSub;
    private readonly List<AutopilotRunContext> _active = [];
    private readonly CancellationTokenSource _closing = new();
    private int _completedRuns;
    private IEmbeddedSession? _planningCeo;

    // `runOnUi` runs session embedding and teardown on the UI thread; `startPlanningForSub` is the workspace's own
    // planning start, which an epic chain calls for its next sub.
    public AutopilotWorkspaceRuns(
        ICockpitHost host,
        string workspaceId,
        AutopilotSettings settings,
        AutopilotPlanController plan,
        AutopilotRunManager manager,
        AutopilotRunQueue queue,
        AutopilotRunHistory history,
        Func<Action, Task> runOnUi,
        Func<AutopilotRun, Task<string?>> startPlanningForSub)
    {
        _host = host;
        _workspaceId = workspaceId;
        _settings = settings;
        _plan = plan;
        _manager = manager;
        _queue = queue;
        _history = history;
        _runOnUi = runOnUi;
        _startPlanningForSub = startPlanningForSub;

        // Being the manager's runner starts any runs already queued; Close clears it, so no run starts with no surface.
        _manager.Runner = _StartRun;
    }

    // The runs in flight, in start order. Changed on the UI thread only, like the list the workspace used to keep.
    public IReadOnlyList<AutopilotRunContext> Active => _active;

    // Raised when a run starts, moves or settles, so the workspace re-renders.
    public event Action? Changed;

    // The workspace was really closed: stop being the runner and cancel every run, which unwinds its driver loop and
    // coordinator awaits and closes its step sessions and CEO; a chain still waiting on a gate stops too.
    public void Close()
    {
        _manager.Runner = null;
        foreach (var context in _active.ToList())
        {
            context.Cancel();
        }

        _closing.Cancel();
    }

    // The planning round's CEO (AC-174), embedded in this workspace and bound to the shared plan controller so only
    // that pane may emit the plan. Null when the host could not embed it.
    public IEmbeddedSession? EmbedPlanningCeo(EmbeddedSessionRequest request)
    {
        var ceo = _host.EmbedSession(_workspaceId, request);
        if (ceo is null)
        {
            return null;
        }

        _planningCeo = ceo;
        _plan.BindSession(ceo.PaneId);
        return ceo;
    }

    // The pop-out closed, approved or cancelled: the planning CEO goes; the run gets a validator of its own.
    public void ClosePlanningCeo()
    {
        if (_planningCeo is { } planningCeo)
        {
            _planningCeo = null;
            _ = planningCeo.CloseAsync();
        }
    }

    private void _OnRunChanged() => Changed?.Invoke();

    // The manager's runner (AC-174): start a run for a dequeued plan in its own context, track it for the surface, and
    // hand the manager the coordinator and completion task. Removing it from the surface when it settles is marshalled to
    // the UI thread, since the run task can complete off it.
    private AutopilotRunHandle _StartRun(AutopilotPlan plan)
    {
        var context = new AutopilotRunContext(_host, _workspaceId, _settings, plan, _runOnUi);
        _ = _runOnUi(() =>
        {
            _active.Add(context);
            context.Changed += _OnRunChanged;
            Changed?.Invoke();
        });
        _ = _RemoveWhenDoneAsync(context);
        return new AutopilotRunHandle(context.Coordinator, context.Completed);
    }

    private async Task _RemoveWhenDoneAsync(AutopilotRunContext context)
    {
        try
        {
            await context.Completed;
        }
        catch (Exception)
        {
            // The run settled or died; either way drop it from the surface.
        }

        // Snapshot the settled run off the controller before dropping it, so history and the toast read a coherent state.
        var controller = context.Controller;
        var settledPlan = controller.Plan;
        var outcome = controller.Phase;
        var blockReason = controller.BlockReason;
        var blockadeAnswers = controller.BlockadeAnswers;
        var pullRequestMissing = controller.PullRequestMissing;
        var mergeResult = context.Coordinator.MergeResult;
        var strandedCommits = context.Coordinator.StrandedCommits;
        var runWorktreePath = context.Coordinator.RunWorktreePath;

        AutopilotRunRecord? settled = null;
        List<AutopilotRunRecord> epicRuns = [];
        await _runOnUi(() =>
        {
            _active.Remove(context);
            context.Changed -= _OnRunChanged;
            settled = _RecordAndNotify(settledPlan, outcome, blockReason, context.RunId, blockadeAnswers, pullRequestMissing);
            Changed?.Invoke();

            // Read on the UI thread, where history is mutated, so a second run settling meanwhile cannot race this.
            if (settledPlan?.Source is { EpicId.Length: > 0 } epic)
            {
                epicRuns = _history.Items.Where(record => string.Equals(record.EpicId, epic.EpicId, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        });

        // AC-1340: an epic sub that settled hands the chain its facts — the next Ready sub starts itself, or the
        // chain says why not. Awaited here, off the UI thread, since resolving the next sub fetches and reads links.
        // A run cancelled by a closing workspace has no surface left to chain on.
        if (settled is not null && !context.IsCancelled && settledPlan?.Source is { EpicId.Length: > 0 } source)
        {
            await _ChainNextSubAsync(source, settledPlan, settled, epicRuns, mergeResult, strandedCommits, runWorktreePath);
        }
    }

    // The settle-hook's chain (AC-1340): the same resolve the epic click runs, the same planning start the "plan"
    // intent makes — the automation replaces exactly that click, nothing else. Stops are said on the epic and to the
    // assistant by the chain itself; a fault in this hook is reported the same way rather than swallowed.
    private async Task _ChainNextSubAsync(AutopilotPlanSource source, AutopilotPlan plan, AutopilotRunRecord settled, IReadOnlyList<AutopilotRunRecord> epicRuns, AutopilotMergeResult? mergeResult, bool strandedCommits, string? runWorktreePath)
    {
        var provider = _host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, source.Tracker, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _ = await _host.NotifyAssistantAsync("epic-chain", $"{source.EpicId}: Autopilot could not continue this epic's chain after {settled.Ticket}: no tracker provider for {source.Tracker} is loaded.");
            return;
        }

        var epic = new AutopilotRun(source.Tracker, source.EpicId, string.Empty, string.Empty, new Dictionary<string, string>());
        var repositoryDirectory = AutopilotWorkingDirectory.Resolve(_host.Sessions, plan.WorkingDirectory);
        var collectionBranch = AutopilotCollectionBranch.For(_settings.EpicDirectToMain(), source.EpicId);

        Func<string, CancellationToken, Task> commentEpic = (text, cancellationToken) => provider.PostCommentAsync(source.EpicId, text, cancellationToken);
        Func<string, Task<bool>> notifyAssistant = text => _host.NotifyAssistantAsync("epic-chain", $"{source.EpicId}: {text}");

        // AC-1341: the epic gate measures in the settled run's worktree, which the merge gate left on the collection
        // tip; a run without one (or without a collection branch) has no gate, and the chain runs as AC-1340 did.
        AutopilotEpicGate? gate = collectionBranch is { Length: > 0 } collection && runWorktreePath is { Length: > 0 } worktree
            ? new AutopilotEpicGate(
                new GitCliEpicGateExecutor(),
                source.EpicId,
                collection,
                worktree,
                _settings.EpicGateSuiteCommand(),
                TimeSpan.FromMinutes(_settings.EpicGateSuiteTimeoutMinutes()),
                _settings.EpicGateBaselineDirectory(),
                async (sub, cancellationToken) => (await provider.GetIssueSnapshotAsync(sub, cancellationToken)).Description,
                _manager.AwaitEpicGoAsync,
                commentEpic,
                notifyAssistant)
            : null;

        var chain = new AutopilotEpicChain(
            cancellationToken => AutopilotEpicRunner.ResolveAsync(
                provider,
                epic,
                _settings.ExecutableStage(source.Tracker),
                new GitEpicSubMergeChecker(repositoryDirectory, collectionBranch),
                cancellationToken,
                _settings.AcceptanceHeadings(),
                new AutopilotMergeBuildLedger(_host.Storage).LastBuild(collectionBranch)),
            _startPlanningForSub,
            commentEpic,
            notifyAssistant,
            _settings.ChainUncleanRunTolerance(),
            gate is null ? null : gate.RunAsync,
            _settings.EpicGateEverySubs());

        try
        {
            _ = await chain.ContinueAsync(settled, epicRuns, mergeResult, strandedCommits, _closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // The workspace closed under the chain (a gate waiting for its go, a suite still running): nothing to say to.
        }
        catch (Exception failure)
        {
            _ = await _host.NotifyAssistantAsync("epic-chain", $"{source.EpicId}: Autopilot could not continue this epic's chain after {settled.Ticket}: {failure.Message}");
        }
    }

    // Whether a run's final phase gets recorded in history and toasted (AC-196): merge-ready, blocked, or
    // operator-stopped. Still-Running means it was cancelled by a closed workspace, nothing to record.
    internal static bool IsSettledOutcome(AutopilotPlanPhase outcome) =>
        outcome is AutopilotPlanPhase.MergeReady or AutopilotPlanPhase.Blocked or AutopilotPlanPhase.Stopped;

    // Returns the record it added to history, or null when the run was not a settled one (AC-1340 reads it).
    private AutopilotRunRecord? _RecordAndNotify(AutopilotPlan? plan, AutopilotPlanPhase outcome, string? blockReason, string runId, int blockadeAnswers, bool pullRequestMissing)
    {
        // A run counts as settled — recorded and toasted — when it merged-ready, blocked, or the operator stopped it
        // (AC-196). A run still Running is a cancelled/closed workspace with nothing to record.
        AutopilotRunRecord? settled = null;
        if (IsSettledOutcome(outcome) && plan is not null)
        {
            _completedRuns++;
            settled = AutopilotRunRecord.Capture(plan, outcome, blockReason, runId, blockadeAnswers, pullRequestMissing, DateTimeOffset.Now);
            _history.Add(settled);

            // AC-346: this run's sub came from an epic chain — one progress comment on the epic per settled step.
            // Best-effort and fire-and-forget, like every other tracker write in this plugin.
            if (plan.Source is { EpicId.Length: > 0 } source)
            {
                _ = _PostEpicProgressAsync(source, plan, outcome, blockReason, pullRequestMissing);
            }

            var label = string.IsNullOrWhiteSpace(plan.Label) ? "Autopilot run" : plan.Label;
            switch (outcome)
            {
                case AutopilotPlanPhase.MergeReady:
                    // The reliability line right after the settle that just moved it (AC-347) — computed after the Add
                    // above so the just-settled run is already counted in it.
                    var reliability = AutopilotRunReliability.Summarize(_history.Items).Describe();
                    _host.ShowToast($"Run “{label}” is merge-ready. {reliability}", PluginToastSeverity.Success);
                    break;
                case AutopilotPlanPhase.Stopped:
                    _host.ShowToast($"Run “{label}” stopped.", PluginToastSeverity.Information);
                    break;
                default:
                    _host.ShowToast($"Run “{label}” is blocked — {blockReason}", PluginToastSeverity.Warning);
                    break;
            }
        }

        // The whole queue drained: after a staged batch (more than one run), a single summary that it is all done. A lone
        // run needs no summary — its own toast above already said it finished.
        if (_active.Count == 0 && _queue.Count == 0)
        {
            if (_completedRuns >= 2)
            {
                _host.ShowToast($"All queued Autopilot runs finished ({_completedRuns}).", PluginToastSeverity.Information);
            }

            _completedRuns = 0;
        }

        return settled;
    }

    // AC-346: the epic-runner's progress comment, one per settled sub-run, written onto the epic (not the sub).
    // Reuses the same reliability line as the merge-ready toast, but scoped to just this epic's own settled runs —
    // the ticket asks for the chain's state, not a figure blended with unrelated runs.
    private async Task _PostEpicProgressAsync(AutopilotPlanSource source, AutopilotPlan plan, AutopilotPlanPhase outcome, string? blockReason, bool pullRequestMissing)
    {
        var provider = _host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, source.Tracker, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return;
        }

        var epicRuns = _history.Items.Where(record => string.Equals(record.EpicId, source.EpicId, StringComparison.OrdinalIgnoreCase)).ToList();
        var reliability = AutopilotRunReliability.Summarize(epicRuns);
        var label = string.IsNullOrWhiteSpace(plan.Label) ? source.IssueId : plan.Label;
        var comment = BuildEpicProgressComment(source.IssueId, label, outcome, blockReason, pullRequestMissing, reliability);

        try
        {
            _ = await provider.PostCommentAsync(source.EpicId, comment);
        }
        catch (Exception)
        {
            // Fail-soft, as every other tracker write in this plugin is.
        }
    }

    // Extracted as a pure static, same reasoning as AutopilotRunRecord.Capture: the comment's exact wording is
    // unit-testable without a UI or a tracker fake (AC-346 review — the settle-hook comment had no test on its actual
    // text, only on the building blocks underneath it).
    internal static string BuildEpicProgressComment(string subIssueId, string label, AutopilotPlanPhase outcome, string? blockReason, bool pullRequestMissing, AutopilotReliabilitySummary reliability)
    {
        var outcomeText = outcome switch
        {
            // A merge-ready run that could not actually open its PR (AC-347's own warning) is not "done" from the
            // epic's point of view either — say so, rather than reporting success on a step that still needs a human
            // to open the PR by hand before the next sub can even be considered merged.
            AutopilotPlanPhase.MergeReady when pullRequestMissing =>
                "reached merge-ready but could not open its pull request — it still needs a human to open one by hand",
            AutopilotPlanPhase.MergeReady => "reached a merge-ready PR",
            AutopilotPlanPhase.Stopped => "was stopped by the operator",
            _ => $"blocked — {blockReason}",
        };

        return $"Epic step {subIssueId} ({label}) {outcomeText}. {reliability.Describe()}";
    }

}
