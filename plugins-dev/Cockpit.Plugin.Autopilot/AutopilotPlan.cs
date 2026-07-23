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

    /// <summary>
    /// The folder the run works in (AC-174), chosen by the operator at approval — the run resolves its repository from
    /// here, so a run planned from a tracker issue (which has no session, hence no directory) still knows where to work.
    /// Blank falls back to the active session's directory, then the cockpit's own. A folder that is a git repository is
    /// isolated per step; a plain folder (an admin task) runs without isolation.
    /// </summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Whether this run is a <em>code</em> run that must end with a merge-ready pull request (AC-216) — set at approval
    /// from the chosen template's <see cref="AutopilotTemplate.DeliversPullRequest"/>. When true, the run's merge-ready
    /// finalizer pushes the run branch and opens a PR (or reports a clear outcome and leaves the work on its branch when
    /// it cannot — no <c>gh</c>, no remote). When false (an administrative run, or a free/CEO-first plan), the run
    /// settles merge-ready with no PR expectation and no error for the missing PR. Defaults to false — silence is only
    /// wrong for a code run.
    /// </summary>
    public bool DeliversPullRequest { get; init; }

    /// <summary>The run's display label — its <see cref="Name"/>, or the <see cref="Goal"/> when no name was set yet,
    /// prefixed with the source issue key (AC-199) so a tracker-triggered run reads as "AC-191 - …" in the queue and
    /// history rather than by its bare summary.</summary>
    public string Label => _WithSourcePrefix(string.IsNullOrWhiteSpace(Name) ? Goal : Name);

    /// <summary>
    /// The best available name to pre-fill the approval field with (Raymond 2026-07-22): the CEO's proposed
    /// <see cref="Name"/> when it gave one, else the <see cref="Goal"/>, else the first step's title. The CEO does not
    /// always pass a name (or even a goal) through the plan tool, so this always yields something concrete for the
    /// operator to accept or edit rather than leaving the field — and the approval gate — empty on a planned run.
    /// </summary>
    public string SuggestedName =>
        _WithSourcePrefix(
            !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(Goal) ? Goal
            : Steps.FirstOrDefault()?.Title ?? string.Empty);

    /// <summary>
    /// Prefixes a run name with the source issue key (AC-199) — "AC-191 - {name}" — when this plan came from a tracker
    /// item, so the operator-facing name carries the ticket it serves. A CEO-first plan (no <see cref="Source"/>) is
    /// left untouched, and a name that already opens with the issue key (the CEO proposed one, or the prefix was applied
    /// once already) is not prefixed twice.
    /// </summary>
    private string _WithSourcePrefix(string name) =>
        Source is { IssueId: { Length: > 0 } issueId }
        && !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith(issueId, StringComparison.OrdinalIgnoreCase)
            ? $"{issueId} - {name}"
            : name;

    /// <summary>This plan with a run name — the CEO's proposal, or the operator's override at approval.</summary>
    public AutopilotPlan WithName(string name) => this with { Name = name };

    /// <summary>This plan with the operator's chosen working directory, set at approval.</summary>
    public AutopilotPlan WithWorkingDirectory(string workingDirectory) => this with { WorkingDirectory = workingDirectory };

    /// <summary>This plan carrying the chosen template's PR-delivery signal (AC-216), stamped at approval — true only for a code run that must end with a merge-ready pull request.</summary>
    public AutopilotPlan WithDeliversPullRequest(bool deliversPullRequest) => this with { DeliversPullRequest = deliversPullRequest };

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
