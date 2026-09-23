using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Docker.Settings;

namespace Cockpit.Plugin.Docker.Security;

// The single policy chokepoint in front of the Docker daemon (AC-84), mirroring the Kubernetes plugin's
// `ClusterAccessGate`. Every daemon-touching MCP tool routes through here before it does anything.
//
// Policy (§12.2 defaults — local daemon only, no per-container jail v1):
//   - Connection — the first touch of the daemon asks once, LowRisk, remembered per pane. Reads are free after that.
//   - Mutation — start/stop/remove/run and other changes always ask afresh, Dangerous, never remembered, with the literal command shown.
//   - Danger capability (exec) — blocked with a settings hint unless the operator turned it on; then asks afresh, Dangerous, never remembered.
// AC-1348: `settings.ConsentMode` (per daemon endpoint) can pre-approve a read (ReadFree/AllFree) or a change
// (AllFree only), per `DockerToolAccess`. A pre-approved request still goes to `host.RequestConsentAsync` — with
// `PreApprovedBy` set — so the host logs it as bypassed instead of the gate silently skipping the ask.
// The `ConsentRequest.Action` is rendered verbatim to the operator, and parts of it (a container name, a
// command) are agent-supplied, so it is flattened to a single line — an agent cannot smuggle extra lines into the
// consent body.
internal sealed class DockerAccessGate(ICockpitHost host, DockerSettings settings)
{
    private const string SourceLabel = "Docker";

    // Authorize touching the daemon at all. LowRisk, remembered per pane — asks once, then reads are free. Pre-
    // approved instead when the consent mode frees this tool (AC-1348).
    public Task<GateResult> AuthorizeConnectionAsync(string toolName, string operation, string? paneId) =>
        _RequestAsync(
            "Connect to the Docker daemon",
            operation,
            "docker.connect:local",
            ConsentRisk.LowRisk,
            allowRemember: true,
            paneId,
            preApprovedBy: _PreApprovedFor(toolName));

    // Authorize a change to a Docker resource. Layered on connection auth, then Dangerous unless the consent mode
    // pre-approves this tool too. `detailLines` (AC-1062): each line is escaped on its own and joined with a real
    // newline, rather than the whole composed body being flattened as one — see `_ComposeAction`.
    public async Task<GateResult> AuthorizeMutationAsync(string toolName, string operation, string? paneId, IReadOnlyList<string>? detailLines = null)
    {
        var connection = await AuthorizeConnectionAsync(toolName, operation, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _RequestAsync(
            "Change a Docker resource",
            operation,
            "docker.mutate:local",
            ConsentRisk.Dangerous,
            allowRemember: false,
            paneId,
            detailLines,
            preApprovedBy: _PreApprovedFor(toolName));
    }

    // Authorize a dangerous capability (exec/run). Blocked with a settings hint when the capability is off — a
    // policy block regardless of consent mode. When on: connection auth, then Dangerous unless the consent mode
    // pre-approves this tool too.
    public async Task<GateResult> AuthorizeDangerAsync(string toolName, DangerCapability capability, bool enabled, string operation, string? paneId)
    {
        if (!enabled)
        {
            return GateResult.Deny(
                $"The \"{capability}\" capability is off for the Docker daemon. Turn it on in the plugin settings first.");
        }

        var connection = await AuthorizeConnectionAsync(toolName, operation, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _RequestAsync(
            $"Docker {capability}",
            operation,
            $"docker.{capability.ToString().ToLowerInvariant()}:local",
            ConsentRisk.Dangerous,
            allowRemember: false,
            paneId,
            preApprovedBy: _PreApprovedFor(toolName));
    }

    // ReadFree frees a read-classified tool; AllFree frees every tool; AlwaysAsk (the default) never pre-approves.
    private string? _PreApprovedFor(string toolName)
    {
        var mode = settings.ConsentMode;
        if (mode == DockerConsentMode.AlwaysAsk)
        {
            return null;
        }

        var freed = mode == DockerConsentMode.AllFree || DockerToolAccess.IsRead(toolName);
        return freed ? $"daemon mode: {_ModeLabel(mode)}" : null;
    }

    private static string _ModeLabel(DockerConsentMode mode) =>
        mode == DockerConsentMode.ReadFree ? "read-free" : "all-free";

    private async Task<GateResult> _RequestAsync(string title, string operation, string scope, ConsentRisk risk, bool allowRemember, string? paneId, IReadOnlyList<string>? detailLines = null, string? preApprovedBy = null)
    {
        var request = new ConsentRequest(
            Title: title,
            // Rendered verbatim; parts are agent-supplied, so flatten each fragment to a single bounded line with
            // control chars escaped, then join with a real newline.
            Action: _ComposeAction(operation, detailLines),
            Source: new ConsentSource(paneId, PluginId: null, Label: SourceLabel),
            Scope: scope,
            Risk: risk,
            AllowRemember: allowRemember,
            PreApprovedBy: preApprovedBy);

        ConsentDecision decision;
        try
        {
            decision = await host.RequestConsentAsync(request);
        }
        catch (Exception)
        {
            // Fail closed: a consent gate that errors must deny, never fall through to the daemon.
            return GateResult.Deny("The operator did not approve this Docker action.");
        }

        if (!decision.IsApproved)
        {
            return GateResult.Deny("The operator did not approve this Docker action.");
        }

        return decision.Bypassed
            ? new GateResult(true, null, $"Executed without asking — {preApprovedBy}.")
            : GateResult.Allow;
    }

    // AC-1062: escapes each fragment on its own — the operation summary, then each detail line — before joining
    // with a real newline, instead of joining first and escaping the whole body. Not shared with
    // ClusterAccessGate/ProxmoxAccessGate — same shape, on purpose.
    private static string _ComposeAction(string operation, IReadOnlyList<string>? detailLines) =>
        detailLines is null or { Count: 0 }
            ? _SingleLine(operation)
            : string.Join('\n', new[] { operation }.Concat(detailLines).Select(_SingleLine));

    // Rendered verbatim to the operator; parts (a container name, a command) are agent-supplied. Escape line breaks
    // and tabs VISIBLY and neutralize every other control character, keeping each fragment a single bounded line —
    // an agent cannot smuggle extra lines into, or hide part of, what the operator approves.
    private static string _SingleLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(char.IsControl(ch) ? ' ' : ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
