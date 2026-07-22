using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The in-process MCP tool (<c>mcp__cockpit-autopilot-plan__autopilot_plan</c>) the CEO uses during the planning round
/// (AC-174) to emit and revise the plan. Pane-scoped like the run's report tools (<see cref="AutopilotRunTools"/>): only
/// the planning session bound to this controller (<see cref="AutopilotPlanController.SessionPaneId"/>) may set the plan,
/// so another session cannot rewrite it. The operator approves the plan through the host UI to freeze it and start the run.
/// </summary>
internal sealed class AutopilotPlanTools(ICockpitHost host, AutopilotPlanController plan)
{
    /// <summary>The in-process MCP server name the plugin mounts this tool under — the plan-flow's own, dark outside planning.</summary>
    internal const string EndpointName = "cockpit-autopilot-plan";

    /// <summary>The tool's own name; combined with <see cref="EndpointName"/> into the qualified name the CEO calls.</summary>
    internal const string ToolName = "autopilot_plan";

    /// <summary>The fully-qualified tool name the CEO is briefed to call — one source of truth for the endpoint and the brief.</summary>
    internal static string QualifiedToolName => $"mcp__{EndpointName}__{ToolName}";

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Parser = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = ToolName)]
    [Description("Emit or revise the plan for this Autopilot run during planning. Pass the goal, a short run name, and the ordered steps as a JSON array; each step: {id, title, description, profile, model, brief, acceptance, hard}. 'hard' true marks a required gate, false or omitted a skippable step. 'model' may be omitted when the profile pins its own model (a local profile). Call this whenever you (re)draft the plan so the operator sees the current plan; they approve it to start the autonomous run.")]
    public string SetPlan(
        [Description("What the run is to achieve — one sentence.")] string goal,
        [Description("The ordered steps as a JSON array of {id, title, description, profile, model, brief, acceptance, hard, mcp, agents}. 'mcp' is the minimal list of MCP server ids the step needs (e.g. [\"cockpit-verify\"]) — keep it minimal, not everything, to save tokens and stay least-privilege. 'agents' is how many agents work the step at once (default 1) — use more only where the work splits cleanly without the parts touching the same files.")] string stepsJson,
        [Description("A short run name (2-5 words) the operator recognises this run by in the queue and history — you propose it; the operator can override it before approving. Optional; when omitted the current name is kept.")] string? name = null,
        [Description("The absolute path of the folder this run should work in, when you can resolve it from the item (e.g. the repository the issue is about) — you propose it and the operator can override it before approving. Optional; when omitted the current directory is kept. A folder that is a git repository has each step isolated in its own worktree; a plain folder (an admin task with no repo) runs without isolation.")] string? workingDirectory = null)
    {
        if (!_IsThisPlanningSession())
        {
            return _Fail("This call is not from this run's planning session.");
        }

        if (plan.Phase != AutopilotPlanPhase.Planning)
        {
            return _Fail("The plan can only be set during planning; this run is past that.");
        }

        if (!TryParseSteps(stepsJson, out var steps, out var error))
        {
            return _Fail(error!);
        }

        var effectiveGoal = string.IsNullOrWhiteSpace(goal) ? plan.Plan?.Goal ?? string.Empty : goal.Trim();
        // The CEO's proposed name is a suggestion the operator can override at approval; keep the current one when this
        // emission omits it, so a re-emit that only revises steps does not wipe a name the operator already set.
        var effectiveName = string.IsNullOrWhiteSpace(name) ? plan.Plan?.Name ?? string.Empty : name.Trim();
        // Same as the name: the CEO's proposed directory pre-fills the operator's field but is theirs to override, so a
        // re-emit that omits it keeps the current one rather than wiping a folder the operator already picked.
        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? plan.Plan?.WorkingDirectory ?? string.Empty : workingDirectory.Trim();
        plan.UpdatePlan(new AutopilotPlan(effectiveGoal, plan.Plan?.Source, steps) { Name = effectiveName, WorkingDirectory = effectiveWorkingDirectory });
        return JsonSerializer.Serialize(new { ok = true, steps = steps.Count }, Serializer);
    }

    // Parsing is kept separate so it can be tested without a live endpoint. It rejects an empty list — a plan needs steps —
    // and a step missing its id or title, so a half-formed emission cannot silently produce an unrunnable plan.
    internal static bool TryParseSteps(string json, out IReadOnlyList<AutopilotStep> steps, out string? error)
    {
        steps = [];
        error = null;

        List<PlanStepDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<PlanStepDto>>(json, Parser);
        }
        catch (JsonException ex)
        {
            error = $"Steps are not valid JSON: {ex.Message}";
            return false;
        }

        if (dtos is not { Count: > 0 })
        {
            error = "A plan needs at least one step.";
            return false;
        }

        var built = new List<AutopilotStep>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Title))
            {
                error = "Every step needs an id and a title.";
                return false;
            }

            built.Add(new AutopilotStep(
                dto.Id.Trim(),
                dto.Title.Trim(),
                dto.Description?.Trim() ?? string.Empty,
                dto.Profile?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model.Trim(),
                dto.Brief?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(dto.Acceptance) ? null : dto.Acceptance.Trim(),
                dto.Hard ? GateMode.Hard : GateMode.Skip)
            {
                // Minimal MCP set per step (Raymond 2026-07-21): only what the step needs — blanks dropped.
                McpServers = dto.Mcp is { Count: > 0 }
                    ? [.. dto.Mcp.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())]
                    : [],
                // How many agents work this step at once (Raymond 2026-07-21); at least 1, the operator can force it back.
                AgentCount = dto.Agents is { } count && count > 1 ? count : 1,
            });
        }

        steps = built;
        return true;
    }

    private bool _IsThisPlanningSession() =>
        plan.SessionPaneId is { Length: > 0 } pane && host.CurrentMcpCallerPaneId == pane;

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);

    // The shape the CEO emits per step. Loose (all optional on the wire) so a mildly-off emission is corrected with a
    // clear error rather than a deserialization crash; TryParseSteps enforces what a runnable step actually needs.
    internal sealed record PlanStepDto
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? Profile { get; init; }
        public string? Model { get; init; }
        public string? Brief { get; init; }
        public string? Acceptance { get; init; }
        public bool Hard { get; init; }
        public List<string>? Mcp { get; init; }
        public int? Agents { get; init; }
    }
}
