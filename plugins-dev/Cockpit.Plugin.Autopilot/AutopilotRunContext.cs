using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
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
    private IEmbeddedSession? _ceo;

    public AutopilotRunContext(ICockpitHost host, IWorkspaceContext context, AutopilotSettings settings, AutopilotPlan plan, Func<Action, Task> runOnUi)
    {
        _host = host;
        _context = context;
        _settings = settings;
        _runOnUi = runOnUi;

        Plan = plan;
        Controller = new AutopilotPlanController();
        Controller.BeginPlanning(plan);
        Coordinator = new AutopilotRunCoordinator(host, Controller);
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

    /// <summary>Stops the run — its workspace closed, or the operator dropped it.</summary>
    public void Cancel() => _cts.Cancel();

    private async Task _RunAsync(AutopilotPlan plan)
    {
        // Re-raise the controller's own change (a step started/settled/noted) as this run's Changed so the surface
        // re-renders this run's pipeline; dropped with the run since the controller is not shared.
        void OnControllerChanged(object? sender, EventArgs e) => Changed?.Invoke();
        Controller.Changed += OnControllerChanged;

        try
        {
            // One worktree for the whole run (AC-174, Raymond 2026-07-22): every step runs in it so their work
            // accumulates on one branch — the merge-ready deliverable — instead of a throwaway worktree per step. Created
            // once here off the run's repo. Null when the repo is not a git repository (or no worktree manager): the
            // steps then fall back to per-step isolation, which the fail-closed gate refuses on a non-confining provider.
            var repositoryDirectory = AutopilotWorkingDirectory.Resolve(_context);
            PluginWorktreeInfo? runWorktree = null;
            try
            {
                runWorktree = await _host.CreateRunWorktreeAsync(repositoryDirectory, "autopilot", _cts.Token);
            }
            catch (Exception)
            {
                // A worktree that could not be created leaves the run to fall back to per-step isolation, not crash.
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
                    WorkingDirectory = runWorktree?.Path ?? repositoryDirectory,
                    // Confine the validator's file tools to the run worktree when there is one (Raymond 2026-07-22): a
                    // Claude/Codex CEO confines natively and ignores this, but a local-model CEO would otherwise reach
                    // the operator's home — so a local CEO is held to the worktree it validates, like the steps. Only
                    // when the worktree exists: with none, WorkingDirectory is the real checkout and must not be confined
                    // to (the run's steps fail to isolate anyway).
                    ConfineFileToolsToWorkingDirectory = runWorktree is not null,
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

            await Coordinator.RunAsync(_context, ceo, _settings, _ShowStepView, _SetValidating, runWorktree?.Path, _runOnUi, _cts.Token);
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
