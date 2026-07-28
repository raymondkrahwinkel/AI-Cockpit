namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// One step of a CEO-built plan (AC-174) — task-type-agnostic. A step is a titled unit of work with a short
/// description, the <see cref="ProfileLabel"/> it runs on and, where that profile's provider offers a choice, the
/// <see cref="Model"/> to run it on (Claude/Codex expose a model; a local Ollama/LM Studio profile pins its own, so
/// <see cref="Model"/> is null then). It carries the <see cref="Brief"/> the executing agent is handed and the
/// <see cref="Acceptance"/> it is judged against, whether it is a hard gate or skippable (<see cref="Mode"/>), and where
/// it currently sits (<see cref="Status"/>). The done-gates (verify/code/security/conventions) are the steps the CEO
/// emits for a coding goal — not a fixed set baked into the model; a non-coding goal gets different steps.
/// </summary>
/// <param name="Id">Stable id within the plan — how a status update or an operator edit finds this step again.</param>
/// <param name="ProfileLabel">The session profile the step runs on; the CEO pre-selects it and the operator may change it.</param>
/// <param name="Model">The model to run on where the profile offers a choice, or null when the profile pins its own.</param>
/// <param name="Brief">The work brief the executing agent is handed when this step starts — the CEO's prepared context.</param>
/// <param name="Acceptance">What "done" means for this step, or null when the step needs no explicit acceptance.</param>
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
    /// <summary>
    /// The MCP servers this step's session is launched with — deliberately the <em>minimal</em> set the step needs, not
    /// everything: a smaller MCP surface is fewer tool definitions in the agent's context (tokens)
    /// and tighter least-privilege (AC-117). The CEO scopes it per step; empty means the step needs no extra MCP beyond
    /// what Autopilot's own report endpoint gives it. Server ids as the host advertises them (e.g. <c>cockpit-verify</c>).
    /// </summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>
    /// The tracker-neutral id of the issue this step is drafted from, when the CEO folded a specific item into the
    /// plan — the run's own source issue, or (for an epic) one of its child issues (AC-411). Null for a step with no
    /// such backing item (most steps, and every step of a CEO-first run). Lets <see cref="AutopilotPlanTools.SetPlan"/>
    /// check a child against the same executable-stage gate its parent already passed, rather than trusting the CEO's
    /// read of the item's stage from the brief.
    /// </summary>
    public string? SourceIssueId { get; init; }

    /// <summary>
    /// How many times this step has been started (AC-174). The CEO validates a step's output against
    /// its <see cref="Acceptance"/>; a step that does not pass is sent back to rework and re-run — but only while it has
    /// attempts left under the run's cap, so a rework loop is bounded and never becomes an endless loop.
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// How many times a validation sent this step back to rework (AC-347) — in contrast to <see cref="Attempts"/>,
    /// which counts every (re-)start, including one with no verdict behind it at all (a crashed session, a stall
    /// timeout, a refused isolation, a profile/model mismatch). This is the narrower count the reliability
    /// classification needs: a rework is a judged correction, a restart is not.
    /// </summary>
    public int Reworks { get; init; }

    /// <summary>
    /// How many agents work this step at once (AC-174). Default 1. The CEO decides where parallel
    /// work is safe — e.g. splitting code work across two or three agents whose parts will not touch the same files —
    /// and the operator can force it back to a single agent ("no multitasking here"). Agents in a parallel step run
    /// isolated (their own worktrees) so they do not get in each other's way; the CEO is what keeps their parts disjoint.
    /// </summary>
    public int AgentCount { get; init; } = 1;

    /// <summary>
    /// A short human-readable note on the step's latest outcome (AC-174) — why it failed (the CEO's validation reason, or
    /// that the run refused to isolate its session), or a status line while it runs. Surfaced on the pipeline block so a
    /// failed step is not a silent red dot: the operator sees what happened without opening the session. Empty until the
    /// run has something to say.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>This step with a new status — the run advances a step without rebuilding the rest of the plan.</summary>
    public AutopilotStep WithStatus(AutopilotStepStatus status) => this with { Status = status };

    /// <summary>This step with a note on its latest outcome — the run explains a failure or shows progress on the block.</summary>
    public AutopilotStep WithNote(string note) => this with { Note = note };

    /// <summary>This step with its attempt count incremented — the driver records a (re-)run before it starts.</summary>
    public AutopilotStep WithAttempt() => this with { Attempts = Attempts + 1 };

    /// <summary>This step with its rework count incremented — a validation sent it back, the one place <see cref="Reworks"/> grows.</summary>
    public AutopilotStep WithRework() => this with { Reworks = Reworks + 1 };

    /// <summary>This step re-targeted at a profile (and its model), the operator's edit during the planning round.</summary>
    public AutopilotStep WithProfile(string profileLabel, string? model) =>
        this with { ProfileLabel = profileLabel, Model = model };
}
