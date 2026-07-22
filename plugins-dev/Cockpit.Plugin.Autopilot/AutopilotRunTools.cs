using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The in-process MCP tools (<c>mcp__cockpit-autopilot-run__*</c>) a step agent uses (AC-174): report its work finished
/// with <c>autopilot_step_done</c>, or raise a blockade for the operator with <c>autopilot_blocked</c>. This is the only
/// Autopilot endpoint a step agent is given — the CEO's own tools (validate, tracker) live on the separate
/// <see cref="AutopilotCeoTools"/> endpoint, so a step agent never sees them (least-privilege, and a weaker model is not
/// distracted into calling them). Pane-scoped through <see cref="ICockpitHost.CurrentMcpCallerPaneId"/>, so a step can
/// only report for its own session. Each call hands the outcome to the <see cref="AutopilotRunCoordinator"/>.
/// </summary>
internal sealed class AutopilotRunTools(ICockpitHost host, AutopilotRunManager manager)
{
    /// <summary>The in-process MCP server name the plugin mounts these tools under — dark once a run has settled.</summary>
    internal const string EndpointName = "cockpit-autopilot-run";

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    [McpServerTool(Name = "autopilot_step_done")]
    [Description("Signal that you have finished this Autopilot step. Pass a short summary of what you did and the result (a branch, a PR url, a review outcome) so the CEO can validate it against the step's acceptance. Call this exactly once, when the step's work is complete. Do not merge anything — a human does the final merge.")]
    public string StepDone(
        [Description("A short summary of what you did and the result, for the CEO to validate against the step's acceptance.")] string summary)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || !manager.ReportStepDone(pane, summary ?? string.Empty))
        {
            return _Fail("This call is not from an active Autopilot step session.");
        }

        return JsonSerializer.Serialize(new { ok = true }, Serializer);
    }

    [McpServerTool(Name = "autopilot_blocked")]
    [Description("Last resort: signal that you are blocked and need the operator to answer before you can continue. Autopilot shows your question on the run surface and waits; the operator's reply is relayed to you as a turn in this same session, and you carry on from there. Prefer to keep going: for an ordinary judgement call the brief did not spell out, make a documented, reasonable assumption in line with the goal and acceptance, follow the codebase's existing conventions, and note it in your autopilot_step_done summary — do not block on it. Reserve this for a genuine hard blocker only: a truly irreversible or destructive choice, a missing credential you cannot obtain, or progress being objectively impossible. Pass the question in one message.")]
    public string Blocked(
        [Description("The question or blocker the operator needs to resolve, in one message.")] string question)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(question) || !manager.ReportBlocked(pane, question.Trim()))
        {
            return _Fail("A blockade needs a question, and only the run's live step or CEO session can raise one while it is running.");
        }

        return JsonSerializer.Serialize(new { ok = true, status = "awaiting-operator" }, Serializer);
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
