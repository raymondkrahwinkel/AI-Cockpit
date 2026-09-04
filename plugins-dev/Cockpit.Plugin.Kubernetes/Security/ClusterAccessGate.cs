using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Security;

// The one place the security policy lives (AC-80). Every MCP tool routes through here before it touches a cluster,
// so the rules are decided once, in one file, and tested without a real cluster. It asks the host's shared consent
// gate (`ICockpitHost.RequestConsentAsync`): the operator sees the literal action and chooses, the
// gate fails closed, and every decision is audited — none of which the plugin has to build.
//
// The matrix: opening a cluster asks once and may be remembered for the session; a namespace on the cluster's
// allowed list is free, one outside it asks each session (reads included); a mutation asks afresh every time and
// is never remembered; cluster-scoped resources and exec/port-forward/attach are blocked outright until the
// operator turns them on per cluster, and even then a mutation or a danger action asks afresh.
// The consent surface renders the action verbatim, so callers pass a description built from the real verb and the
// real parameters (never agent-supplied free text): a delete tool that asks "delete pod …" cannot be talked into
// showing "get pod …", because the tool it came from chose the verb, not the agent.
internal sealed class ClusterAccessGate(ICockpitHost host)
{
    private const string SourceLabel = "Kubernetes";

    // A read against a namespaced resource: needs an open connection and the namespace to be in the jail (or consented).
    public async Task<GateResult> AuthorizeNamespacedReadAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        var connection = await _AuthorizeConnectionAsync(cluster, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _AuthorizeNamespaceAsync(cluster, @namespace, operation, paneId);
    }

    // A change to a namespaced resource: connection, namespace jail, then an always-fresh Dangerous consent.
    // `detailLines` (AC-1062) is the multi-line ingress: each line is escaped on its own and joined with a real
    // newline, rather than the whole composed body being flattened as one — see `_ComposeAction`.
    public async Task<GateResult> AuthorizeNamespacedMutationAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId, IReadOnlyList<string>? detailLines = null)
    {
        var namespaced = await AuthorizeNamespacedReadAsync(cluster, @namespace, operation, paneId);
        if (!namespaced.IsAllowed)
        {
            return namespaced;
        }

        return await _AuthorizeMutationAsync(cluster, operation, paneId, detailLines);
    }

    // A read against a cluster-scoped resource (nodes, PVs, namespaces): blocked unless the cluster opted in, then
    // consented. `resourceKey` (group/plural) scopes a remembered approval to the kind that was
    // actually shown — approving "read nodes" must not silently authorize clusterroles or persistentvolumes too.
    public async Task<GateResult> AuthorizeClusterScopedReadAsync(ClusterRegistration cluster, string resourceKey, string operation, string? paneId)
    {
        if (!cluster.AllowClusterScoped)
        {
            return GateResult.Deny($"Cluster-scoped resources are off for cluster \"{cluster.Label}\". Turn on cluster-scoped access for it in the Kubernetes plugin settings to reach nodes, persistent volumes, namespaces and the like.");
        }

        var connection = await _AuthorizeConnectionAsync(cluster, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _RequestAsync(
            title: "Kubernetes: read a cluster-scoped resource",
            operation: operation,
            clusterLabel: cluster.Label,
            // Per-kind, not per-cluster: a remembered approval binds to the exact kind shown, so it cannot carry over
            // to a different cluster-scoped kind the operator never saw.
            scope: $"k8s.clusterscoped:{cluster.Id}:{resourceKey}",
            risk: ConsentRisk.LowRisk,
            allowRemember: true,
            paneId: paneId);
    }

    // A change to a cluster-scoped resource: opt-in, connection, then an always-fresh Dangerous consent.
    public async Task<GateResult> AuthorizeClusterScopedMutationAsync(ClusterRegistration cluster, string resourceKey, string operation, string? paneId)
    {
        var read = await AuthorizeClusterScopedReadAsync(cluster, resourceKey, operation, paneId);
        if (!read.IsAllowed)
        {
            return read;
        }

        return await _AuthorizeMutationAsync(cluster, operation, paneId);
    }

    // exec, port-forward or attach: blocked unless the capability is on for the cluster, then the namespace jail
    // applies and the action asks afresh every time. These sit apart because they hand out a shell or a tunnel
    // that reaches past the namespace RBAC the read/mutate tools rely on.
    public async Task<GateResult> AuthorizeDangerAsync(ClusterRegistration cluster, DangerCapability capability, string @namespace, string operation, string? paneId)
    {
        if (!_IsCapabilityEnabled(cluster, capability))
        {
            return GateResult.Deny($"{capability} is off for cluster \"{cluster.Label}\". Turn it on for this cluster in the Kubernetes plugin settings first — it is off by default because it can reach past the namespace boundary.");
        }

        var namespaced = await AuthorizeNamespacedReadAsync(cluster, @namespace, operation, paneId);
        if (!namespaced.IsAllowed)
        {
            return namespaced;
        }

        return await _RequestAsync(
            title: $"Kubernetes: {capability} — this reaches past the namespace boundary",
            operation: operation,
            clusterLabel: cluster.Label,
            scope: $"k8s.{capability.ToString().ToLowerInvariant()}:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);
    }

    // A read of credential material (a secret) in a namespaced resource: the namespace jail applies, and then —
    // even inside an allowed namespace — reading the contents asks afresh as Dangerous and is never remembered, so
    // "free to read in an allowed namespace" does not silently include secrets (security review F2).
    public async Task<GateResult> AuthorizeSensitiveNamespacedReadAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        var namespaced = await AuthorizeNamespacedReadAsync(cluster, @namespace, operation, paneId);
        if (!namespaced.IsAllowed)
        {
            return namespaced;
        }

        return await _RequestAsync(
            title: "Kubernetes: read credential material",
            operation: operation,
            clusterLabel: cluster.Label,
            scope: $"k8s.secret:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);
    }

    // AC-576 phase 4: a refresh is idempotent and changes nothing by itself (Argo just re-reads Git and updates
    // status), but it is still a write, so it gets its own scope — separate from the generic mutation bucket a
    // real change (e.g. a future argo_sync) would share — and still asks Dangerous, never remembered.
    public async Task<GateResult> AuthorizeArgoRefreshAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        var namespaced = await AuthorizeNamespacedReadAsync(cluster, @namespace, operation, paneId);
        if (!namespaced.IsAllowed)
        {
            return namespaced;
        }

        return await _RequestAsync(
            title: "Kubernetes: refresh an Argo CD Application",
            operation: operation,
            clusterLabel: cluster.Label,
            scope: $"k8s.argo.refresh:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);
    }

    private async Task<GateResult> _AuthorizeConnectionAsync(ClusterRegistration cluster, string? paneId) =>
        await _RequestAsync(
            title: "Kubernetes: open a connection to a cluster",
            // Exec-auth contexts run an external credential command on connect — state that on the runtime prompt an
            // agent triggers, not only in the settings UI where the cluster was registered.
            operation: cluster.UsesExecAuth
                ? $"Connect to cluster \"{cluster.Label}\" ({_ContextDisplay(cluster)}) — connecting runs an external credential command from the kubeconfig"
                : $"Connect to cluster \"{cluster.Label}\" ({_ContextDisplay(cluster)})",
            clusterLabel: cluster.Label,
            scope: $"k8s.connect:{cluster.Id}",
            risk: ConsentRisk.LowRisk,
            allowRemember: true,
            paneId: paneId);

    private async Task<GateResult> _AuthorizeNamespaceAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        if (cluster.IsNamespaceAllowed(@namespace))
        {
            return GateResult.Allow;
        }

        return await _RequestAsync(
            title: "Kubernetes: reach a namespace outside the allowed list",
            operation: $"{operation} — namespace \"{@namespace}\" is not on the allowed list for cluster \"{cluster.Label}\"",
            clusterLabel: cluster.Label,
            scope: $"k8s.namespace:{cluster.Id}:{@namespace}",
            risk: ConsentRisk.LowRisk,
            allowRemember: true,
            paneId: paneId);
    }

    private Task<GateResult> _AuthorizeMutationAsync(ClusterRegistration cluster, string operation, string? paneId, IReadOnlyList<string>? detailLines = null) =>
        _RequestAsync(
            title: "Kubernetes: change a resource",
            operation: operation,
            clusterLabel: cluster.Label,
            scope: $"k8s.mutate:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId,
            detailLines: detailLines);

    private async Task<GateResult> _RequestAsync(string title, string operation, string clusterLabel, string scope, ConsentRisk risk, bool allowRemember, string? paneId, IReadOnlyList<string>? detailLines = null)
    {
        var request = new ConsentRequest(
            Title: title,
            // The Action is rendered verbatim and parts of it (a pod name, a command, a patch) are agent-supplied.
            // Collapse control characters so an agent cannot smuggle newlines into the consent body and pad it with
            // reassuring extra lines — the operator must see one clearly-bounded line, or (with detailLines) a
            // clearly-bounded set of them.
            Action: _ComposeAction(operation, detailLines),
            Source: new ConsentSource(paneId, PluginId: null, Label: SourceLabel),
            Scope: scope,
            Risk: risk,
            AllowRemember: allowRemember);

        var decision = await host.RequestConsentAsync(request);
        return decision.IsApproved
            ? GateResult.Allow
            : GateResult.Deny($"The operator did not approve this action on cluster \"{clusterLabel}\".");
    }

    // AC-1062: escapes each fragment on its own — the operation summary, then each detail line — before joining
    // with a real newline, instead of joining first and escaping the whole body (which is what let a plugin-computed
    // diff collapse to one line). Not shared with DockerAccessGate/ProxmoxAccessGate — same shape, on purpose.
    private static string _ComposeAction(string operation, IReadOnlyList<string>? detailLines) =>
        detailLines is null or { Count: 0 }
            ? _SingleLine(operation)
            : string.Join('\n', new[] { operation }.Concat(detailLines).Select(_SingleLine));

    // Rendered verbatim to the operator; parts (a pod name, a command, a patch) are agent-supplied. Escape line
    // breaks and tabs VISIBLY (echo hi #harmless\nrm -rf /data must not collapse to one reassuring line) and
    // neutralize every other control character, keeping each fragment one bounded line. Mirrors DockerAccessGate (AC-92).
    private static string _SingleLine(string operation)
    {
        var builder = new StringBuilder(operation.Length);
        foreach (var character in operation)
        {
            switch (character)
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
                    builder.Append(char.IsControl(character) ? ' ' : character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool _IsCapabilityEnabled(ClusterRegistration cluster, DangerCapability capability) => capability switch
    {
        DangerCapability.Exec => cluster.AllowExec,
        DangerCapability.PortForward => cluster.AllowPortForward,
        DangerCapability.Attach => cluster.AllowAttach,
        _ => false,
    };

    private static string _ContextDisplay(ClusterRegistration cluster) =>
        string.IsNullOrWhiteSpace(cluster.ContextName) ? "current context" : $"context {cluster.ContextName}";
}
