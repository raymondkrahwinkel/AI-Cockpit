using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Proxmox.Security;

// The single policy chokepoint in front of the Proxmox API (AC-1038), mirroring `DockerAccessGate`. Connecting asks
// once and is remembered per pane; every mutation asks afresh, Dangerous, never remembered — shutdown and stop stay
// separate calls with separate text, since Proxmox treats them as different operations.
internal sealed class ProxmoxAccessGate(ICockpitHost host)
{
    private const string SourceLabel = "Proxmox";

    // Authorize touching the API at all. LowRisk, remembered per pane — asks once, then reads are free.
    public Task<GateResult> AuthorizeConnectionAsync(string operation, string? paneId) =>
        _RequestAsync(
            "Connect to Proxmox",
            operation,
            "proxmox.connect:default",
            ConsentRisk.LowRisk,
            allowRemember: true,
            paneId);

    // Authorize a change (start/stop/shutdown/reboot/snapshot). Layered on connection auth, then always Dangerous and never remembered.
    // `detailLines` (AC-1062) is the multi-line ingress: each line is escaped on its own and joined with a real
    // newline, rather than the whole composed body being flattened as one — see `_ComposeAction`.
    public async Task<GateResult> AuthorizeMutationAsync(string operation, string? paneId, IReadOnlyList<string>? detailLines = null)
    {
        var connection = await AuthorizeConnectionAsync(operation, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _RequestAsync(
            "Change a Proxmox VM or LXC container",
            operation,
            "proxmox.mutate:default",
            ConsentRisk.Dangerous,
            allowRemember: false,
            paneId,
            detailLines);
    }

    // Authorize a dangerous capability (rollback/delete). Blocked with a settings hint when off — a policy block,
    // so no prompt is shown. When on: connection auth, then always Dangerous and never remembered.
    public async Task<GateResult> AuthorizeDangerAsync(DangerCapability capability, bool enabled, string operation, string? paneId)
    {
        if (!enabled)
        {
            return GateResult.Deny(
                $"\"{capability}\" is off for this Proxmox target. Turn it on in the plugin settings first.");
        }

        var connection = await AuthorizeConnectionAsync(operation, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _RequestAsync(
            $"Proxmox: {capability}",
            operation,
            $"proxmox.{capability.ToString().ToLowerInvariant()}:default",
            ConsentRisk.Dangerous,
            allowRemember: false,
            paneId);
    }

    private async Task<GateResult> _RequestAsync(string title, string operation, string scope, ConsentRisk risk, bool allowRemember, string? paneId, IReadOnlyList<string>? detailLines = null)
    {
        var request = new ConsentRequest(
            Title: title,
            // Rendered verbatim; parts are agent-supplied, so flatten each fragment to a single bounded line with
            // control chars escaped, then join with a real newline.
            Action: _ComposeAction(operation, detailLines),
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
            // Fail closed: a consent gate that errors must deny, never fall through to the API.
            return GateResult.Deny("The operator did not approve this Proxmox action.");
        }

        return decision.IsApproved
            ? GateResult.Allow
            : GateResult.Deny("The operator did not approve this Proxmox action.");
    }

    // AC-1062: escapes each fragment on its own — the operation summary, then each detail line — before joining
    // with a real newline, instead of joining first and escaping the whole body. Not shared with
    // ClusterAccessGate/DockerAccessGate — same shape, on purpose.
    private static string _ComposeAction(string operation, IReadOnlyList<string>? detailLines) =>
        detailLines is null or { Count: 0 }
            ? _SingleLine(operation)
            : string.Join('\n', new[] { operation }.Concat(detailLines).Select(_SingleLine));

    // Rendered verbatim to the operator; parts (a VM/LXC id, a snapshot name) are agent-supplied. Escape line breaks
    // and tabs VISIBLY and neutralize every other control character, keeping each fragment a single physical
    // line — an agent cannot smuggle extra lines into what the operator approves. Mirrors DockerAccessGate.
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
