using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The in-process MCP tools (<c>mcp__cockpit-autopilot-run__*</c>) the autonomous CEO run uses (AC-174): a step agent
/// reports its work finished with <c>autopilot_step_done</c>, and the CEO reports its validation verdict with
/// <c>autopilot_validate</c>. Both are pane-scoped through the transport-verified caller
/// (<see cref="ICockpitHost.CurrentMcpCallerPaneId"/>) — a step can only report for its own session and only the run's
/// CEO session can validate, so an agent cannot report for work that is not its own or spoof a pane id it types. Each
/// call hands the outcome to the <see cref="AutopilotRunCoordinator"/>, which is what the executeStep adapter awaits.
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

    [McpServerTool(Name = "autopilot_blocked")]
    [Description("Signal that you are blocked and need the operator to answer before you can continue. Autopilot shows your question on the run surface and waits; the operator's reply is relayed to you as a turn in this same session, and you carry on from there. Use this instead of guessing. Pass the question in one message.")]
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
