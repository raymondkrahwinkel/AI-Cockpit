using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// Drives an approved CEO plan to completion (AC-174): embeds a session per step, awaits its done-report, has
// the CEO validate it, returning Passed/Rejected only from a real verdict — Faulted otherwise (AC-347).
// MCP calls mutate coupling state under `_lock`; UI work runs through the caller's `runOnUi`.
internal sealed class AutopilotRunCoordinator(
    ICockpitHost host,
    AutopilotPlanController plan,
    TimeSpan? stepDoneReminderDelay = null,
    TimeSpan? stepStallTimeout = null,
    IAutopilotPrPublisher? prPublisher = null,
    IAutopilotEvidenceSource? evidenceSource = null,
    Func<string, Task<IEmbeddedSession?>>? checkpointCeo = null)
{
    private readonly Lock _lock = new();

    // The plan this run is driving, or null before one is bound (AC-346) — how a caller outside this run
    // (the "plan" intent handler's duplicate-run guard) can tell which issue an already-active run is working on
    // without reaching into the private controller it wraps.
    public AutopilotPlan? Plan => plan.Plan;

    // Publishes a merge-ready code run's branch and opens its PR (AC-216); null in a bare test graph, where finalization
    // is skipped. The app supplies the real GitCliPrPublisher through AutopilotRunContext.
    private readonly IAutopilotPrPublisher? _prPublisher = prPublisher;

    // Observes what a step actually changed, so the CEO validates against the harness's account instead of the agent's
    // summary (AC-255); null in a bare test graph, and in every run the source cannot observe — both fall back to the
    // deep inspection. The app supplies the real GitCliEvidenceSource through AutopilotRunContext.
    private readonly IAutopilotEvidenceSource? _evidenceSource = evidenceSource;

    // Replaces the run's validator with a fresh session briefed on the carry-over it is handed, returning it — or null
    // when the host refused to embed one (AC-253). Null in a bare test graph, and in a run whose surface cannot embed:
    // both simply never checkpoint. The app supplies the real swap through AutopilotRunContext.
    private readonly Func<string, Task<IEmbeddedSession?>>? _checkpointCeo = checkpointCeo;

    // AC-434: a review group's gates run concurrently and can reach the leftover-work safety commit at the same
    // moment — two concurrent `git add`/`git commit` can collide on git's index lock (an adversarial-pass find).
    // One at a time removes the race; the commit itself is still best-effort (see the call site).
    private readonly SemaphoreSlim _safetyCommitGate = new(1, 1);

    // Injectable for tests (short values keep the stall test fast); production uses the defaults below.
    private readonly TimeSpan _stepDoneReminderDelay = stepDoneReminderDelay ?? StepDoneReminderDelay;
    private readonly TimeSpan _stepStallTimeout = stepStallTimeout ?? StepStallTimeout;
    private readonly Dictionary<string, TaskCompletionSource<string>> _stepAgents = new(StringComparer.Ordinal);
    private readonly List<IEmbeddedSession> _liveStepSessions = [];
    private TaskCompletionSource<bool>? _validation;
    private string? _validationReason;
    private string? _blockedPane;

    // AC-434: at most one step may be mid-validation with the CEO at a time — a single CEO conversation cannot
    // usefully judge two steps at once, and _validation/_validationReason above are a single slot. A review
    // group's gates run agent work concurrently but queue here for the CEO's turn one at a time.
    private readonly SemaphoreSlim _validationGate = new(1, 1);

    // AC-201 tiered escalation state, all guarded by _lock: _consultPane is the worker awaiting a CEO answer
    // (one at a time, fail-safe not fail-silent), _paneStepIds names which step each pane belongs to (AC-434:
    // the pane identifies a consult, not "the" active step), _maxConsultsPerStep caps consults per step.
    private string? _consultPane;
    private IEmbeddedSession? _ceoSession;
    private readonly Dictionary<string, string> _paneStepIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _consultCounts = new(StringComparer.Ordinal);
    private int _maxConsultsPerStep;

    // AC-253: how many validation turns the live validator has taken on since it was last replaced, and the interval
    // it is replaced on (0 = never). Guarded by _lock, like the CEO state above it.
    private int _validationsSinceCheckpoint;
    private int _ceoCheckpointEverySteps;

    // AC-202: the last stage this run's automatic phase→stage mapping set on the source issue, so a lifecycle
    // edge does not set the same stage twice (idempotent). Guarded by _lock. The CEO's manual
    // autopilot_tracker_stage does not touch this, so the auto-mapping never reverts a stage the CEO set by hand.
    private TrackerWorkStage? _lastAutoStage;

    // Runs the approved plan to a settled end, feeding the bounded run-driver the executeStep adapter; returns
    // when the run settles or `cancellationToken` cancels it. `showStepSession` places a started step's view on
    // the surface, invoked inside `runOnUi`, which runs session embedding/teardown on the UI thread.
    public async Task RunAsync(
        IWorkspaceContext context,
        IEmbeddedSession ceo,
        AutopilotSettings settings,
        Action<Control> showStepSession,
        Action<bool> setValidating,
        AutopilotRunEnvironment environment,
        Func<Action, Task> runOnUi,
        CancellationToken cancellationToken)
    {
        // Remember the run's CEO session and its per-step consult budget so a worker's mid-step consult (AC-201) can be
        // relayed to the CEO and the fail-closed check can see whether the CEO is still live.
        lock (_lock)
        {
            _ceoSession = ceo;
            _maxConsultsPerStep = settings.MaxConsultsPerStep();
            _ceoCheckpointEverySteps = settings.CeoCheckpointEverySteps();
        }

        // AC-202: the run has just started (the plan is Running after its single approval). Move the source issue to the
        // in-progress stage automatically — a source-triggered run must not sit on Backlog waiting for the CEO to move it
        // by hand (AC-195 did exactly that). A CEO-first run has no issue, so this is a no-op there.
        await AutoAdvanceTrackerStageAsync(TrackerWorkStage.InProgress, cancellationToken);

        // AC-215 preflight: a code run (the template asked for a PR) that will not be able to deliver one — a plain-folder
        // run, no git remote, no gh — is flagged up front, so the operator learns it now rather than at the silent end.
        await _PreflightPullRequestAsync(environment, runOnUi, cancellationToken);

        var driver = new AutopilotRunDriver(
            plan,
            settings.MaxSelfFixAttempts(),
            async step => AutopilotModelTier.HoldToCeiling(step, await host.GetProfilesAsync().ConfigureAwait(false) ?? [], settings.CostStrategy()));
        await driver.RunAsync(
            step => _ExecuteStepAsync(context, settings, showStepSession, setValidating, environment, runOnUi, step, cancellationToken),
            cancellationToken);

        // AC-202: the run settled. When it reached merge-ready (every hard step passed), move the source issue to the
        // review stage — the work is done, the merge itself is left to the human. A blocked, stopped or cancelled run is
        // left where it is (its stage stays in-progress, so the operator sees it still needs them).
        if (!cancellationToken.IsCancellationRequested && plan.Phase == AutopilotPlanPhase.MergeReady)
        {
            // AC-216: deliver the merge-ready pull request for a code run (commit → push → PR), or report a clear outcome
            // and leave the work on its branch when it cannot — never a silent "done". An admin run reports nothing.
            await _FinalizeMergeReadyAsync(environment, runOnUi, cancellationToken);
            await AutoAdvanceTrackerStageAsync(TrackerWorkStage.InReview, cancellationToken);
        }
    }

    // AC-216: does this merge-ready run deliver a PR, and can it? Combines the template signal (plan.DeliversPullRequest)
    // with a live probe of the run worktree (git run, remote, gh). Kept off the pure decision so the decision stays
    // testable without a git repo. A run that expects no PR short-circuits to NotExpected without probing.
    private async Task<AutopilotPrDelivery> _DecideDeliveryAsync(AutopilotRunEnvironment environment, CancellationToken cancellationToken)
    {
        var deliversPullRequest = plan.Plan?.DeliversPullRequest ?? false;
        if (!deliversPullRequest || _prPublisher is null || !environment.HasRunBranch)
        {
            // No PR expected, no publisher (a bare test graph), or no single run branch (a plain-folder or parallel-only
            // run): the decision is NotExpected or NoGitRun without a probe.
            return AutopilotMergeReadyDecision.Decide(deliversPullRequest, isGitRun: false, hasRemote: false, ghAvailable: false);
        }

        var probe = await _prPublisher.ProbeAsync(environment.RunWorktreePath!, cancellationToken);
        return AutopilotMergeReadyDecision.Decide(deliversPullRequest, probe.IsGitRun, probe.HasRemote, probe.GhAvailable);
    }

    // AC-215: probe the delivery up front and warn the operator when a code run will not be able to open its PR.
    private async Task _PreflightPullRequestAsync(AutopilotRunEnvironment environment, Func<Action, Task> runOnUi, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await _DecideDeliveryAsync(environment, cancellationToken);
            if (AutopilotMergeReadyDecision.PreflightWarning(delivery) is { } warning)
            {
                await runOnUi(() => host.ShowToast(warning, PluginToastSeverity.Warning));
            }
        }
        catch (Exception)
        {
            // The preflight is advisory (AC-215): a probe fault must never keep a run from starting.
        }
    }

    // AC-216: at merge-ready, publish the code run's branch and open its PR — or report a clear outcome and leave the
    // work on its branch when it cannot. Fail-soft: a publish fault is recorded, never a crashed run. Silent for an admin
    // run (NotExpected), which reports the plain "settled merge-ready".
    private async Task _FinalizeMergeReadyAsync(AutopilotRunEnvironment environment, Func<Action, Task> runOnUi, CancellationToken cancellationToken)
    {
        try
        {
            var delivery = await _DecideDeliveryAsync(environment, cancellationToken);

            string? prUrl = null;
            string? error = null;
            if (delivery is AutopilotPrDelivery.CanCreatePr or AutopilotPrDelivery.PushOnly
                && _prPublisher is not null
                && environment.HasRunBranch)
            {
                var request = new AutopilotPrRequest(
                    environment.RunWorktreePath!,
                    environment.RunWorktreeBranch!,
                    _PullRequestTitle(),
                    _PullRequestBody());

                // AC-453: a checkout the operator has gated gets no pull request while the local run is red or
                // absent. The push still happens, and the reason travels into the run's outcome. Only asked when a
                // PR was actually going to be attempted — a push-only delivery has none to hold back.
                var heldBack = delivery == AutopilotPrDelivery.CanCreatePr
                    ? await LocalCiGate.RefusalFor(host, environment.RunWorktreePath!)
                    : null;

                var result = await _prPublisher.PublishAsync(
                    request,
                    createPullRequest: delivery == AutopilotPrDelivery.CanCreatePr && heldBack is null,
                    cancellationToken);
                prUrl = result.PrUrl;
                error = heldBack ?? result.Error;
            }

            var outcome = AutopilotMergeReadyDecision.Outcome(delivery, environment.RunWorktreeBranch, environment.RunWorktreePath, prUrl);
            if (!string.IsNullOrWhiteSpace(error))
            {
                outcome = $"{outcome} ({error})";
            }

            // Surface the outcome so a code run that could not produce its PR is never a silent "done": a toast the
            // operator sees now, and a note on the last step so it persists in the run's pipeline/afronding.
            var clean = delivery == AutopilotPrDelivery.NotExpected || (!string.IsNullOrWhiteSpace(prUrl) && string.IsNullOrWhiteSpace(error));
            if (!clean)
            {
                // Recorded immediately, before the toast/note below (which could themselves throw and land in the
                // catch): a run that provably could not deliver its PR must never read back as clean (AC-347).
                plan.RecordPullRequestMissing();
            }

            await runOnUi(() => host.ShowToast(outcome, clean ? PluginToastSeverity.Success : PluginToastSeverity.Warning));

            if (delivery != AutopilotPrDelivery.NotExpected && plan.Plan?.Steps.LastOrDefault() is { } lastStep)
            {
                plan.NoteStep(lastStep.Id, outcome);
            }
        }
        catch (Exception)
        {
            // Fail-soft (AC-216): the run already did its work; a finalization fault must not crash the settle.
        }
    }

    // The PR title for a merge-ready code run (AC-216) — the run's label (issue key + name), a clean human title. It also
    // becomes any leftover-work safety commit's message, so it carries no Co-Authored-By trailer and no AI/agent mention.
    private string _PullRequestTitle()
    {
        var label = plan.Plan?.Label;
        return string.IsNullOrWhiteSpace(label) ? "Autopilot run" : label.Trim();
    }

    // The PR body — the run's goal and, when it came from a tracker item, the source link so the PR points back at it.
    private string _PullRequestBody()
    {
        var current = plan.Plan;
        var goal = current?.Goal;
        var url = current?.Source?.Url;
        var body = string.IsNullOrWhiteSpace(goal) ? "Autopilot run." : goal.Trim();
        return string.IsNullOrWhiteSpace(url) ? body : $"{body}\n\n{url.Trim()}";
    }

    // Called on an MCP thread: a step agent's pane reports its work done. False when the pane is not a live step agent.
    public bool ReportStepDone(string paneId, string summary)
    {
        lock (_lock)
        {
            return _stepAgents.TryGetValue(paneId, out var signal) && signal.TrySetResult(summary);
        }
    }

    // Called on an MCP thread: the CEO pane reports its verdict. Gated to the run's CEO session, to a validation
    // actually pending, and to a running phase — so a CEO that in one turn both blocks and validates cannot resolve the
    // validation after the blockade has moved the run to AwaitingOperator.
    public bool ReportValidation(string paneId, bool passed, string? reason)
    {
        lock (_lock)
        {
            if (_validation is not { } validation
                || plan.Phase != AutopilotPlanPhase.Running
                || plan.SessionPaneId != paneId)
            {
                return false;
            }

            // Keep the CEO's reason so a failed step can show why it was not accepted, not just that it was not.
            _validationReason = reason;
            return validation.TrySetResult(passed);
        }
    }

    // Called on an MCP thread (AC-201, spoor 2): a live step worker consults the run's CEO before continuing,
    // instead of going straight to the operator. Two guarded fallbacks send it straight to the operator instead:
    // *fail-closed* (no live CEO) and *loop-cap* (consult budget spent). False when the gate turns it down.
    public async Task<bool> ReportConsultAsync(string workerPane, string question)
    {
        bool toCeo;
        string? ceoPane = null;
        AutopilotStep? step = null;
        lock (_lock)
        {
            if (!_stepAgents.ContainsKey(workerPane)
                || plan.Phase != AutopilotPlanPhase.Running
                || _consultPane is not null
                || _blockedPane is not null)
            {
                return false;
            }

            // Fail-closed: with no live CEO to consult (never embedded, or already ended) the consult cannot be answered,
            // so it falls back to the operator rather than being silently dropped.
            if (_ceoSession is not { } ceo || ceo.Completion.IsCompleted)
            {
                _blockedPane = workerPane;
                toCeo = false;
            }
            // Loop-cap: count this consult against the CALLING pane's own step (AC-434 — a review group runs more
            // than one worker at once, so `_paneStepIds` identifies the step, not "the" active step). Once that
            // step exceeds its budget, stop bouncing this worker off the CEO and put the question to the operator.
            else if (_BumpConsult(_paneStepIds[workerPane]) > _maxConsultsPerStep)
            {
                _blockedPane = workerPane;
                toCeo = false;
            }
            else
            {
                _consultPane = workerPane;
                ceoPane = plan.SessionPaneId;
                step = plan.Plan?.Steps.FirstOrDefault(candidate => candidate.Id == _paneStepIds[workerPane]);
                toCeo = true;
            }
        }

        if (toCeo)
        {
            // Relay the question into the CEO's session as a turn — the phase stays Running (a consult is not a blockade).
            await host.SendToSessionAsync(ceoPane!, AutopilotConsultBrief.ConsultTurn(step, question));
            return true;
        }

        // Fail-closed or loop-cap fallback: this is a real operator blockade — raise it (Changed for the surface) outside
        // the lock so the render is not dispatched while it is held.
        plan.Block(question);
        return true;
    }

    // Called on an MCP thread (AC-201): the CEO answers a worker's consult. Relays the answer as a turn into the
    // worker's session — the phase never changed, since a consult keeps the run Running. Gated to the run's CEO
    // session with a consult pending; false otherwise. A blank answer just clears the consult without relaying.
    public async Task<bool> AnswerWorkerAsync(string ceoPane, string answer)
    {
        string? workerPane;
        lock (_lock)
        {
            if (plan.SessionPaneId != ceoPane || _consultPane is null)
            {
                return false;
            }

            workerPane = _consultPane;
            _consultPane = null;
        }

        if (workerPane is { Length: > 0 } && !string.IsNullOrWhiteSpace(answer))
        {
            await host.SendToSessionAsync(workerPane, answer);
        }

        return true;
    }

    // Called on an MCP thread (AC-201, spoor 3): the CEO decides a worker's consult is genuinely an operator call
    // and escalates it. The worker (not the CEO) becomes the blocked pane, so the answer is later relayed to the
    // worker through `AnswerBlockadeAsync`. Gated to the run's CEO session with a consult pending; false otherwise.
    public bool EscalateToOperator(string ceoPane, string question)
    {
        lock (_lock)
        {
            if (plan.SessionPaneId != ceoPane || _consultPane is null)
            {
                return false;
            }

            // The worker awaiting the consult becomes the blocked pane so the operator's reply reaches the worker, not the CEO.
            _blockedPane = _consultPane;
            _consultPane = null;
        }

        plan.Block(question);
        return true;
    }

    // Counts a consult against a step's budget under _lock and returns the running total, so ReportConsultAsync can cap
    // how often one step may consult before the run falls back to the operator.
    private int _BumpConsult(string stepId)
    {
        _consultCounts.TryGetValue(stepId, out var count);
        count++;
        _consultCounts[stepId] = count;
        return count;
    }

    // The operator answered the blockade (AC-155): relay their reply to the blocked session as a turn — it carries on
    // in that same session and eventually reports done as usual — and resume the run.
    public async Task AnswerBlockadeAsync(string answer)
    {
        string? pane;
        lock (_lock)
        {
            // Only a run actually awaiting an answer resumes: a stale or double click (after a settle, or with no blockade
            // pending) must not shove a finished run back to Running with no driver behind it.
            if (plan.Phase != AutopilotPlanPhase.AwaitingOperator)
            {
                return;
            }

            pane = _blockedPane;
            _blockedPane = null;
        }

        if (pane is { Length: > 0 })
        {
            // The blocked worker already ended its turn when it raised the blockade, so even a blank operator
            // answer must send a turn — a minimal "carry on" nudge — rather than only resuming the phase.
            await host.SendToSessionAsync(pane, string.IsNullOrWhiteSpace(answer) ? "Continue." : answer);
        }

        // AC-347: this is the one place an operator answers a blockade — count it before resuming, so the reliability
        // figure never counts a resolved blockade as a correction.
        plan.RecordBlockadeAnswer();
        plan.ResumeRunning();
    }

    // The CEO moves the source issue this run came from to a tracker stage (AC-177) — its own stage name, so the
    // CEO picks the vocabulary. Only the run's CEO session, only for a source-triggered run: the plugin is the
    // sole tracker writer, step agents never touch it. False for any caller/run/tracker that doesn't qualify.
    public Task<bool> ReportTrackerStageAsync(string paneId, string stage, CancellationToken cancellationToken = default) =>
        _WithSourceTrackerAsync(paneId, (provider, source) => provider.SetStageAsync(source.IssueId, stage, cancellationToken));

    // The CEO posts a comment (evidence, a status note) on the source issue this run came from (AC-177). Same gates as
    // `ReportTrackerStageAsync`: CEO session only, source-run only, the plugin the sole writer.
    public Task<bool> ReportTrackerNoteAsync(string paneId, string note, CancellationToken cancellationToken = default) =>
        _WithSourceTrackerAsync(paneId, (provider, source) => provider.PostCommentAsync(source.IssueId, note, cancellationToken));

    // Resolves the run's source and its tracker plugin behind the CEO-only gate, then runs the tracker action outside the
    // lock (the provider call is I/O). The plugin is the only tracker access (AC-177): a caller that is not the run's CEO
    // session, a run with no source (CEO-first), or a tracker id no plugin backs, all yield false rather than an action.
    private async Task<bool> _WithSourceTrackerAsync(string paneId, Func<ITrackerProvider, AutopilotPlanSource, Task<bool>> action)
    {
        bool isRunCeo;
        lock (_lock)
        {
            isRunCeo = plan.SessionPaneId == paneId;
        }

        if (!isRunCeo || _ResolveSourceTracker() is not { } resolved)
        {
            return false;
        }

        return await action(resolved.Provider, resolved.Source);
    }

    // The run's source and the tracker plugin backing its tracker id, or null when the run has no source (CEO-first) or
    // no installed plugin backs it. The read of the plan's source is under _lock; the provider lookup is a pure host read.
    private (ITrackerProvider Provider, AutopilotPlanSource Source)? _ResolveSourceTracker()
    {
        AutopilotPlanSource? source;
        lock (_lock)
        {
            source = plan.Plan?.Source;
        }

        if (source is null)
        {
            return null;
        }

        var provider = host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, source.Tracker, StringComparison.OrdinalIgnoreCase));
        return provider is null ? null : (provider, source);
    }

    // AC-202: automatically move the run's source issue to `stage` on a lifecycle edge. *Idempotent* — never
    // twice; *fail-soft* — a tracker error never breaks the run. Maps through `SuggestStageName`, so no
    // tracker-specific name is hardcoded. A safety net beside the CEO's manual autopilot_tracker_stage.
    internal async Task AutoAdvanceTrackerStageAsync(TrackerWorkStage stage, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Idempotent: this run's auto-mapping already moved the issue to this stage.
            if (_lastAutoStage == stage)
            {
                return;
            }
        }

        if (_ResolveSourceTracker() is not { } resolved)
        {
            // A CEO-first run (no source) or no installed plugin backs its tracker — nothing to move.
            return;
        }

        try
        {
            var stageName = resolved.Provider.SuggestStageName(stage);
            if (string.IsNullOrWhiteSpace(stageName))
            {
                // The tracker maps this neutral stage to no column of its own — leave the issue where it is.
                return;
            }

            if (await resolved.Provider.SetStageAsync(resolved.Source.IssueId, stageName, cancellationToken))
            {
                // Remember the last stage that actually landed, so this edge is not retried and a later, different edge
                // still proceeds. A set that did not land (API down) is not remembered, so a subsequent edge can try again.
                lock (_lock)
                {
                    _lastAutoStage = stage;
                }
            }
        }
        catch (Exception)
        {
            // Fail-soft (AC-202): a tracker fault must never take the run down. Providers already degrade a failure to a
            // false return; this guards the run against anything they might still throw (a cancellation, an unexpected
            // error). The CEO's manual tracker tools remain as the fallback.
        }
    }

    // The operator chose to intervene in the running step (AC-174): hand them the keyboard by enabling the composer on
    // the live step session(s). A no-op between steps, when nothing is running.
    public void EnableCurrentStepInput()
    {
        IReadOnlyList<IEmbeddedSession> live;
        lock (_lock)
        {
            live = [.. _liveStepSessions];
        }

        foreach (var session in live)
        {
            session.SetInputEnabled(true);
        }
    }

    // One step: embed the agent session(s), await every agent's done-report, then have the CEO validate the
    // combined result. Only a real verdict returns Passed/Rejected; every other path returns Faulted —
    // reworked like Rejected, but never counted as a review finding (AC-347).
    private async Task<AutopilotStepOutcome> _ExecuteStepAsync(
        IWorkspaceContext context,
        AutopilotSettings settings,
        Action<Control> showStepSession,
        Action<bool> setValidating,
        AutopilotRunEnvironment environment,
        Func<Action, Task> runOnUi,
        AutopilotStep step,
        CancellationToken cancellationToken)
    {
        // Parallel agents are only safe when each gets its own worktree — a run that does not isolate (a non-git folder)
        // has no per-agent isolation, so N agents would race on the same files in the one working directory. Force a
        // single agent there; the split only makes sense with isolation.
        var agentCount = environment.IsolateSteps ? Math.Max(1, step.AgentCount) : 1;
        var sessions = new List<IEmbeddedSession>(agentCount);
        var reports = new List<Task<string>>(agentCount);

        // The shared run worktree is used only for a single-agent step. A parallel step keeps each agent in its
        // own isolated worktree (null → the host creates one per agent), so they never race on one directory.
        // AC-434: a review-gate step is never handed the shared worktree either — null forces a fresh one per gate.
        var stepWorktreePath = !step.IsReviewGate && agentCount == 1 ? environment.RunWorktreePath : null;

        // AC-434: that fresh throwaway worktree must fork from the run's own branch tip — where earlier rounds'
        // fix steps already landed — not from the stale base repository a plain fresh worktree would fork from.
        // Only a review-gate step needs this; every other step runs in (or forks from) the base as always.
        var stepWorkingDirectory = step.IsReviewGate && environment.RunWorktreePath is { Length: > 0 }
            ? environment.RunWorktreePath
            : environment.RepositoryDirectory;

        // A fresh attempt clears any note the previous one left, so a rework does not show a stale reason.
        plan.NoteStep(step.Id, string.Empty);

        // This attempt's fresh consult budget (AC-201) — _paneStepIds below is what a consult now looks the step up
        // through (AC-434), not a coordinator-wide "the active step".
        lock (_lock)
        {
            _consultCounts.Remove(step.Id);
        }

        try
        {
            // AC-210 embed-time safety net: the plan passed profile/model validation at emit, but an operator edit
            // can re-target a step afterwards, so re-check against the host's roster before embedding — a
            // mismatch throws a clear message instead of failing downstream with a misleading isolation error.
            var profiles = await host.GetProfilesAsync().ConfigureAwait(false) ?? [];
            if (profiles.Count > 0 && AutopilotPlanTools.ValidateStepProfile(step, profiles) is { } profileError)
            {
                throw new InvalidOperationException(profileError);
            }

            // AC-434: a review-gate step reads its own throwaway worktree forked from the run branch's latest
            // commit, so this ensures uncommitted work is committed first. Fail-soft, serialized against the
            // group's other concurrent gate.
            if (step.IsReviewGate && _prPublisher is not null && environment.RunWorktreePath is { Length: > 0 } runWorktree)
            {
                await _safetyCommitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _prPublisher.EnsureCommittedAsync(runWorktree, "Autopilot: work in progress before review", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort — see above.
                }
                finally
                {
                    _safetyCommitGate.Release();
                }
            }

            // AC-255: where the worktree stood before this step's agents touched it, so the harness can tell the
            // CEO what changed instead of taking the summary's word. AC-1037: the run's own worktree for every
            // step, not stepWorktreePath — that was null for step types writing on their own branch, so nothing was measured.
            var evidenceMark = await _MarkEvidenceAsync(environment.RunWorktreePath, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < agentCount; index++)
            {
                var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new EmbeddedSessionRequest
                {
                    // Every session this run starts is recorded under the run (AC-251), so the fresh session a step
                    // gets — the reason a run's spend is spread over many of them — adds back up to one figure.
                    RunId = environment.RunId,
                    RunLabel = environment.RunLabel,
                    ProfileId = step.ProfileLabel,
                    Model = step.Model,
                    McpServers = _StepMcpServers(step),
                    // Pre-authorize the step worker's own control tools (AC-215) — report-done and consult-CEO — so an
                    // autonomous step never stops mid-run to ask the operator to allow autopilot_step_done.
                    PreApprovedTools = AutopilotRunToolNames.ForStepWorker,
                    // "Worktree is the boundary": an autonomous step must run its real work tools with no one to
                    // answer a prompt, so it auto-allows every tool. Its throwaway worktree is the containment,
                    // not the per-call gate — the operator accepts a run can reach outside it, bounded to the run.
                    PreApproveAllTools = true,
                    // Isolate each step in a worktree for a git repository (the fail-closed default). False only
                    // for a folder the host reported is not a git repository: an admin task with no repo runs
                    // directly in the working directory instead of being refused for "no git repository".
                    IsolateInWorktree = environment.IsolateSteps,
                    // The run's shared worktree for a single-agent isolated step, so work accumulates on one
                    // branch. A parallel step keeps each agent isolated (null → a fresh worktree per agent).
                    WorktreePath = stepWorktreePath,
                    // A non-isolated step (a non-git folder) has no worktree, so confine its file tools to the
                    // working directory instead — a local model without an OS sandbox is held to the operator's
                    // chosen folder. An isolated step does not set this — its worktree is the confinement.
                    ConfineFileToolsToWorkingDirectory = !environment.IsolateSteps,
                    PermissionMode = settings.AutonomyMode(),
                    WorkingDirectory = stepWorkingDirectory,
                    InitialUserMessage = AutopilotStepBrief.For(step, agentCount, index + 1),
                    // The step agent drives itself; start its composer off so the operator does not type into it, until
                    // they deliberately intervene (EnableCurrentStepInput). The brief still submits — it is host-driven.
                    StartWithInputDisabled = true,
                };

                var showThisOne = index == 0;
                IEmbeddedSession? embedded = null;
                await runOnUi(() =>
                {
                    var session = context.EmbedSession(request);
                    embedded = session;
                    if (showThisOne)
                    {
                        showStepSession(session.View);
                    }
                });

                if (embedded is not { } agent)
                {
                    // The host refused to embed this agent's session — no CEO ever saw this step's work. Faulted, not a
                    // rejection.
                    return AutopilotStepOutcome.Faulted;
                }

                lock (_lock)
                {
                    _stepAgents[agent.PaneId] = signal;
                    _liveStepSessions.Add(agent);
                    // AC-434: which step this pane belongs to — a review group runs more than one worker pane at
                    // once, so a consult from this pane charges its own step, not "the" active one.
                    _paneStepIds[agent.PaneId] = step.Id;
                }

                sessions.Add(agent);
                reports.Add(_AwaitStepReportOrEndAsync(signal.Task, agent, cancellationToken));
            }

            var summaries = await Task.WhenAll(reports);

            // AC-1037: whatever this step committed in a worktree of its own, brought back onto the run's branch
            // before anyone judges the step — and said out loud in the validation turn either way.
            var strayCommits = await _RecoverStrayCommitsAsync(environment, sessions, cancellationToken).ConfigureAwait(false);

            // AC-255: the harness's own account of what the step changed, collected before the CEO is asked anything so
            // the turn carries it. Null — nothing observable — hands the CEO the inspection instruction it always had.
            var evidence = await _CollectEvidenceAsync(environment.RunWorktreePath, evidenceMark, step, summaries, cancellationToken).ConfigureAwait(false);

            // The agent(s) reported done, but the step is not settled until the CEO validates it — that window used to
            // read as a plain "Running…" with no sign the work was already done (the model says it's finished, but the
            // status still shows running). Say so on the block, so the operator sees the run has moved on to validation.
            plan.NoteStep(step.Id, "Work reported — the CEO is validating it against the acceptance…");

            // AC-434: only one step's session may be mid-validation with the CEO at a time (see _validationGate's
            // doc) — a review group's gates queue here one at a time, after running concurrently above. Released
            // in the inner finally below, not the outer one, so a later gate's validation is never nulled by this cleanup.
            await _validationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool passed;
            try
            {
                // Swap the surface to the CEO session for the validation window so it is clear the CEO is now
                // reviewing the step, not the finished worker still sitting there.
                setValidating(true);

                var validation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_lock)
                {
                    _validation = validation;
                }

                // AC-253: replaced on the way into the next turn, not straight after the one that filled the
                // interval — a run's last step would otherwise embed a fresh validator nothing is ever asked of.
                await _MaybeCheckpointCeoAsync();
                var ceo = _CurrentCeo() ?? throw new InvalidOperationException("The run has no live CEO session to validate this step.");

                await host.SendToSessionAsync(ceo.PaneId, AutopilotStepBrief.ValidationTurn(step, summaries, evidence, strayCommits));
                passed = await _AwaitValidationOrCeoEndAsync(validation.Task, ceo, cancellationToken);
                lock (_lock)
                {
                    _validationsSinceCheckpoint++;
                }

                if (!passed)
                {
                    // The CEO turned the step down; show its reason on the block so a failed step explains itself.
                    string? reason;
                    lock (_lock)
                    {
                        reason = _validationReason;
                    }

                    plan.NoteStep(step.Id, string.IsNullOrWhiteSpace(reason)
                        ? "The CEO did not accept this step against its acceptance."
                        : $"CEO: {reason.Trim()}");
                }
            }
            finally
            {
                // The release is its own innermost finally so a throwing setValidating cannot leak the gate — every
                // other queued gate would hang behind it until cancellation (an adversarial-pass find). Clearing
                // _validation and releasing happen together, so a later gate's fresh _validation is never nulled (AC-434).
                try
                {
                    lock (_lock)
                    {
                        _validation = null;
                    }

                    setValidating(false);
                }
                finally
                {
                    _validationGate.Release();
                }
            }

            // The only path that actually reached the CEO's verdict: a genuine acceptance or a genuine rejection.
            return passed ? AutopilotStepOutcome.Passed : AutopilotStepOutcome.Rejected;
        }
        catch (OperationCanceledException)
        {
            // The run was cancelled (the surface closed) — nothing to explain on a step that is going away, and no
            // verdict was ever reached.
            return AutopilotStepOutcome.Faulted;
        }
        catch (Exception failure)
        {
            // A step whose execution threw is a failed attempt that never reached a verdict — a refused isolation,
            // a stalled agent, a profile/model mismatch, or a CEO ending before validating all throw here. Record
            // why so the block shows it instead of a silent red dot.
            plan.NoteStep(step.Id, failure.Message);
            return AutopilotStepOutcome.Faulted;
        }
        finally
        {
            lock (_lock)
            {
                foreach (var session in sessions)
                {
                    _stepAgents.Remove(session.PaneId);
                    _liveStepSessions.Remove(session);
                    _paneStepIds.Remove(session.PaneId);
                }

                // A blockade raised by this step's agent cannot outlive the step; drop it so the next step starts clean.
                if (_blockedPane is not null && sessions.Any(session => session.PaneId == _blockedPane))
                {
                    _blockedPane = null;
                }

                // Likewise a consult raised by this step's own worker (AC-201) cannot outlive the step.
                if (_consultPane is not null && sessions.Any(session => session.PaneId == _consultPane))
                {
                    _consultPane = null;
                }

                _consultCounts.Remove(step.Id);
            }

            await runOnUi(() =>
            {
                foreach (var session in sessions)
                {
                    _ = session.CloseAsync();
                }
            });
        }
    }

    // AC-1037: brings work a step committed in a worktree of its own back onto the run's branch, and returns what
    // the CEO has to be told. Silence means the work is where it belongs — never "nobody looked", so a check that
    // could not run says so rather than returning nothing.
    private async Task<IReadOnlyList<string>> _RecoverStrayCommitsAsync(
        AutopilotRunEnvironment environment,
        IReadOnlyList<IEmbeddedSession> sessions,
        CancellationToken cancellationToken)
    {
        if (_prPublisher is null
            || environment.RunWorktreePath is not { Length: > 0 } runWorktree
            || environment.RunWorktreeBranch is not { Length: > 0 } runBranch)
        {
            return [];
        }

        var notes = new List<string>();
        foreach (var session in sessions)
        {
            if (session.WorktreePath is not { Length: > 0 } stepWorktree)
            {
                continue;
            }

            try
            {
                var stray = await _prPublisher.RecoverStrayCommitsAsync(runWorktree, runBranch, stepWorktree, cancellationToken).ConfigureAwait(false);
                if (stray.NeedsSaying)
                {
                    notes.Add(_DescribeStray(stray, runBranch, stepWorktree));
                }
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                // A git fault must not fail a step that did its work, but an unchecked step is not a clean one, so
                // a throw lands where a reported failure lands.
                notes.Add(_DescribeStray(AutopilotStrayCommits.Unmeasured(failure.Message), runBranch, stepWorktree));
            }
        }

        return notes;
    }

    // One line per session for the validation turn. A stranded commit is stated as what it is — the step's own report
    // describes a tree the run does not have — because that is the sentence the CEO has to act on.
    private static string _DescribeStray(AutopilotStrayCommits stray, string runBranch, string stepWorktree)
    {
        if (!stray.Found)
        {
            // Nothing was found because nothing could be looked at. Said as a failed check, never as a clean one.
            return $"The harness could not check whether this step's work landed on “{runBranch}” "
                + $"({stray.Error}) — the step ran in {stepWorktree}, so check that branch yourself before accepting it.";
        }

        var recovered = stray.Recovered.Count == 0
            ? string.Empty
            : $"Cherry-picked {stray.Recovered.Count} commit(s) it had made on a branch of its own onto “{runBranch}” "
                + $"({string.Join(", ", stray.Recovered.Select(_Short))}) — that work is in the run now and the observation below covers it. ";

        return stray.Stranded.Count == 0
            ? recovered.TrimEnd()
            : recovered
                + $"{stray.Stranded.Count} further commit(s) could NOT be brought over ({string.Join(", ", stray.Stranded.Select(_Short))}): "
                + $"{stray.Error ?? "the cherry-pick was refused"}. They are still only in {stepWorktree}, so anything the "
                + "step reports about them — a passing suite, a finished fix — describes a tree this run does not have. "
                + "Do not accept the step on that report.";
    }

    private static string _Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    // AC-255: where the shared run worktree stood before a step ran. Null whenever there is nothing to measure against
    // — no source, no shared worktree, or git could not answer — and the step then validates the way it always did.
    private async Task<AutopilotWorktreeMark?> _MarkEvidenceAsync(string? worktreePath, CancellationToken cancellationToken)
    {
        if (_evidenceSource is null || worktreePath is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return await _evidenceSource.MarkAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The source's contract is that it never throws, but this gate exists to make validation cheaper — it may
            // never be the reason a step fails. Same posture as the leftover-work safety commit above.
            return null;
        }
    }

    // AC-255: the harness's account of what the step changed, or null when it has none — which is the whole opt-in
    // rule in one place: evidence is offered only where the harness could actually observe, never assumed.
    private async Task<AutopilotStepEvidence?> _CollectEvidenceAsync(
        string? worktreePath,
        AutopilotWorktreeMark? mark,
        AutopilotStep step,
        IReadOnlyList<string> summaries,
        CancellationToken cancellationToken)
    {
        if (_evidenceSource is null || worktreePath is not { Length: > 0 } || mark is null)
        {
            return null;
        }

        try
        {
            var change = await _evidenceSource.CollectAsync(worktreePath, mark, cancellationToken).ConfigureAwait(false);
            return change is null ? null : AutopilotStepEvidence.From(change, step, summaries);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Wait for a step agent to report its work done, but no longer than its session lives — a session that ends
    // first must not leave this waiting forever; it throws and _ExecuteStepAsync's catch reworks or fails it.
    private static readonly TimeSpan StepDoneReminderDelay = TimeSpan.FromSeconds(45);

    // The hard stall deadline (AC-192): a local model that keeps ending its turn with a text tool-call (never
    // actually calling it) used to hang the run indefinitely. Five minutes is deliberately generous so a
    // genuinely long turn is not cut off early, while still guaranteeing the run settles.
    private static readonly TimeSpan StepStallTimeout = TimeSpan.FromMinutes(5);

    private async Task<string> _AwaitStepReportOrEndAsync(Task<string> report, IEmbeddedSession agent, CancellationToken cancellationToken)
    {
        var ended = (Task)agent.Completion;

        // First wait: the report, the session ending, or the reminder window elapsing. The delay carries no token so it
        // cannot throw an unobserved cancellation once the other two have settled — cancellation rides WaitAsync instead.
        var firstWinner = await Task.WhenAny(report, ended, Task.Delay(_stepDoneReminderDelay)).WaitAsync(cancellationToken);
        if (firstWinner == report)
        {
            return await report;
        }

        if (firstWinner != ended)
        {
            // The window elapsed with no report and the session is still live: nudge the agent once to call the
            // tool, then keep waiting — but no longer than the hard stall deadline.
            await host.SendToSessionAsync(agent.PaneId, AutopilotStepBrief.StepDoneReminder());

            // Measure the stall deadline from the agent's last real tool progress, not the reminder: a step
            // working hard keeps resetting it through agent.Activity, so only true stalls (AC-192) hit it.
            if (await _StalledWithoutProgressAsync(report, ended, agent, cancellationToken))
            {
                // No report, no end, and no tool progress for the stall window: the agent is stuck. Fail the step so the
                // driver reworks or gives up, instead of hanging the run forever.
                throw new InvalidOperationException(
                    $"The step agent made no tool progress and did not report its work done within {_stepStallTimeout.TotalMinutes:0.#} minutes — treating the step as stalled.");
            }

            if (report.IsCompleted)
            {
                return await report;
            }
        }

        // The session ended before the agent reported done. Its Completion carries the reason when the host ended it
        // itself (refused to isolate, failed to start); surface that so the failed step explains itself.
        var reason = await agent.Completion;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
            ? "The step agent's session ended before it reported its work done."
            : reason);
    }

    // The validator this run is talking to right now — the one it started with, or whichever checkpoint replaced it.
    private IEmbeddedSession? _CurrentCeo()
    {
        lock (_lock)
        {
            return _ceoSession;
        }
    }

    // AC-253: swaps the validator for a fresh session once it has taken on `_ceoCheckpointEverySteps` validation turns,
    // so the diffs those turns carried stop being re-read on every later one. Called from inside the validation gate —
    // the one place in a run where exactly one step is talking to the CEO (AC-434).
    private async Task _MaybeCheckpointCeoAsync()
    {
        if (_checkpointCeo is not { } checkpointCeo)
        {
            return;
        }

        AutopilotPlan? current;
        lock (_lock)
        {
            // A worker mid-consult (AC-201) is waiting on an answer from this exact session, and replacing it would
            // leave it waiting for one that never comes. Defer to the next validation rather than force the swap.
            if (!AutopilotCeoCheckpoint.IsDue(_validationsSinceCheckpoint, _ceoCheckpointEverySteps) || _consultPane is not null)
            {
                return;
            }

            current = plan.Plan;
        }

        if (current is null)
        {
            return;
        }

        try
        {
            if (await checkpointCeo(AutopilotCeoCheckpoint.CarryOver(current)).ConfigureAwait(false) is not { } fresh)
            {
                // The host refused to embed a replacement; the live validator stays and the run carries on with it.
                return;
            }

            lock (_lock)
            {
                _ceoSession = fresh;
                _validationsSinceCheckpoint = 0;
            }

            // Without this the fresh pane's own autopilot_validate is turned down by ReportValidation's run gate, and
            // the very next step would hang until its CEO-end race fired.
            plan.BindSession(fresh.PaneId);
        }
        catch (Exception)
        {
            // A checkpoint is a saving, never a condition: a failed swap leaves the run on the validator it has.
        }
    }

    // The CEO's verdict, or its session ending first — the validation counterpart of _AwaitStepReportOrEndAsync.
    // Reachable, not theoretical: a CEO provider that doesn't vouch file confinement is refused at start
    // (AC-191), and waiting on the verdict alone there would hang. Fails with the host's own reason.
    private static async Task<bool> _AwaitValidationOrCeoEndAsync(Task<bool> validation, IEmbeddedSession ceo, CancellationToken cancellationToken)
    {
        await Task.WhenAny(validation, ceo.Completion).WaitAsync(cancellationToken);

        // A verdict that did arrive wins the race: the CEO may end immediately after answering, and its answer is the
        // real outcome — reading the ending first would throw away a validation the operator's run already earned.
        if (validation.IsCompleted)
        {
            return await validation;
        }

        var reason = await ceo.Completion;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
            ? "The CEO's session ended before it validated this step."
            : reason);
    }

    // Waits for the step's done-report or its session ending, returning true only when the agent makes no tool
    // progress for the whole stall window. Each agent.Activity restarts the window, so genuine (if slow) work is
    // never failed; a stuck agent that only emits text (AC-192) times out. False when the report or end wins first.
    private async Task<bool> _StalledWithoutProgressAsync(Task report, Task ended, IEmbeddedSession agent, CancellationToken cancellationToken)
    {
        while (!report.IsCompleted && !ended.IsCompleted)
        {
            // A fresh signal each round, completed by the next tool progress — subscribed before the race so an event
            // between rounds still resolves it — so the stall delay below is measured from the most recent activity.
            var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnActivity() => progressed.TrySetResult();
            agent.Activity += OnActivity;
            // A fresh linked source per round so this round's stall timer is torn down the moment another task
            // wins, rather than left ticking — an active step re-enters this loop on every tool event, and an
            // uncancelled Task.Delay per round would pile up orphaned timers.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var winner = await Task.WhenAny(report, ended, progressed.Task, Task.Delay(_stepStallTimeout, stall.Token)).WaitAsync(cancellationToken);
                if (winner == report || winner == ended)
                {
                    return false;
                }

                if (winner != progressed.Task)
                {
                    // The stall delay won: no report, no end, no tool progress for the whole window — stalled.
                    return true;
                }

                // Tool progress: loop, restarting the stall window from now.
            }
            finally
            {
                agent.Activity -= OnActivity;
                stall.Cancel();
            }
        }

        return false;
    }

    // The MCP set a step agent is launched with (AC-117): the step's own report endpoint, plus whatever minimal
    // set the CEO scoped it to. When the CEO scoped nothing, the step gets ONLY its report endpoint — never the
    // CEO's own endpoint (validate/tracker tools) — keeping the step least-privilege.
    private static IReadOnlyList<string> _StepMcpServers(AutopilotStep step)
    {
        if (step.McpServers.Count == 0)
        {
            return [AutopilotRunTools.EndpointName];
        }

        return step.McpServers.Contains(AutopilotRunTools.EndpointName)
            ? step.McpServers
            : [.. step.McpServers, AutopilotRunTools.EndpointName];
    }
}
