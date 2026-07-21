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
    /// everything (Raymond 2026-07-21): a smaller MCP surface is fewer tool definitions in the agent's context (tokens)
    /// and tighter least-privilege (AC-117). The CEO scopes it per step; empty means the step needs no extra MCP beyond
    /// what Autopilot's own report endpoint gives it. Server ids as the host advertises them (e.g. <c>cockpit-verify</c>).
    /// </summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>
    /// How many times this step has been started (AC-174, Raymond 2026-07-21). The CEO validates a step's output against
    /// its <see cref="Acceptance"/>; a step that does not pass is sent back to rework and re-run — but only while it has
    /// attempts left under the run's cap, so a rework loop is bounded and never becomes an endless loop.
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// How many agents work this step at once (AC-174, Raymond 2026-07-21). Default 1. The CEO decides where parallel
    /// work is safe — e.g. splitting code work across two or three agents whose parts will not touch the same files —
    /// and the operator can force it back to a single agent ("no multitasking here"). Agents in a parallel step run
    /// isolated (their own worktrees) so they do not get in each other's way; the CEO is what keeps their parts disjoint.
    /// </summary>
    public int AgentCount { get; init; } = 1;

    /// <summary>This step with a new status — the run advances a step without rebuilding the rest of the plan.</summary>
    public AutopilotStep WithStatus(AutopilotStepStatus status) => this with { Status = status };

    /// <summary>This step with its attempt count incremented — the driver records a (re-)run before it starts.</summary>
    public AutopilotStep WithAttempt() => this with { Attempts = Attempts + 1 };

    /// <summary>This step re-targeted at a profile (and its model), the operator's edit during the planning round.</summary>
    public AutopilotStep WithProfile(string profileLabel, string? model) =>
        this with { ProfileLabel = profileLabel, Model = model };
}
