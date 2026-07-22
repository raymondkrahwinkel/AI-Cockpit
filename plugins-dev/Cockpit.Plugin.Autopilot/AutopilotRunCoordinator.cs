using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
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
internal sealed class AutopilotRunCoordinator(ICockpitHost host, AutopilotPlanController plan)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<string>> _stepAgents = new(StringComparer.Ordinal);
    private readonly List<IEmbeddedSession> _liveStepSessions = [];
    private TaskCompletionSource<bool>? _validation;
    private string? _validationReason;
    private string? _blockedPane;

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
        var driver = new AutopilotRunDriver(plan, settings.MaxSelfFixAttempts());
        await driver.RunAsync(
            step => _ExecuteStepAsync(context, ceo, settings, showStepSession, setValidating, environment, runOnUi, step, cancellationToken),
            cancellationToken);
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
    /// Called on an MCP thread (AC-155): a live step agent or the CEO raises a blockade the operator must answer. Moves
    /// the run to <see cref="AutopilotPlanPhase.AwaitingOperator"/> and remembers the pane to relay the answer to. Gated
    /// to a session that is actually part of this run and to a run that is currently running — a second, concurrent
    /// blockade is turned down (only one is answered at a time).
    /// </summary>
    public bool ReportBlocked(string paneId, string question)
    {
        lock (_lock)
        {
            if (_blockedPane is not null || plan.Phase != AutopilotPlanPhase.Running)
            {
                return false;
            }

            if (!_stepAgents.ContainsKey(paneId) && plan.SessionPaneId != paneId)
            {
                return false;
            }

            _blockedPane = paneId;
        }

        // Raises Changed for the surface (which shows the question and an answer box); done outside the lock so the
        // render is not dispatched while it is held.
        plan.Block(question);
        return true;
    }

    /// <summary>
    /// The operator answered the blockade (AC-155): relay their reply to the blocked session as a turn — it carries on
    /// in that same session and eventually reports done as usual — and resume the run. A blank answer just resumes.
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

        if (pane is { Length: > 0 } && !string.IsNullOrWhiteSpace(answer))
        {
            await host.SendToSessionAsync(pane, answer);
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
        AutopilotPlanSource? source;
        lock (_lock)
        {
            source = plan.SessionPaneId == paneId ? plan.Plan?.Source : null;
        }

        if (source is null)
        {
            return false;
        }

        var provider = host.TrackerProviders.FirstOrDefault(candidate => string.Equals(candidate.TrackerId, source.Tracker, StringComparison.OrdinalIgnoreCase));
        return provider is not null && await action(provider, source);
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

        try
        {
            for (var index = 0; index < agentCount; index++)
            {
                var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new EmbeddedSessionRequest
                {
                    ProfileId = step.ProfileLabel,
                    Model = step.Model,
                    McpServers = _StepMcpServers(step),
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

    private async Task<string> _AwaitStepReportOrEndAsync(Task<string> report, IEmbeddedSession agent, CancellationToken cancellationToken)
    {
        var ended = (Task)agent.Completion;

        // First wait: the report, the session ending, or the reminder window elapsing. The delay carries no token so it
        // cannot throw an unobserved cancellation once the other two have settled — cancellation rides WaitAsync instead.
        var firstWinner = await Task.WhenAny(report, ended, Task.Delay(StepDoneReminderDelay)).WaitAsync(cancellationToken);
        if (firstWinner == report)
        {
            return await report;
        }

        if (firstWinner != ended)
        {
            // The window elapsed with no report and the session is still live: nudge the agent once to call the tool,
            // then keep waiting (report or end) with no further reminders. A message sent mid-turn queues harmlessly and
            // takes effect when the turn ends — which is exactly when a finished-but-silent agent needs it.
            await host.SendToSessionAsync(agent.PaneId, AutopilotStepBrief.StepDoneReminder());
            if (await Task.WhenAny(report, ended).WaitAsync(cancellationToken) == report)
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
