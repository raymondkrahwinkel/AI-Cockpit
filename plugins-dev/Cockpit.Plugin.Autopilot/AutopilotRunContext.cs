using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// One running Autopilot run and its surface state (AC-174): its own plan controller and coordinator, the CEO validator
// session it embeds, and the live step view to show. Runs the plan to a settled end, raising `Changed` as its
// pipeline or step view moves. Several can run at once: each is independent and self-gates its own panes.
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

    // Where a checkpointed validator is re-embedded (AC-253) — the run's worktree, else its folder. Resolved in
    // `_RunAsync` before the first CEO exists, so the coordinator can never call back here while it is still blank.
    private string _ceoDirectory = string.Empty;

    // The MCP surface the run's validator CEO is scoped to (AC-197): only the CEO endpoint that hosts its own tools.
    // Left on the request's default empty list it would inherit the host's whole selection instead — mounting
    // AutopilotCeoTools.EndpointName explicitly guarantees the validate and tracker tools are present.
    internal static readonly IReadOnlyList<string> ValidatorCeoMcpServers = [AutopilotCeoTools.EndpointName];

    // The embed request for a run's validating CEO — a pure static so the shape it asks the host for can be exercised
    // without a host or a UI thread. Pointed at `workingDirectory`: the run's worktree when it has one, else the folder
    // it runs in. `carryOver` is the ledger a checkpointed replacement starts with (AC-253), null for the first.
    internal static EmbeddedSessionRequest ValidatorCeoRequest(
        AutopilotSettings settings,
        string workingDirectory,
        AutopilotPlan plan,
        string runId,
        string? carryOver = null) =>
        new()
        {
            // The validating CEO spends on the run's behalf just as its steps do, so it is recorded against the same
            // run (AC-251) — leaving it out would under-report exactly the context whose growth this run is measured on.
            RunId = runId,
            RunLabel = plan.Label,
            // AC-254: the validator's own profile/model, not planning's — it defaults to the planning pair until an
            // operator sets a validation-specific override (AutopilotSettings.CeoValidationProfileLabel/Model).
            ProfileId = settings.CeoValidationProfileLabel(),
            Model = settings.CeoValidationModel(),
            McpServers = ValidatorCeoMcpServers,
            // Pre-authorize the CEO's own control tools (AC-215) so validating a step never stops mid-run to ask
            // the operator to allow autopilot_validate — an autonomous run must not need a hand on its own tools.
            PreApprovedTools = AutopilotRunToolNames.ForValidatorCeo,
            // "Worktree is the boundary": the validating CEO runs autonomously too — it may
            // read the diff and run the tests (Bash) to check the work against acceptance — so it auto-allows
            // every tool rather than stall on a prompt, contained by the run's worktree.
            PreApproveAllTools = true,
            WorkingDirectory = workingDirectory,
            // Confine the validator's file tools to whatever directory it is pointed at. A Claude/Codex CEO
            // confines natively and ignores this; a local-model CEO would otherwise reach the operator's home,
            // so it is held to least privilege in every case, never wider than the run's own directory.
            ConfineFileToolsToWorkingDirectory = true,
            // The run's autonomy mode, for the same reason a step worker carries it (AC-209): it is coerced away from
            // bypassPermissions, else a profile saved on bypassPermissions reaches the driver and the host's
            // fail-closed gate refuses the confinement this request asks for, leaving the validator stuck (AC-191).
            PermissionMode = settings.AutonomyMode(),
            // The ledger rides in the hidden brief rather than as an opening turn: it is this session's standing
            // context, and a turn would cost the very round trip the checkpoint is there to save.
            AppendSystemPrompt = string.IsNullOrWhiteSpace(carryOver)
                ? AutopilotValidatorBrief.For(plan)
                : $"{AutopilotValidatorBrief.For(plan)}\n\n{carryOver}",
        };

    public AutopilotRunContext(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlan plan, Func<Action, Task> runOnUi)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _runOnUi = runOnUi;

        Plan = plan;
        Controller = new AutopilotPlanController();
        Controller.BeginPlanning(plan);
        Coordinator = new AutopilotRunCoordinator(
            host,
            Controller,
            prPublisher: new GitCliPrPublisher(),
            evidenceSource: new GitCliEvidenceSource(),
            checkpointCeo: _CheckpointCeoAsync);
        Completed = _RunAsync(plan);
    }

    // The plan this run drives — its goal is the run's label on the surface.
    public AutopilotPlan Plan { get; }

    // What ties this one run's sessions together in the host's usage trail (AC-251), so "what did this run cost"
    // is a sum rather than an estimate. Minted per run and never reused; planning is not part of it, having
    // happened before there was a run.
    public string RunId { get; } = Guid.NewGuid().ToString("n");

    // The run's plan controller and where each step sits — what the surface renders as this run's pipeline.
    public AutopilotPlanController Controller { get; }

    // The run's coordinator — how a tool call routes to it, and how the operator answers its blockade or hands it the keyboard.
    public AutopilotRunCoordinator Coordinator { get; }

    // Completes when the run settles or is cancelled — what the manager awaits to free the slot.
    public Task Completed { get; }

    // The running step's live view, or null between steps.
    public Control? StepView { get; private set; }

    // The CEO validator's live session view — shown in place of the step while the CEO validates a finished step.
    public Control? CeoView => _ceo?.View;

    // Whether the CEO is validating a just-finished step right now: the surface swaps the
    // right pane to the CEO session and a clear banner while this is true, so the validation is not a small side note.
    public bool IsValidating { get; private set; }

    // Raised on this run's pipeline change or step-view change, so the surface re-renders it.
    public event Action? Changed;

    // Stops the run — its workspace closed, or the operator dropped it. Guards against a run that already
    // settled and disposed its token source in the window before the surface dropped it from its active list.
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

            // Whether the run isolates each step in a worktree (AC-174). Only a folder the host positively reports
            // is NOT a git repository runs without isolation; Unknown (older host, failed probe) stays isolated,
            // fail-closed, so the guard is never dropped silently.
            var status = await _host.DetectGitDirectoryStatusAsync(repositoryDirectory, _cts.Token);
            var isolateSteps = AutopilotRunEnvironment.IsolateFor(status);

            // Record the chosen folder in the shared quick-pick (here and in the New-session dialog), but only once its
            // status resolved to a real directory — a path that could not be resolved (Unknown: missing, unreadable) is
            // not remembered, so a mistyped path does not pollute the operator's recents.
            if (status != GitDirectoryStatus.Unknown)
            {
                await _host.RememberWorkingPathAsync(repositoryDirectory, _cts.Token);
            }

            // One worktree for the whole run when it isolates (AC-174): every step runs in it so their work
            // accumulates on one branch — the merge-ready deliverable — instead of a throwaway worktree per step.
            // Null when the run does not isolate, or the worktree could not be created (falls back per-step).
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
            _ceoDirectory = runWorktree?.Path ?? repositoryDirectory;
            IEmbeddedSession? ceo = null;
            await _runOnUi(() =>
            {
                ceo = _context.EmbedSession(ValidatorCeoRequest(_settings, _ceoDirectory, plan, RunId));
            });

            if (ceo is null)
            {
                return;
            }

            _ceo = ceo;
            Controller.BindSession(ceo.PaneId);
            Controller.Approve();

            var environment = new AutopilotRunEnvironment(repositoryDirectory, runWorktree?.Path, isolateSteps, runWorktree?.Branch, RunId, plan.Label);
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
    // elsewhere while the run sits blocked. OnControllerChanged fires on every re-render, so the previous-phase edge
    // guard keeps it to one toast per wait; marshalled to the UI thread since Changed fires from other threads too.
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

    // AC-253: replaces the run's validator with a fresh session briefed on `carryOver`, so the growing tail of earlier
    // steps' diffs leaves its context. The new session is embedded before the old one is closed, and the run keeps the
    // one it has when the host refuses — a checkpoint is a saving, never a condition for the run to continue.
    private async Task<IEmbeddedSession?> _CheckpointCeoAsync(string carryOver)
    {
        IEmbeddedSession? fresh = null;
        await _runOnUi(() =>
        {
            fresh = _context.EmbedSession(ValidatorCeoRequest(_settings, _ceoDirectory, Plan, RunId, carryOver));
            if (fresh is null)
            {
                return;
            }

            var previous = _ceo;
            _ceo = fresh;
            _ = previous?.CloseAsync();
            Changed?.Invoke();
        });

        return fresh;
    }

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
