using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The in-process MCP tools a step agent uses (AC-174): report done, or consult its manager (the CEO) via
// `autopilot_blocked` (AC-201), which answers or escalates rather than the worker reaching the operator
// directly. Pane-scoped so a step only reports for its own session.
internal sealed class AutopilotRunTools(ICockpitHost host, AutopilotRunManager manager)
{
    // The in-process MCP server name the plugin mounts these tools under — dark once a run has settled.
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
    [Description("Consult your manager (the CEO) when you cannot proceed on your own judgement — this does NOT go straight to the operator. Prefer to keep going first: for an ordinary judgement call the brief did not spell out, make a documented, reasonable assumption in line with the goal and acceptance, follow the codebase's existing conventions, and note it in your autopilot_step_done summary — do not consult on it. But when you genuinely cannot decide with a reasonable assumption — a real ambiguity, a design call beyond the plan, a truly irreversible or destructive choice, a missing credential you cannot obtain, or progress being objectively impossible — call this to put your question to your manager. Your manager answers you (the reply is relayed to you as a turn in this same session, and you carry on from there) or, only if it is genuinely an operator decision, escalates it for you. Pass the question in one message.")]
    public async Task<string> Blocked(
        [Description("The question or blocker to put to your manager, in one message.")] string question)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(question) || !await manager.ReportConsultAsync(pane, question.Trim()))
        {
            return _Fail("A consult needs a question, and only the run's live step session can raise one while it is running.");
        }

        return JsonSerializer.Serialize(new { ok = true, status = "consulting-manager" }, Serializer);
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
