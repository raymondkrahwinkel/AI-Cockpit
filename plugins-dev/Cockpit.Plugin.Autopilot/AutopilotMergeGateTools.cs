using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The merge gate's answer channel (AC-1338): one tool that gives or refuses the go for a sub waiting at the gate.
// Deliberately NOT an internal endpoint: the cockpit-assistant only sees what the default MCP fan-out offers
// (AssistantSessionHost.McpSelection), so this is how its go reaches a run; run sessions never mount it.
internal sealed class AutopilotMergeGateTools(ICockpitHost host, AutopilotRunManager manager)
{
    internal const string EndpointName = "cockpit-autopilot-merge-gate";

    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    [McpServerTool(Name = "autopilot_merge_go", ReadOnly = false, Destructive = true)]
    [Description("Give or refuse the go for an Autopilot run waiting at its merge gate — the sub named by `issue` has passed both review gates and its evidence package (pull request, HEAD, diff-stat against the epic's collection branch, gate verdicts, build) was handed to cockpit-assistant. go=true rebases the run branch onto the collection branch, merges its pull request, builds the new tip and reports the exit code on the epic; go=false leaves the branch and its pull request untouched and records your reason on the epic. Read the diff-stat first: a deletion of files another sub added is the failure this gate exists to catch. Refused when no run is waiting at the gate for that issue.")]
    public string MergeGo(
        [Description("The issue id of the sub waiting at the gate, exactly as the evidence package names it (e.g. \"AC-1338\").")] string issue,
        [Description("true to merge, false to refuse.")] bool go,
        [Description("One line on why — required for a refusal, welcome for a go.")] string? reason = null)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (string.IsNullOrWhiteSpace(issue) || (!go && trimmedReason is null))
        {
            return _Fail("Name the issue waiting at the gate, and give a reason when you refuse.");
        }

        // Whoever answered is named by the pane the host verified the call came from — "cockpit-assistant" for the
        // assistant, a session's pane id for a desk reviewer — so the epic comment says who gave the go.
        var by = $"autopilot_merge_go from {(string.IsNullOrEmpty(host.CurrentMcpCallerPaneId) ? "an unidentified pane" : host.CurrentMcpCallerPaneId)}";
        if (!manager.ReportMergeGo(issue.Trim(), go, trimmedReason, by))
        {
            return _Fail($"No Autopilot run is waiting at the merge gate for {issue.Trim()}.");
        }

        return JsonSerializer.Serialize(new { ok = true, issue = issue.Trim(), go }, Serializer);
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
