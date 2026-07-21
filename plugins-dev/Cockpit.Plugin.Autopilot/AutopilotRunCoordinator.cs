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
        Func<Action, Task> runOnUi,
        CancellationToken cancellationToken)
    {
        var driver = new AutopilotRunDriver(plan, settings.MaxSelfFixAttempts());
        await driver.RunAsync(
            step => _ExecuteStepAsync(context, ceo, settings, showStepSession, runOnUi, step, cancellationToken),
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
            return _validation is { } validation
                && plan.Phase == AutopilotPlanPhase.Running
                && plan.SessionPaneId == paneId
                && validation.TrySetResult(passed);
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
        Func<Action, Task> runOnUi,
        AutopilotStep step,
        CancellationToken cancellationToken)
    {
        var agentCount = Math.Max(1, step.AgentCount);
        var sessions = new List<IEmbeddedSession>(agentCount);
        var reports = new List<Task<string>>(agentCount);

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
                    IsolateInWorktree = true,
                    PermissionMode = settings.AutonomyMode(),
                    WorkingDirectory = context.Sessions.ActiveSessionWorkingDirectory,
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

            var validation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _validation = validation;
            }

            await host.SendToSessionAsync(ceo.PaneId, AutopilotStepBrief.ValidationTurn(step, summaries));
            return await validation.Task.WaitAsync(cancellationToken);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
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
    private static async Task<string> _AwaitStepReportOrEndAsync(Task<string> report, IEmbeddedSession agent, CancellationToken cancellationToken)
    {
        var winner = await Task.WhenAny(report, agent.Completion).WaitAsync(cancellationToken);
        if (winner == report)
        {
            return await report;
        }

        throw new InvalidOperationException("the step agent's session ended before it reported its work done.");
    }

    // The step agent must be able to report done, so its endpoint stays in reach even when the CEO scoped the step to a
    // minimal MCP set (AC-117). An empty set keeps the host's usual selection, which already carries the endpoint.
    private static IReadOnlyList<string> _StepMcpServers(AutopilotStep step)
    {
        if (step.McpServers.Count == 0)
        {
            return [];
        }

        return step.McpServers.Contains(AutopilotRunTools.EndpointName)
            ? step.McpServers
            : [.. step.McpServers, AutopilotRunTools.EndpointName];
    }
}
