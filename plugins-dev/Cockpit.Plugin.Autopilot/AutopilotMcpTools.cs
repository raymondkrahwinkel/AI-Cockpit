using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The in-process MCP tools (<c>mcp__cockpit-autopilot__*</c>) the run's own agent uses to report done-gate outcomes
/// back to Autopilot (AC-153), exposed through the plugin's own endpoint (<see cref="ICockpitHost.AddMcpEndpoint"/>).
/// The endpoint is advertised to the whole session fan-out, so each tool binds to the run only when the
/// transport-verified caller pane (<see cref="ICockpitHost.CurrentMcpCallerPaneId"/>) is the run's embedded session —
/// an agent cannot report for a run that is not its own, and it cannot spoof a pane id it types.
/// </summary>
internal sealed class AutopilotMcpTools(ICockpitHost host, AutopilotRunController runs)
{
    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    [McpServerTool(Name = "autopilot_gate")]
    [Description("Report a done-gate outcome for this Autopilot run. gate is one of: verify, code, security, conventions. result is one of: passed, failed, skipped (skipped = the gate could not be run at all). Call this for each gate after you run it — the visual verify via mcp__cockpit-verify__verify, /code-review, /security-review, and the conventions check — before autopilot_ready.")]
    public string ReportGate(
        [Description("The gate: verify, code, security, or conventions.")] string gate,
        [Description("The outcome: passed, failed, or skipped.")] string result,
        [Description("A short note or link backing the outcome — a review summary, a verify screenshot path.")] string? evidence = null)
    {
        if (!_IsThisRun())
        {
            return _Fail("This call is not from the active Autopilot run's session.");
        }

        if (!_TryParseGate(gate, out var kind))
        {
            return _Fail($"Unknown gate '{gate}'. Use verify, code, security or conventions.");
        }

        if (!_TryParseOutcome(result, out var outcome))
        {
            return _Fail($"Unknown result '{result}'. Use passed, failed or skipped.");
        }

        runs.ReportGate(kind, outcome, string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim());
        return JsonSerializer.Serialize(new { ok = true, gate = kind.ToString(), result = outcome.ToString() }, Serializer);
    }

    [McpServerTool(Name = "autopilot_ready")]
    [Description("Signal that the work is done and the pull request is open. Pass the PR url so Autopilot can post it back to the tracker as evidence. Autopilot settles the run to merge-ready when every hard gate passed, or blocked when one did not. Call this only after opening the PR and reporting every gate. Do NOT merge — a human does the merge.")]
    public string MarkReady(
        [Description("The url of the pull request you opened, so it can be posted back to the tracker.")] string? prUrl = null)
    {
        if (!_IsThisRun())
        {
            return _Fail("This call is not from the active Autopilot run's session.");
        }

        runs.MarkReady(prUrl);
        return JsonSerializer.Serialize(new { ok = true, phase = runs.Phase.ToString(), reason = runs.BlockReason }, Serializer);
    }

    // The transport-verified caller pane must be the run's own embedded session (AC-89/AC-128) — never a pane the agent
    // typed — so a different session cannot report for this run.
    private bool _IsThisRun() =>
        runs.SessionPaneId is { Length: > 0 } pane && host.CurrentMcpCallerPaneId == pane;

    private static bool _TryParseGate(string gate, out GateKind kind)
    {
        switch (gate.Trim().ToLowerInvariant())
        {
            case "verify": kind = GateKind.Verify; return true;
            case "code": kind = GateKind.CodeReview; return true;
            case "security": kind = GateKind.Security; return true;
            case "conventions": kind = GateKind.Conventions; return true;
            default: kind = default; return false;
        }
    }

    private static bool _TryParseOutcome(string result, out AutopilotGateOutcome outcome)
    {
        switch (result.Trim().ToLowerInvariant())
        {
            case "passed": outcome = AutopilotGateOutcome.Passed; return true;
            case "failed": outcome = AutopilotGateOutcome.Failed; return true;
            case "skipped": outcome = AutopilotGateOutcome.Skipped; return true;
            default: outcome = default; return false;
        }
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
