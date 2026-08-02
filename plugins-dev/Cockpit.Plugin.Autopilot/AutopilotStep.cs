namespace Cockpit.Plugin.Autopilot;

// One step of a CEO-built plan (AC-174) — task-type-agnostic. A step is a titled unit of work with a short
// description, the `ProfileLabel` it runs on and, where that profile's provider offers a choice, the
// `Model` to run it on (Claude/Codex expose a model; a local Ollama/LM Studio profile pins its own, so
// `Model` is null then). It carries the `Brief` the executing agent is handed and the
// `Acceptance` it is judged against, whether it is a hard gate or skippable (`Mode`), and where
// it currently sits (`Status`). The done-gates (verify/code/security/conventions) are the steps the CEO
// emits for a coding goal — not a fixed set baked into the model; a non-coding goal gets different steps.
//
// `Id`: Stable id within the plan — how a status update or an operator edit finds this step again.
// `ProfileLabel`: The session profile the step runs on; the CEO pre-selects it and the operator may change it.
// `Model`: The model to run on where the profile offers a choice, or null when the profile pins its own.
// `Brief`: The work brief the executing agent is handed when this step starts — the CEO's prepared context.
// `Acceptance`: What "done" means for this step, or null when the step needs no explicit acceptance.
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
    // The MCP servers this step's session is launched with — deliberately the *minimal* set the step needs, not
    // everything: a smaller MCP surface is fewer tool definitions in the agent's context (tokens)
    // and tighter least-privilege (AC-117). The CEO scopes it per step; empty means the step needs no extra MCP beyond
    // what Autopilot's own report endpoint gives it. Server ids as the host advertises them (e.g. `cockpit-verify`).
    public IReadOnlyList<string> McpServers { get; init; } = [];

    // The tracker-neutral id of the issue this step is drafted from, when the CEO folded a specific item into the
    // plan — the run's own source issue, or (for an epic) one of its child issues (AC-411). Null for a step with no
    // such backing item (most steps, and every step of a CEO-first run). Lets `AutopilotPlanTools.SetPlan`
    // check a child against the same executable-stage gate its parent already passed, rather than trusting the CEO's
    // read of the item's stage from the brief.
    public string? SourceIssueId { get; init; }

    // How many times this step has been started (AC-174). The CEO validates a step's output against
    // its `Acceptance`; a step that does not pass is sent back to rework and re-run — but only while it has
    // attempts left under the run's cap, so a rework loop is bounded and never becomes an endless loop.
    public int Attempts { get; init; }

    // How many times a validation sent this step back to rework (AC-347) — in contrast to `Attempts`,
    // which counts every (re-)start, including one with no verdict behind it at all (a crashed session, a stall
    // timeout, a refused isolation, a profile/model mismatch). This is the narrower count the reliability
    // classification needs: a rework is a judged correction, a restart is not.
    public int Reworks { get; init; }

    // How many agents work this step at once (AC-174). Default 1. The CEO decides where parallel
    // work is safe — e.g. splitting code work across two or three agents whose parts will not touch the same files —
    // and the operator can force it back to a single agent ("no multitasking here"). Agents in a parallel step run
    // isolated (their own worktrees) so they do not get in each other's way; the CEO is what keeps their parts disjoint.
    public int AgentCount { get; init; } = 1;

    // A short human-readable note on the step's latest outcome (AC-174) — why it failed (the CEO's validation reason, or
    // that the run refused to isolate its session), or a status line while it runs. Surfaced on the pipeline block so a
    // failed step is not a silent red dot: the operator sees what happened without opening the session. Empty until the
    // run has something to say.
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

    // Marks this step a review gate the CEO plans alongside its sibling gates rather than strictly one after another
    // (AC-434) — the driver runs every pending step carrying this flag concurrently, each reading its own throwaway
    // copy of the run's worktree, and inserts one shared fix step to apply whatever they find before they re-verify.
    // False (the default) for an ordinary step, which the driver still runs one at a time exactly as before.
    public bool IsReviewGate { get; init; }
}
