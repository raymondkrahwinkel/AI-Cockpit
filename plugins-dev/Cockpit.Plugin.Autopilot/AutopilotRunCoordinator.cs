using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Drives an approved CEO plan to completion (AC-174) — the integration adapter behind <see cref="AutopilotRunDriver"/>'s
/// executeStep seam. Per step it embeds a session on the step's profile/model/minimal-MCP with the step brief as its
/// opening turn, shows it on the surface, waits for the agent to report done (<c>autopilot_step_done</c>), asks the
/// still-live CEO to validate the result against the step's acceptance (<c>autopilot_validate</c>), and returns the
/// pass/fail the driver reworks or advances on. Parallel agents (<see cref="AutopilotStep.AgentCount"/> &gt; 1) run as
/// isolated sessions; the step passes only when every agent reports and the CEO validates the combined result.
/// <para>
/// The MCP tools (<see cref="AutopilotRunTools"/>) call <see cref="ReportStepDone"/>/<see cref="ReportValidation"/> on
/// background MCP-call threads while a step awaits; the state that couples them is guarded by <see cref="_lock"/>. UI
/// work (embedding a session, showing it, closing it) is marshalled onto the UI thread through the <c>runOnUi</c>
/// delegate the caller supplies, since the driver loop does not run on the UI thread.
/// </para>
/// </summary>
internal sealed class AutopilotRunCoordinator(
    ICockpitHost host,
    AutopilotPlanController plan,
    TimeSpan? stepDoneReminderDelay = null,
    TimeSpan? stepStallTimeout = null,
    IAutopilotPrPublisher? prPublisher = null)
{
    private readonly Lock _lock = new();

    // Publishes a merge-ready code run's branch and opens its PR (AC-216); null in a bare test graph, where finalization
    // is skipped. The app supplies the real GitCliPrPublisher through AutopilotRunContext.
    private readonly IAutopilotPrPublisher? _prPublisher = prPublisher;

    // Injectable for tests (short values keep the stall test fast); production uses the defaults below.
    private readonly TimeSpan _stepDoneReminderDelay = stepDoneReminderDelay ?? StepDoneReminderDelay;
    private readonly TimeSpan _stepStallTimeout = stepStallTimeout ?? StepStallTimeout;
    private readonly Dictionary<string, TaskCompletionSource<string>> _stepAgents = new(StringComparer.Ordinal);
    private readonly List<IEmbeddedSession> _liveStepSessions = [];
    private TaskCompletionSource<bool>? _validation;
    private string? _validationReason;
    private string? _blockedPane;

    // AC-201 tiered escalation state, all guarded by _lock. A worker's autopilot_blocked now consults the run's CEO
    // first (spoor 2) instead of going straight to the operator: _consultPane is the worker awaiting the CEO's answer
    // (at most one at a time), _ceoSession is the live CEO the consult is relayed to and the fail-closed check reads,
    // _activeStepId names the running step whose consult budget _consultCounts tracks, and _maxConsultsPerStep caps how
    // often one step may consult before the run falls back to the operator.
    private string? _consultPane;
    private IEmbeddedSession? _ceoSession;
    private string? _activeStepId;
    private readonly Dictionary<string, int> _consultCounts = new(StringComparer.Ordinal);
    private int _maxConsultsPerStep;

    // AC-202: the last stage this run's automatic phase→stage mapping set on the source issue, so a lifecycle edge does
    // not set the same stage twice (idempotent). Guarded by _lock. The CEO's manual autopilot_tracker_stage does not
    // touch this — the auto-mapping fires only on the two lifecycle edges (start, merge-ready), far from the CEO's own
    // mid-run stage/notes, so it is a safety net that never immediately reverts a stage the CEO set by hand.
    private TrackerWorkStage? _lastAutoStage;

    /// <summary>
    /// Runs the approved plan to a settled end. Builds the bounded run-driver on the run's rework cap and feeds it the
    /// executeStep adapter; returns when the run settles (merge-ready or blocked) or <paramref name="cancellationToken"/>
    /// cancels it (the surface closed).
    /// </summary>
    /// <param name="showStepSession">Places a started step session's view on the surface; invoked inside <paramref name="runOnUi"/>.</param>
    /// <param name="runOnUi">Runs an action on the UI thread — session embedding and teardown touch Avalonia controls.</param>
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
        }

        // AC-202: the run has just started (the plan is Running after its single approval). Move the source issue to the
        // in-progress stage automatically — a source-triggered run must not sit on Backlog waiting for the CEO to move it
        // by hand (AC-195 did exactly that). A CEO-first run has no issue, so this is a no-op there.
        await AutoAdvanceTrackerStageAsync(TrackerWorkStage.InProgress, cancellationToken);

        // AC-215 preflight: a code run (the template asked for a PR) that will not be able to deliver one — a plain-folder
        // run, no git remote, no gh — is flagged up front, so the operator learns it now rather than at the silent end.
        await _PreflightPullRequestAsync(environment, runOnUi, cancellationToken);

        var driver = new AutopilotRunDriver(plan, settings.MaxSelfFixAttempts());
        await driver.RunAsync(
            step => _ExecuteStepAsync(context, ceo, settings, showStepSession, setValidating, environment, runOnUi, step, cancellationToken),
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

                var result = await _prPublisher.PublishAsync(request, createPullRequest: delivery == AutopilotPrDelivery.CanCreatePr, cancellationToken);
                prUrl = result.PrUrl;
                error = result.Error;
            }

            var outcome = AutopilotMergeReadyDecision.Outcome(delivery, environment.RunWorktreeBranch, environment.RunWorktreePath, prUrl);
            if (!string.IsNullOrWhiteSpace(error))
            {
                outcome = $"{outcome} ({error})";
            }

            // Surface the outcome so a code run that could not produce its PR is never a silent "done": a toast the
            // operator sees now, and a note on the last step so it persists in the run's pipeline/afronding.
            var clean = delivery == AutopilotPrDelivery.NotExpected || (!string.IsNullOrWhiteSpace(prUrl) && string.IsNullOrWhiteSpace(error));
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

    /// <summary>Called on an MCP thread: a step agent's pane reports its work done. False when the pane is not a live step agent.</summary>
    public bool ReportStepDone(string paneId, string summary)
    {
        lock (_lock)
        {
            return _stepAgents.TryGetValue(paneId, out var signal) && signal.TrySetResult(summary);
        }
    }

    /// <summary>Called on an MCP thread: the CEO pane reports its verdict. Gated to the run's CEO session, to a validation
    /// actually pending, and to a running phase — so a CEO that in one turn both blocks and validates cannot resolve the
    /// validation after the blockade has moved the run to AwaitingOperator.</summary>
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

    /// <summary>
    /// Called on an MCP thread (AC-201, spoor 2): a live step worker consults the run's CEO before it continues instead
    /// of going straight to the operator. The worker's question is relayed as a turn into the CEO's own session, which
    /// answers it (<see cref="AnswerWorkerAsync"/>) or escalates it (<see cref="EscalateToOperator"/>). Two guarded
    /// fallbacks send it straight to the operator instead: <em>fail-closed</em> when there is no live CEO to consult, and
    /// the <em>loop-cap</em> when this step has already spent its consult budget (a worker stuck asking in circles).
    /// Gated to a live step worker of a running run with no other consult or blockade already open. False when the gate
    /// turns it down; true whether it went to the CEO or fell back to the operator.
    /// </summary>
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
            // Loop-cap: count this consult against the running step's budget; once a step exceeds it, stop bouncing the
            // worker off the CEO and put the question to the operator instead.
            else if (_activeStepId is { } stepId && _BumpConsult(stepId) > _maxConsultsPerStep)
            {
                _blockedPane = workerPane;
                toCeo = false;
            }
            else
            {
                _consultPane = workerPane;
                ceoPane = plan.SessionPaneId;
                step = plan.ActiveStep;
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

    /// <summary>
    /// Called on an MCP thread (AC-201): the CEO answers a worker's consult. Relays the answer as a turn into the
    /// worker's session — it carries on there and eventually reports done as usual — and clears the pending consult. The
    /// phase never changed (a consult keeps the run Running), so nothing here moves it. Gated to the run's CEO session
    /// with a consult actually pending; false otherwise. A blank answer just clears the consult without relaying.
    /// </summary>
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

    /// <summary>
    /// Called on an MCP thread (AC-201, spoor 3): the CEO decides a worker's consult is genuinely an operator call and
    /// escalates it. The worker (not the CEO) becomes the blocked pane, so the operator's answer is later relayed to the
    /// worker through the unchanged <see cref="AnswerBlockadeAsync"/>. Gated to the run's CEO session with a consult
    /// pending; false otherwise.
    /// </summary>
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

    /// <summary>
    /// The operator answered the blockade (AC-155): relay their reply to the blocked session as a turn — it carries on
    /// in that same session and eventually reports done as usual — and resume the run.
    /// </summary>
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
            // The blocked worker already ended its turn when it raised the blockade; without a new turn it would just sit
            // until the stall deadline. So even a blank operator answer must send a turn — a minimal "carry on" nudge —
            // rather than only resuming the phase, so the worker actually continues.
            await host.SendToSessionAsync(pane, string.IsNullOrWhiteSpace(answer) ? "Continue." : answer);
        }

        plan.ResumeRunning();
    }

    /// <summary>
    /// The CEO moves the source issue this run came from to a tracker stage (AC-177) — its own stage name, so the CEO
    /// picks the vocabulary. Only from the run's CEO session and only for a source-triggered run (a CEO-first run has no
    /// issue): the plugin is the sole tracker writer, step agents never touch it. False when the caller is not the CEO,
    /// the run has no source, or no tracker plugin backs its tracker id.
    /// </summary>
    public Task<bool> ReportTrackerStageAsync(string paneId, string stage, CancellationToken cancellationToken = default) =>
        _WithSourceTrackerAsync(paneId, (provider, source) => provider.SetStageAsync(source.IssueId, stage, cancellationToken));

    /// <summary>
    /// The CEO posts a comment (evidence, a status note) on the source issue this run came from (AC-177). Same gates as
    /// <see cref="ReportTrackerStageAsync"/>: CEO session only, source-run only, the plugin the sole writer.
    /// </summary>
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

    /// <summary>
    /// AC-202: automatically move the run's source issue to <paramref name="stage"/> as the run crosses a lifecycle edge
    /// (started, merge-ready). Only for a source-triggered run — a CEO-first run has no issue; <em>idempotent</em> — it
    /// never sets the same stage twice; and <em>fail-soft</em> — a tracker error (API down, no permission, cancellation)
    /// never breaks the run. The neutral stage is mapped to the tracker's own vocabulary through
    /// <see cref="ITrackerProvider.SuggestStageName"/>, so no tracker-specific stage name is hardcoded here; a tracker
    /// that maps the stage to null is left untouched. Internal (not private) so a focused test can exercise the
    /// idempotency guard directly. This is a safety net beside the CEO's manual autopilot_tracker_stage, not a
    /// replacement — see <see cref="_lastAutoStage"/> on why it does not fight the CEO's own mid-run stage.
    /// </summary>
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

    /// <summary>
    /// The operator chose to intervene in the running step (AC-174): hand them the keyboard by enabling the composer on
    /// the live step session(s). A no-op between steps, when nothing is running.
    /// </summary>
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

    // One step: embed the agent session(s) on the step's profile/model/minimal-MCP with the brief as their opening turn,
    // show the first on the surface, await every agent's done-report, then have the CEO validate the combined result. A
    // cancelled or thrown step is a failed attempt (false) — the driver reworks or gives up — never a crashed run.
    private async Task<bool> _ExecuteStepAsync(
        IWorkspaceContext context,
        IEmbeddedSession ceo,
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

        // The shared run worktree is used only for a single-agent step, so its work accumulates on the run's branch. A
        // parallel step (AgentCount > 1) keeps each agent in its own isolated worktree (WorktreePath left null → the host
        // creates one per agent), so they do not race on one directory's files and git index — the same isolation the
        // parallel path had before the run-worktree change, and what this coordinator's contract promises. Null too when
        // the run does not isolate (a non-git folder), where a step runs directly in the working directory.
        var stepWorktreePath = agentCount == 1 ? environment.RunWorktreePath : null;

        // A fresh attempt clears any note the previous one left, so a rework does not show a stale reason.
        plan.NoteStep(step.Id, string.Empty);

        // This is the running step a consult belongs to (AC-201), with a fresh consult budget for the attempt.
        lock (_lock)
        {
            _activeStepId = step.Id;
            _consultCounts.Remove(step.Id);
        }

        try
        {
            // AC-210 embed-time safety net: the plan passed profile/model validation at emit, but an operator edit can
            // re-target a step's profile/model afterwards, so re-check this step against the host's roster before embedding
            // it. A mismatch throws a clear model↔profile message the catch below records on the step — instead of embedding
            // a session that is refused downstream and failing the step with a misleading isolation error. With no roster
            // to check against (a host that supplies none) this is a no-op; the plan-time gate stays the primary guard.
            var profiles = await host.GetProfilesAsync().ConfigureAwait(false) ?? [];
            if (profiles.Count > 0 && AutopilotPlanTools.ValidateStepProfile(step, profiles) is { } profileError)
            {
                throw new InvalidOperationException(profileError);
            }

            for (var index = 0; index < agentCount; index++)
            {
                var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new EmbeddedSessionRequest
                {
                    ProfileId = step.ProfileLabel,
                    Model = step.Model,
                    McpServers = _StepMcpServers(step),
                    // Pre-authorize the step worker's own control tools (AC-215) — report-done and consult-CEO — so an
                    // autonomous step never stops mid-run to ask the operator to allow autopilot_step_done.
                    PreApprovedTools = AutopilotRunToolNames.ForStepWorker,
                    // "Worktree is the boundary" (Raymond 2026-07-23): an autonomous step must run its real work tools
                    // (Bash to build/test/grep, edits, git) with no one to answer a prompt, so it auto-allows every tool.
                    // The step's isolation in a throwaway worktree is the containment, not the per-call gate — the
                    // operator accepts a run can reach outside its worktree (prompt-injection), bounded to the run.
                    PreApproveAllTools = true,
                    // Isolate each step in a worktree for a git repository (the fail-closed default — the confinement
                    // guarantee holds, and a non-confining provider is refused by the host gate). False only for a
                    // folder the host reported is not a git repository (Raymond 2026-07-22): an admin task with no repo
                    // runs directly in the working directory instead of being refused for "no git repository".
                    IsolateInWorktree = environment.IsolateSteps,
                    // The run's shared worktree for a single-agent isolated step (Raymond 2026-07-22): steps run in it so
                    // their work accumulates on one branch. A parallel step keeps each agent isolated (stepWorktreePath
                    // null → a fresh worktree per agent). Null when the run does not isolate.
                    WorktreePath = stepWorktreePath,
                    // A non-isolated step (a non-git folder) has no worktree to hold it, so confine its file tools to the
                    // working directory instead — least-privilege (security review): a local model without an OS sandbox
                    // is held to the operator's chosen folder rather than reaching their home and up. An isolated step
                    // does not set this — its worktree is the confinement, and this would point at the base repo, not it.
                    ConfineFileToolsToWorkingDirectory = !environment.IsolateSteps,
                    PermissionMode = settings.AutonomyMode(),
                    WorkingDirectory = environment.RepositoryDirectory,
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
                    return false;
                }

                lock (_lock)
                {
                    _stepAgents[agent.PaneId] = signal;
                    _liveStepSessions.Add(agent);
                }

                sessions.Add(agent);
                reports.Add(_AwaitStepReportOrEndAsync(signal.Task, agent, cancellationToken));
            }

            var summaries = await Task.WhenAll(reports);

            // The agent(s) reported done, but the step is not settled until the CEO validates it — that window used to
            // read as a plain "Running…" with no sign the work was already done (Raymond: "haiku zegt klaar, maar de
            // status is nog running"). Say so on the block, so the operator sees the run has moved on to validation.
            plan.NoteStep(step.Id, "Work reported — the CEO is validating it against the acceptance…");

            // Swap the surface to the CEO session for the validation window so it is clear the CEO is now reviewing the
            // step, not the finished worker still sitting there (Raymond 2026-07-22). Cleared in the finally, whatever
            // the outcome.
            setValidating(true);

            var validation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _validation = validation;
            }

            await host.SendToSessionAsync(ceo.PaneId, AutopilotStepBrief.ValidationTurn(step, summaries));
            var passed = await validation.Task.WaitAsync(cancellationToken);
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

            return passed;
        }
        catch (OperationCanceledException)
        {
            // The run was cancelled (the surface closed) — nothing to explain on a step that is going away.
            return false;
        }
        catch (Exception failure)
        {
            // A step whose execution threw is a failed attempt (the driver reworks or gives up). Record why — a session
            // the host refused to isolate carries its reason here — so the block shows it instead of a silent red dot.
            plan.NoteStep(step.Id, failure.Message);
            return false;
        }
        finally
        {
            // The validation window is over (passed, failed, threw or cancelled) — return the surface to the step view.
            setValidating(false);

            lock (_lock)
            {
                foreach (var session in sessions)
                {
                    _stepAgents.Remove(session.PaneId);
                    _liveStepSessions.Remove(session);
                }

                _validation = null;

                // A blockade raised by this step's agent cannot outlive the step; drop it so the next step starts clean.
                if (_blockedPane is not null && sessions.Any(session => session.PaneId == _blockedPane))
                {
                    _blockedPane = null;
                }

                // Likewise a consult raised by this step's own worker (AC-201) cannot outlive the step. Clear the active
                // step and its consult budget so the next step (or the next attempt) starts with a clean tier.
                if (_consultPane is not null && sessions.Any(session => session.PaneId == _consultPane))
                {
                    _consultPane = null;
                }

                if (_activeStepId == step.Id)
                {
                    _activeStepId = null;
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

    // Wait for a step agent to report its work done, but no longer than its session lives. The agent reports through
    // its MCP tool (ReportStepDone completes signal); a session that ends first — the host refused to isolate it on a
    // provider that cannot confine file access (AC-174), or it died — must not leave this waiting forever. The session
    // ending before it reports is a failed attempt: this throws, and _ExecuteStepAsync's catch turns it into the
    // rework-or-fail the driver already handles, never a hung run. Cancellation (the surface closed) is honoured
    // through WaitAsync; the losing task is left to complete on its own and neither faults, so nothing is unobserved.
    // How long a step agent may go quiet — no done-report, session still live — before it gets one reminder to call the
    // tool (Raymond 2026-07-22). A weaker/local model sometimes does the work but ends its turn with a text summary
    // instead of calling autopilot_step_done, which would otherwise leave the step waiting forever. One nudge is enough:
    // a model still working just gets a harmless "call it when you finish", a model that already finished gets the tool
    // call it forgot. Deliberately generous, so a legitimately long turn is not nagged early.
    private static readonly TimeSpan StepDoneReminderDelay = TimeSpan.FromSeconds(45);

    // The hard stall deadline (AC-192): how long after the single nudge a step agent may still go quiet — no
    // done-report, session still live — before the step is failed as stalled rather than waited on forever. Before
    // this, one nudge was the only push and then the wait was unbounded, so a local model that keeps ending its turn
    // with a text tool-call (never calling autopilot_step_done, and never actually running the tool) hung the whole
    // run indefinitely. Five minutes is deliberately generous — a genuinely long turn (a big build, a slow tool) is
    // not cut off early — while still guaranteeing the run eventually settles instead of hanging. The failed step is
    // a normal failed attempt the driver reworks or gives up on.
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
            // The window elapsed with no report and the session is still live: nudge the agent once to call the tool,
            // then keep waiting — but no longer than the hard stall deadline. A message sent mid-turn queues harmlessly
            // and takes effect when the turn ends — which is exactly when a finished-but-silent agent needs it.
            await host.SendToSessionAsync(agent.PaneId, AutopilotStepBrief.StepDoneReminder());

            // Wait for the report or the session ending, but measure the stall deadline from the agent's last real tool
            // progress rather than from the reminder (Raymond 2026-07-23): a step that is slow because it is working
            // hard — a long single turn with many edits and a build — keeps resetting that deadline through
            // agent.Activity, so it is never failed for being slow. Only an agent that makes no tool progress at all for
            // the whole window (AC-192 — a turn that emits text describing a tool it never runs) hits the deadline.
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

    // Waits for the step's done-report or its session ending, returning true only when the agent makes no tool progress
    // for the whole stall window (Raymond 2026-07-23). Each agent.Activity — a tool call surfacing or a tool result
    // landing — restarts the window, so a step that is genuinely working (however slowly) is never failed; a stuck one
    // that only emits text (AC-192) makes no tool events and times out. Returns false when the report landed or the
    // session ended first — the caller reads which. The stall delay carries no token so it cannot throw an unobserved
    // cancellation once another task wins; cancellation rides WaitAsync, mirroring the reminder wait above.
    private async Task<bool> _StalledWithoutProgressAsync(Task report, Task ended, IEmbeddedSession agent, CancellationToken cancellationToken)
    {
        while (!report.IsCompleted && !ended.IsCompleted)
        {
            // A fresh signal each round, completed by the next tool progress — subscribed before the race so an event
            // between rounds still resolves it — so the stall delay below is measured from the most recent activity.
            var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnActivity() => progressed.TrySetResult();
            agent.Activity += OnActivity;
            // A fresh linked source per round so this round's stall timer is torn down the moment another task wins,
            // rather than left ticking for the full timeout — an active step re-enters this loop on every tool event,
            // and an uncancelled Task.Delay per round would pile up orphaned timers for the whole step. A delay that
            // loses cancels here (a canceled task is not a fault, so nothing is left unobserved); cancellation of the
            // outer token still rides WaitAsync.
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

    // The MCP set a step agent is launched with (AC-117, Raymond 2026-07-22): the step's own report endpoint, plus
    // whatever minimal set the CEO scoped it to. When the CEO scoped nothing, the step gets ONLY its report endpoint —
    // not the host's whole selection, which would hand it the CEO's own endpoint (its validate/tracker tools) and every
    // other server. Restricting to the report endpoint keeps the step least-privilege and, for a local model, keeps its
    // confined set to the step endpoint alone (the CEO endpoint is never selected, so a step never gains the CEO tools).
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
