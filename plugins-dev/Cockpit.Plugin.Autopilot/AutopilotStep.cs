namespace Cockpit.Plugin.Autopilot;

// One step of a CEO-built plan (AC-174) — task-type-agnostic: a titled unit of work with the profile/model it
// runs on, the `Brief` handed to the agent, the `Acceptance` it is judged against, whether it is a hard gate or
// skippable, and where it sits. Done-gates are steps the CEO emits for a coding goal, not a fixed set.
internal sealed record AutopilotStep(
    string Id,
    string Title,
    string Description,
    string ProfileLabel,
    string? Model,
    string Brief,
    string? Acceptance,
    GateMode Mode = GateMode.Skip,
    AutopilotStepStatus Status = AutopilotStepStatus.Pending)
{
    // The MCP servers this step's session is launched with — deliberately the *minimal* set the step needs: fewer
    // tool definitions in context, tighter least-privilege (AC-117). Empty means no extra MCP beyond Autopilot's
    // own report endpoint. Server ids as the host advertises them (e.g. `cockpit-verify`).
    public IReadOnlyList<string> McpServers { get; init; } = [];

    // The tracker-neutral id of the issue this step is drafted from, when the CEO folded a specific item into the
    // plan (AC-411); null for most steps. Lets `AutopilotPlanTools.SetPlan` check a child against the
    // same executable-stage gate its parent already passed, rather than trusting the CEO's read from the brief.
    public string? SourceIssueId { get; init; }

    // How many times this step has been started (AC-174). A step that fails validation is sent back to rework
    // and re-run — but only while it has attempts left under the run's cap, so the loop is bounded.
    public int Attempts { get; init; }

    // How many times a validation sent this step back to rework (AC-347) — narrower than `Attempts`, which counts
    // every (re-)start including one with no verdict (a crash, a stall, a refused isolation). A rework is a
    // judged correction, a restart is not.
    public int Reworks { get; init; }

    // How many agents work this step at once (AC-174). Default 1. The CEO decides where parallel work is safe,
    // and the operator can force it back to a single agent. Agents in a parallel step run isolated (their own
    // worktrees); the CEO is what keeps their parts disjoint.
    public int AgentCount { get; init; } = 1;

    // A short human-readable note on the step's latest outcome (AC-174) — why it failed, or a status line while
    // it runs. Surfaced on the pipeline block so a failed step is not a silent red dot. Empty until the run has something to say.
    public string Note { get; init; } = string.Empty;

    // This step with a new status — the run advances a step without rebuilding the rest of the plan.
    public AutopilotStep WithStatus(AutopilotStepStatus status) => this with { Status = status };

    // This step with a note on its latest outcome — the run explains a failure or shows progress on the block.
    public AutopilotStep WithNote(string note) => this with { Note = note };

    // This step with its attempt count incremented — the driver records a (re-)run before it starts.
    public AutopilotStep WithAttempt() => this with { Attempts = Attempts + 1 };

    // This step with its rework count incremented — a validation sent it back, the one place `Reworks` grows.
    public AutopilotStep WithRework() => this with { Reworks = Reworks + 1 };

    // This step re-targeted at a profile (and its model), the operator's edit during the planning round.
    public AutopilotStep WithProfile(string profileLabel, string? model) =>
        this with { ProfileLabel = profileLabel, Model = model };

    // Marks this step a review gate the CEO plans alongside its siblings rather than strictly one after another
    // (AC-434): the driver runs every step carrying this flag concurrently, each on its own throwaway worktree
    // copy, then inserts one shared fix step. False (default) runs one at a time as before.
    public bool IsReviewGate { get; init; }
}
