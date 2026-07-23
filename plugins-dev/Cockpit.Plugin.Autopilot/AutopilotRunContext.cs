using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// One running Autopilot run and its surface state (AC-174): its own plan controller and coordinator, the CEO validator
/// session it embeds, and the live step view to show. Created per dequeued plan by the workspace body; it runs the plan
/// to a settled end — embedding a fresh CEO validator (the planning round is long closed) and, through the coordinator,
/// each step's agent — and raises <see cref="Changed"/> as its pipeline or step view moves so the surface re-renders it.
/// Several can run at once: each is independent, and its coordinator self-gates every tool call on its own panes.
/// </summary>
internal sealed class AutopilotRunContext
{
    private readonly ICockpitHost _host;
    private readonly IWorkspaceContext _context;
    private readonly AutopilotSettings _settings;
    private readonly Func<Action, Task> _runOnUi;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _phaseLock = new();
    private AutopilotPlanPhase _lastPhase = AutopilotPlanPhase.Planning;
    private IEmbeddedSession? _ceo;

    // The MCP surface the run's validator CEO is scoped to (AC-197): only the CEO endpoint that hosts its own tools —
    // autopilot_validate plus autopilot_tracker_stage / autopilot_tracker_note. Left on the request's default empty list
    // it would inherit the host's whole selection (161 tools observed): every tool definition in its context, and it
    // would still leave the tracker-stage flow to chance. Mounting AutopilotCeoTools.EndpointName explicitly guarantees
    // the validate and tracker tools are present — the exact endpoint the validator/tracker brief tells it to call.
    internal static readonly IReadOnlyList<string> ValidatorCeoMcpServers = [AutopilotCeoTools.EndpointName];

    public AutopilotRunContext(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlan plan, Func<Action, Task> runOnUi)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _runOnUi = runOnUi;

        Plan = plan;
        Controller = new AutopilotPlanController();
        Controller.BeginPlanning(plan);
        Coordinator = new AutopilotRunCoordinator(host, Controller, prPublisher: new GitCliPrPublisher());
        Completed = _RunAsync(plan);
    }

    /// <summary>The plan this run drives — its goal is the run's label on the surface.</summary>
    public AutopilotPlan Plan { get; }

    /// <summary>The run's plan controller and where each step sits — what the surface renders as this run's pipeline.</summary>
    public AutopilotPlanController Controller { get; }

    /// <summary>The run's coordinator — how a tool call routes to it, and how the operator answers its blockade or hands it the keyboard.</summary>
    public AutopilotRunCoordinator Coordinator { get; }

    /// <summary>Completes when the run settles or is cancelled — what the manager awaits to free the slot.</summary>
    public Task Completed { get; }

    /// <summary>The running step's live view, or null between steps.</summary>
    public Control? StepView { get; private set; }

    /// <summary>The CEO validator's live session view — shown in place of the step while the CEO validates a finished step.</summary>
    public Control? CeoView => _ceo?.View;

    /// <summary>Whether the CEO is validating a just-finished step right now (Raymond 2026-07-22): the surface swaps the
    /// right pane to the CEO session and a clear banner while this is true, so the validation is not a small side note.</summary>
    public bool IsValidating { get; private set; }

    /// <summary>Raised on this run's pipeline change or step-view change, so the surface re-renders it.</summary>
    public event Action? Changed;

    /// <summary>Stops the run — its workspace closed, or the operator dropped it. Guards against a run that already
    /// settled and disposed its token source in the window before the surface dropped it from its active list.</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already settled and disposed its CTS; there is nothing left to cancel.
        }
    }

    private async Task _RunAsync(AutopilotPlan plan)
    {
        // Re-raise the controller's own change (a step started/settled/noted) as this run's Changed so the surface
        // re-renders this run's pipeline; dropped with the run since the controller is not shared. It is also where the
        // "needs you" toast fires on the edge into AwaitingOperator (AC-194).
        void OnControllerChanged(object? sender, EventArgs e)
        {
            _MaybeNotifyAwaiting();
            Changed?.Invoke();
        }

        Controller.Changed += OnControllerChanged;

        try
        {
            var repositoryDirectory = AutopilotWorkingDirectory.Resolve(_context, plan.WorkingDirectory);

            // Whether the run isolates each step in a worktree (AC-174, Raymond 2026-07-22). A git repository isolates —
            // the confinement guarantee holds. Only a folder the host positively reports is NOT a git repository runs
            // without isolation (an admin task with no repo, Raymond's choice); Unknown (an older host, a failed probe)
            // stays isolated, fail-closed, so the guard is never dropped silently. This keeps "not a git repo → run
            // free" apart from "a git repo whose worktree could not be created", which stays isolated (refused downstream).
            var status = await _host.DetectGitDirectoryStatusAsync(repositoryDirectory, _cts.Token);
            var isolateSteps = AutopilotRunEnvironment.IsolateFor(status);

            // Record the chosen folder in the shared quick-pick (here and in the New-session dialog), but only once its
            // status resolved to a real directory — a path that could not be resolved (Unknown: missing, unreadable) is
            // not remembered, so a mistyped path does not pollute the operator's recents.
            if (status != GitDirectoryStatus.Unknown)
            {
                await _host.RememberWorkingPathAsync(repositoryDirectory, _cts.Token);
            }

            // One worktree for the whole run when it isolates (AC-174, Raymond 2026-07-22): every step runs in it so their
            // work accumulates on one branch — the merge-ready deliverable — instead of a throwaway worktree per step.
            // Null when the run does not isolate (a plain folder), or when the worktree could not be created (a git-repo
            // run then falls back to per-step isolation, which the fail-closed gate still guards).
            PluginWorktreeInfo? runWorktree = null;
            if (isolateSteps)
            {
                try
                {
                    runWorktree = await _host.CreateRunWorktreeAsync(repositoryDirectory, "autopilot", _cts.Token);
                }
                catch (Exception)
                {
                    // A worktree that could not be created leaves the run to fall back to per-step isolation, not crash.
                }
            }

            // A fresh CEO validator for this run (the planning round is closed): embedded on the CEO profile and briefed
            // to validate. Pointed at the run's worktree so it can actually inspect the accumulated work — not only the
            // step's summary — when validating; the main checkout when there is no run worktree.
            IEmbeddedSession? ceo = null;
            await _runOnUi(() =>
            {
                ceo = _context.EmbedSession(new EmbeddedSessionRequest
                {
                    ProfileId = _settings.CeoProfileLabel(),
                    Model = _settings.CeoModel(),
                    McpServers = ValidatorCeoMcpServers,
                    // Pre-authorize the CEO's own control tools (AC-215) so validating a step never stops mid-run to ask
                    // the operator to allow autopilot_validate — an autonomous run must not need a hand on its own tools.
                    PreApprovedTools = AutopilotRunToolNames.ForValidatorCeo,
                    // "Worktree is the boundary" (Raymond 2026-07-23): the validating CEO runs autonomously too — it may
                    // read the diff and run the tests (Bash) to check the work against acceptance — so it auto-allows
                    // every tool rather than stall on a prompt, contained by the run's worktree.
                    PreApproveAllTools = true,
                    WorkingDirectory = runWorktree?.Path ?? repositoryDirectory,
                    // Confine the validator's file tools to whatever directory it is pointed at (Raymond 2026-07-22): the
                    // run worktree when there is one, else the run's folder (a non-git run, or a git run whose worktree
                    // could not be created). A Claude/Codex CEO confines natively and ignores this; a local-model CEO
                    // would otherwise reach the operator's home, so it is held to the folder it validates — least
                    // privilege in every case, never wider than the run's own directory.
                    ConfineFileToolsToWorkingDirectory = true,
                    AppendSystemPrompt = AutopilotValidatorBrief.For(plan),
                });
            });

            if (ceo is null)
            {
                return;
            }

            _ceo = ceo;
            Controller.BindSession(ceo.PaneId);
            Controller.Approve();

            var environment = new AutopilotRunEnvironment(repositoryDirectory, runWorktree?.Path, isolateSteps, runWorktree?.Branch);
            await Coordinator.RunAsync(_context, ceo, _settings, _ShowStepView, _SetValidating, environment, _runOnUi, _cts.Token);
        }
        catch (Exception)
        {
            // A failed or cancelled run must not crash the surface; the pipeline shows its settled or blocked state.
        }
        finally
        {
            Controller.Changed -= OnControllerChanged;
            await _runOnUi(() =>
            {
                StepView = null;
                if (_ceo is { } settled)
                {
                    _ceo = null;
                    _ = settled.CloseAsync();
                }

                _cts.Dispose();
                Changed?.Invoke();
            });
        }
    }

    // A run entered the AwaitingOperator wait (AC-155/AC-194): tell the operator once, since they may be working
    // elsewhere in the app while the run sits blocked on their answer. OnControllerChanged fires on every re-render, so
    // an unguarded toast would repeat on each render while the run waits — the previous-phase edge guard keeps it to one
    // toast per time the run enters the wait. Marshalled to the UI thread since Changed is raised from MCP-call/driver
    // threads too.
    private void _MaybeNotifyAwaiting()
    {
        var current = Controller.Phase;
        bool entered;
        lock (_phaseLock)
        {
            entered = ShouldToastAwaiting(_lastPhase, current);
            _lastPhase = current;
        }

        if (!entered)
        {
            return;
        }

        var label = Controller.Plan?.Label is { Length: > 0 } text ? text : "Autopilot run";
        var question = Controller.PendingQuestion;
        var message = string.IsNullOrWhiteSpace(question)
            ? $"Run “{label}” needs you."
            : $"Run “{label}” needs you — {question}";
        _ = _runOnUi(() => _host.ShowToast(message, PluginToastSeverity.Warning));
    }

    // The phase edge that warrants a "needs you" toast: only the transition INTO AwaitingOperator, never a re-render
    // while already there. Pure so the edge guard is unit-testable without a host or a UI thread.
    internal static bool ShouldToastAwaiting(AutopilotPlanPhase previous, AutopilotPlanPhase current) =>
        previous != AutopilotPlanPhase.AwaitingOperator && current == AutopilotPlanPhase.AwaitingOperator;

    private void _ShowStepView(Control view)
    {
        StepView = view;
        Changed?.Invoke();
    }

    // The coordinator flips this around the CEO's validation of a finished step, so the surface swaps to the CEO session
    // for that window. Raises Changed so the pane re-renders; the body marshals the render onto the UI thread.
    private void _SetValidating(bool validating)
    {
        IsValidating = validating;
        Changed?.Invoke();
    }
}
