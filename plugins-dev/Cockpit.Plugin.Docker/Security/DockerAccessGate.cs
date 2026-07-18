using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Docker.Security;

/// <summary>
/// The single policy chokepoint in front of the Docker daemon (AC-84), mirroring the Kubernetes plugin's
/// <c>ClusterAccessGate</c>. Every daemon-touching MCP tool routes through here before it does anything.
///
/// <para>Policy (§12.2 defaults — local daemon only, no per-container jail v1):</para>
/// <list type="bullet">
///   <item>Connection — the first touch of the daemon asks once, LowRisk, remembered per pane. Reads are free after that.</item>
///   <item>Mutation — start/stop/remove/run and other changes always ask afresh, Dangerous, never remembered, with the literal command shown.</item>
///   <item>Danger capability (exec) — blocked with a settings hint unless the operator turned it on; then asks afresh, Dangerous, never remembered.</item>
/// </list>
/// The <see cref="ConsentRequest.Action"/> is rendered verbatim to the operator, and parts of it (a container name, a
/// command) are agent-supplied, so it is flattened to a single line — an agent cannot smuggle extra lines into the
/// consent body.
/// </summary>
internal sealed class DockerAccessGate(ICockpitHost host)
{
    private const string SourceLabel = "Docker";

    /// <summary>Authorize touching the daemon at all. LowRisk, remembered per pane — asks once, then reads are free.</summary>
    public Task<GateResult> AuthorizeConnectionAsync(string operation, string? paneId) =>
        _RequestAsync(
            "Connect to the Docker daemon",
            operation,
            "docker.connect:local",
            ConsentRisk.LowRisk,
            allowRemember: true,
            paneId);

    /// <summary>Authorize a change to a Docker resource. Layered on connection auth, then always Dangerous and never remembered.</summary>
    public async Task<GateResult> AuthorizeMutationAsync(string operation, string? paneId)
    {
        var connection = await AuthorizeConnectionAsync(operation, paneId);
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
            paneId);
    }

    /// <summary>
    /// Authorize a dangerous capability (exec/run). Blocked with a settings hint when the capability is off — a policy
    /// block, so no prompt is shown. When on: connection auth, then always Dangerous and never remembered.
    /// </summary>
    public async Task<GateResult> AuthorizeDangerAsync(DangerCapability capability, bool enabled, string operation, string? paneId)
    {
        if (!enabled)
        {
            return GateResult.Deny(
                $"The \"{capability}\" capability is off for the Docker daemon. Turn it on in the plugin settings first.");
        }

        var connection = await AuthorizeConnectionAsync(operation, paneId);
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
            paneId);
    }

    private async Task<GateResult> _RequestAsync(string title, string operation, string scope, ConsentRisk risk, bool allowRemember, string? paneId)
    {
        var request = new ConsentRequest(
            Title: title,
            // Rendered verbatim; parts are agent-supplied, so flatten to a single bounded line with control chars escaped.
            Action: _SingleLine(operation),
            Source: new ConsentSource(paneId, PluginId: null, Label: SourceLabel),
            Scope: scope,
            Risk: risk,
            AllowRemember: allowRemember);

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

        return decision.IsApproved
            ? GateResult.Allow
            : GateResult.Deny("The operator did not approve this Docker action.");
    }

    // Rendered verbatim to the operator; parts (a container name, a command) are agent-supplied. Escape line breaks
    // and tabs VISIBLY (so a multi-line command reads as multi-line and cannot be disguised as commented-out) and
    // neutralize every other control character, keeping the consent body a single bounded line — an agent cannot
    // smuggle extra lines into, or hide part of, what the operator approves.
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
