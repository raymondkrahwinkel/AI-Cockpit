using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The in-process MCP tools (<c>mcp__cockpit-autopilot-ceo__*</c>) only the run's CEO validator uses (AC-174, Raymond
/// 2026-07-22): report a step's validation verdict (<c>autopilot_validate</c>), answer or escalate a worker's mid-step
/// consult (<c>autopilot_answer_worker</c>/<c>autopilot_escalate_to_operator</c>, AC-201), and keep the source issue in
/// sync (<c>autopilot_tracker_stage</c>/<c>autopilot_tracker_note</c>). Split off the step agents' own endpoint
/// (<see cref="AutopilotRunTools"/>) so a step agent never even sees the CEO's tools — tighter least-privilege, and a
/// weaker (local) model is not distracted into calling a validate/tracker tool it has no business calling. Each is still
/// pane-scoped through <see cref="ICockpitHost.CurrentMcpCallerPaneId"/>, so only the run's CEO session can call them.
/// </summary>
internal sealed class AutopilotCeoTools(ICockpitHost host, AutopilotRunManager manager)
{
    /// <summary>The in-process MCP server name the plugin mounts the CEO's tools under — dark once a run has settled.</summary>
    internal const string EndpointName = "cockpit-autopilot-ceo";

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    [McpServerTool(Name = "autopilot_validate")]
    [Description("Report your validation verdict for the Autopilot step you were just asked to check. passed=true when the step's output meets its acceptance, false when it does not (the step is reworked, or fails once its attempts run out). Give a one-line reason. Only the run's CEO session validates.")]
    public string Validate(
        [Description("Whether the step's output meets its acceptance.")] bool passed,
        [Description("A one-line reason for the verdict.")] string? reason = null)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || !manager.ReportValidation(pane, passed, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()))
        {
            return _Fail("This call is not from the run's CEO session, or no validation is pending.");
        }

        return JsonSerializer.Serialize(new { ok = true, passed }, Serializer);
    }

    [McpServerTool(Name = "autopilot_answer_worker")]
    [Description("Answer a worker that consulted you mid-step (AC-201). Your answer is relayed into the worker's session as a turn — it reads it and carries on. Use this when you can settle the question yourself: a convention to follow, a reasonable default, a design call within the approved plan. You may inspect the working directory (Read/Grep) before you answer. Only the run's CEO session may, and only while a worker is actually waiting on your consult.")]
    public async Task<string> AnswerWorker(
        [Description("The answer relayed to the waiting worker as a turn in its session.")] string answer)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(answer) || !await manager.AnswerWorkerAsync(pane, answer.Trim()))
        {
            return _Fail("Only the run's CEO session can answer a worker's consult, and only while one is pending.");
        }

        return JsonSerializer.Serialize(new { ok = true }, Serializer);
    }

    [McpServerTool(Name = "autopilot_escalate_to_operator")]
    [Description("Escalate a worker's consult to the operator (AC-201) when it is genuinely their call — a truly irreversible or destructive choice, a missing credential, or a business preference you cannot make within the approved plan. The run pauses and the operator's answer is relayed back to the waiting worker (not to you), which then carries on. Prefer to answer the worker yourself with autopilot_answer_worker whenever you reasonably can; only escalate what really needs the operator. Only the run's CEO session may, and only while a worker is actually waiting on your consult.")]
    public string EscalateToOperator(
        [Description("The question to put to the operator, in one message.")] string question)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(question) || !manager.EscalateToOperator(pane, question.Trim()))
        {
            return _Fail("Only the run's CEO session can escalate a worker's consult, and only while one is pending.");
        }

        return JsonSerializer.Serialize(new { ok = true, status = "awaiting-operator" }, Serializer);
    }

    [McpServerTool(Name = "autopilot_tracker_stage")]
    [Description("Move the tracker issue this run was triggered from to a stage — using the tracker's own stage name (e.g. \"In Progress\", \"Review\", \"Done\"). Only the run's CEO session may, and only for a run triggered from a tracker issue (a CEO-first run has no issue to move). Call it as the run reaches each stage.")]
    public async Task<string> TrackerStage(
        [Description("The stage to move the source issue to, in the tracker's own vocabulary.")] string stage)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(stage) || !await manager.ReportTrackerStageAsync(pane, stage.Trim()))
        {
            return _Fail("Only the run's CEO session can move a source issue's stage, and only for a run triggered from a tracker issue whose tracker plugin is installed.");
        }

        return JsonSerializer.Serialize(new { ok = true, stage = stage.Trim() }, Serializer);
    }

    [McpServerTool(Name = "autopilot_tracker_note")]
    [Description("Post a comment on the tracker issue this run was triggered from — a status note or the run's evidence (what was done, the PR, the outcome). Only the run's CEO session may, and only for a run triggered from a tracker issue.")]
    public async Task<string> TrackerNote(
        [Description("The comment to post on the source issue.")] string note)
    {
        var pane = host.CurrentMcpCallerPaneId;
        if (string.IsNullOrEmpty(pane) || string.IsNullOrWhiteSpace(note) || !await manager.ReportTrackerNoteAsync(pane, note.Trim()))
        {
            return _Fail("Only the run's CEO session can comment on a source issue, and only for a run triggered from a tracker issue whose tracker plugin is installed.");
        }

        return JsonSerializer.Serialize(new { ok = true }, Serializer);
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
