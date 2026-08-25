namespace Cockpit.Plugin.Autopilot;

// The plan the CEO builds and the operator approves once (AC-174) — a living artifact during planning, frozen at
// approval when the autonomous run starts driving it step by step. `Source` is null for a CEO-first plan.
// `Goal`: what the run is to achieve.
internal sealed record AutopilotPlan(
    string Goal,
    AutopilotPlanSource? Source,
    IReadOnlyList<AutopilotStep> Steps)
{
    // A short operator-facing label for the run: the CEO proposes it while planning and the
    // operator can override it, but it must be non-empty before the run is approved — so a queue of several runs is
    // recognisable, and history reads by name rather than by goal sentence. Falls back to `Goal` when unset.
    public string Name { get; init; } = string.Empty;

    // The folder the run works in (AC-174), chosen at approval so a tracker-triggered run (which has no
    // session, hence no directory) still knows where to work. Blank falls back to the session's directory, then
    // the cockpit's own; a git repository is isolated per step, a plain folder runs without isolation.
    public string WorkingDirectory { get; init; } = string.Empty;

    // Whether this run is a code run that must end with a merge-ready pull request (AC-216) — set at approval
    // from the chosen template. When true the finalizer pushes and opens a PR (or reports why it couldn't);
    // when false (admin run, free/CEO-first plan) merge-ready settles with no PR expectation. Defaults to false.
    public bool DeliversPullRequest { get; init; }

    // The run's display label — its `Name`, or the `Goal` when no name was set yet,
    // prefixed with the source issue key (AC-199) so a tracker-triggered run reads as "AC-191 - …" in the queue and
    // history rather than by its bare summary.
    public string Label => _WithSourcePrefix(string.IsNullOrWhiteSpace(Name) ? Goal : Name);

    // The best available name to pre-fill the approval field with: `Name`, else `Goal`, else the first step's
    // title. The CEO does not always pass a name (or goal) through the plan tool, so this always yields something
    // concrete rather than leaving the field — and the approval gate — empty.
    public string SuggestedName =>
        _WithSourcePrefix(
            !string.IsNullOrWhiteSpace(Name) ? Name
            : !string.IsNullOrWhiteSpace(Goal) ? Goal
            : Steps.FirstOrDefault()?.Title ?? string.Empty);

    // Prefixes a run name with the source issue key (AC-199) — "AC-191 - {name}" — when this plan came from a
    // tracker item. A CEO-first plan (no `Source`) is left untouched, and a name already carrying the
    // issue key is not prefixed twice.
    private string _WithSourcePrefix(string name) =>
        Source is { IssueId: { Length: > 0 } issueId }
        && !string.IsNullOrWhiteSpace(name)
        && !name.StartsWith(issueId, StringComparison.OrdinalIgnoreCase)
            ? $"{issueId} - {name}"
            : name;

    // This plan with a run name — the CEO's proposal, or the operator's override at approval.
    public AutopilotPlan WithName(string name) => this with { Name = name };

    // This plan with the operator's chosen working directory, set at approval.
    public AutopilotPlan WithWorkingDirectory(string workingDirectory) => this with { WorkingDirectory = workingDirectory };

    // This plan carrying the chosen template's PR-delivery signal (AC-216), stamped at approval — true only for a code run that must end with a merge-ready pull request.
    public AutopilotPlan WithDeliversPullRequest(bool deliversPullRequest) => this with { DeliversPullRequest = deliversPullRequest };

    // An empty plan to open the planning round on — the CEO fills the steps from there.
    public static AutopilotPlan Empty(AutopilotPlanSource? source, string goal) => new(goal, source, []);

    // The step running now, or null when none is (before start, between steps, or once the run has settled).
    public AutopilotStep? Active => Steps.FirstOrDefault(step => step.Status == AutopilotStepStatus.Running);

    // The next step still to run, or null when every step has settled — how the run picks what to start next.
    public AutopilotStep? NextPending => Steps.FirstOrDefault(step => step.Status == AutopilotStepStatus.Pending);

    // The next unit of pending work (AC-434): the run of review-gate steps starting at, and contiguous with,
    // `NextPending` — so the driver runs a code-review/security-review pair concurrently, without pulling a later
    // review gate forward past an unfinished step. Just `NextPending` alone for an ordinary step.
    public IReadOnlyList<AutopilotStep> NextPendingGroup
    {
        get
        {
            if (NextPending is not { } next)
            {
                return [];
            }

            if (!next.IsReviewGate)
            {
                return [next];
            }

            var group = new List<AutopilotStep>();
            foreach (var step in Steps.SkipWhile(candidate => candidate.Id != next.Id))
            {
                if (!step.IsReviewGate || step.Status != AutopilotStepStatus.Pending)
                {
                    break;
                }

                group.Add(step);
            }

            return group;
        }
    }

    // Replaces the step sharing `step`'s id, returning a new plan — a single living-artifact edit.
    public AutopilotPlan WithStep(AutopilotStep step) =>
        this with { Steps = [.. Steps.Select(existing => existing.Id == step.Id ? step : existing)] };

    // Replaces the whole step list — how the CEO re-emits a revised plan while the operator is still shaping it.
    public AutopilotPlan WithSteps(IReadOnlyList<AutopilotStep> steps) => this with { Steps = steps };
}
