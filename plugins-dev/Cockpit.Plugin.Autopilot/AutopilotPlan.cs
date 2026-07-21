namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The plan the CEO builds and the operator approves once (AC-174). During the planning round it is a <em>living
/// artifact</em> — the CEO rebuilds it from the conversation and the operator tweaks a step's profile/model — and it
/// freezes on approval, after which the autonomous run drives it step by step. <see cref="Source"/> is null for a
/// CEO-first plan (started with only the CEO, no supplied item). The model is task-type-agnostic: the same shape holds
/// a coding plan (code → … → PR) and a future non-coding plan (AC-158), because a step is just a step.
/// </summary>
/// <param name="Goal">What the run is to achieve — drafted from the source, or defined with the CEO in a CEO-first run.</param>
internal sealed record AutopilotPlan(
    string Goal,
    AutopilotPlanSource? Source,
    IReadOnlyList<AutopilotStep> Steps)
{
    /// <summary>An empty plan to open the planning round on — the CEO fills the steps from there.</summary>
    public static AutopilotPlan Empty(AutopilotPlanSource? source, string goal) => new(goal, source, []);

    /// <summary>The step running now, or null when none is (before start, between steps, or once the run has settled).</summary>
    public AutopilotStep? Active => Steps.FirstOrDefault(step => step.Status == AutopilotStepStatus.Running);

    /// <summary>The next step still to run, or null when every step has settled — how the run picks what to start next.</summary>
    public AutopilotStep? NextPending => Steps.FirstOrDefault(step => step.Status == AutopilotStepStatus.Pending);

    /// <summary>Replaces the step sharing <paramref name="step"/>'s id, returning a new plan — a single living-artifact edit.</summary>
    public AutopilotPlan WithStep(AutopilotStep step) =>
        this with { Steps = [.. Steps.Select(existing => existing.Id == step.Id ? step : existing)] };

    /// <summary>Replaces the whole step list — how the CEO re-emits a revised plan while the operator is still shaping it.</summary>
    public AutopilotPlan WithSteps(IReadOnlyList<AutopilotStep> steps) => this with { Steps = steps };
}
