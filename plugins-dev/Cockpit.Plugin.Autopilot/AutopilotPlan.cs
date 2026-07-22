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
    /// <summary>
    /// A short operator-facing label for the run (Raymond 2026-07-22): the CEO proposes it while planning and the
    /// operator can override it, but it must be non-empty before the run is approved — so a queue of several runs is
    /// recognisable, and history reads by name rather than by goal sentence. Falls back to <see cref="Goal"/> when unset.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The run's display label — its <see cref="Name"/>, or the <see cref="Goal"/> when no name was set yet.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Goal : Name;

    /// <summary>
    /// The best available name to pre-fill the approval field with (Raymond 2026-07-22): the CEO's proposed
    /// <see cref="Name"/> when it gave one, else the <see cref="Goal"/>, else the first step's title. The CEO does not
    /// always pass a name (or even a goal) through the plan tool, so this always yields something concrete for the
    /// operator to accept or edit rather than leaving the field — and the approval gate — empty on a planned run.
    /// </summary>
    public string SuggestedName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Goal) ? Goal
        : Steps.FirstOrDefault()?.Title ?? string.Empty;

    /// <summary>This plan with a run name — the CEO's proposal, or the operator's override at approval.</summary>
    public AutopilotPlan WithName(string name) => this with { Name = name };

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
