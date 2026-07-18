using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Security;

/// <summary>
/// The one place the security policy lives (AC-80). Every MCP tool routes through here before it touches a cluster,
/// so the rules are decided once, in one file, and tested without a real cluster. It asks the host's shared consent
/// gate (<see cref="ICockpitHost.RequestConsentAsync"/>): the operator sees the literal action and chooses, the
/// gate fails closed, and every decision is audited — none of which the plugin has to build.
/// <para>
/// The matrix: opening a cluster asks once and may be remembered for the session; a namespace on the cluster's
/// allowed list is free, one outside it asks each session (reads included); a mutation asks afresh every time and
/// is never remembered; cluster-scoped resources and exec/port-forward/attach are blocked outright until the
/// operator turns them on per cluster, and even then a mutation or a danger action asks afresh.
/// </para>
/// </summary>
/// <remarks>
/// The consent surface renders the action verbatim, so callers pass a description built from the real verb and the
/// real parameters (never agent-supplied free text): a delete tool that asks "delete pod …" cannot be talked into
/// showing "get pod …", because the tool it came from chose the verb, not the agent.
/// </remarks>
internal sealed class ClusterAccessGate(ICockpitHost host)
{
    private const string SourceLabel = "Kubernetes";

    /// <summary>A read against a namespaced resource: needs an open connection and the namespace to be in the jail (or consented).</summary>
    public async Task<GateResult> AuthorizeNamespacedReadAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        var connection = await _AuthorizeConnectionAsync(cluster, paneId);
        if (!connection.IsAllowed)
        {
            return connection;
        }

        return await _AuthorizeNamespaceAsync(cluster, @namespace, operation, paneId);
    }

    /// <summary>A change to a namespaced resource: connection, namespace jail, then an always-fresh Dangerous consent.</summary>
    public async Task<GateResult> AuthorizeNamespacedMutationAsync(ClusterRegistration cluster, string @namespace, string operation, string? paneId)
    {
        var namespaced = await AuthorizeNamespacedReadAsync(cluster, @namespace, operation, paneId);
        if (!namespaced.IsAllowed)
        {
            return namespaced;
        }

        return await _AuthorizeMutationAsync(cluster, operation, paneId);
    }

    /// <summary>A read against a cluster-scoped resource (nodes, PVs, namespaces): blocked unless the cluster opted in, then consented.</summary>
    public async Task<GateResult> AuthorizeClusterScopedReadAsync(ClusterRegistration cluster, string operation, string? paneId)
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
            cluster: cluster,
            scope: $"k8s.clusterscoped:{cluster.Id}",
            risk: ConsentRisk.LowRisk,
            allowRemember: true,
            paneId: paneId);
    }

    /// <summary>A change to a cluster-scoped resource: opt-in, connection, then an always-fresh Dangerous consent.</summary>
    public async Task<GateResult> AuthorizeClusterScopedMutationAsync(ClusterRegistration cluster, string operation, string? paneId)
    {
        var read = await AuthorizeClusterScopedReadAsync(cluster, operation, paneId);
        if (!read.IsAllowed)
        {
            return read;
        }

        return await _AuthorizeMutationAsync(cluster, operation, paneId);
    }

    /// <summary>
    /// exec, port-forward or attach: blocked unless the capability is on for the cluster, then the namespace jail
    /// applies and the action asks afresh every time. These sit apart because they hand out a shell or a tunnel
    /// that reaches past the namespace RBAC the read/mutate tools rely on.
    /// </summary>
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
            cluster: cluster,
            scope: $"k8s.{capability.ToString().ToLowerInvariant()}:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);
    }

    /// <summary>
    /// A read of credential material (a secret) in a namespaced resource: the namespace jail applies, and then —
    /// even inside an allowed namespace — reading the contents asks afresh as Dangerous and is never remembered, so
    /// "free to read in an allowed namespace" does not silently include secrets (security review F2).
    /// </summary>
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
            cluster: cluster,
            scope: $"k8s.secret:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);
    }

    private async Task<GateResult> _AuthorizeConnectionAsync(ClusterRegistration cluster, string? paneId) =>
        await _RequestAsync(
            title: "Kubernetes: open a connection to a cluster",
            operation: $"Connect to cluster \"{cluster.Label}\" ({_ContextDisplay(cluster)})",
            cluster: cluster,
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
            cluster: cluster,
            scope: $"k8s.namespace:{cluster.Id}:{@namespace}",
            risk: ConsentRisk.LowRisk,
            allowRemember: true,
            paneId: paneId);
    }

    private Task<GateResult> _AuthorizeMutationAsync(ClusterRegistration cluster, string operation, string? paneId) =>
        _RequestAsync(
            title: "Kubernetes: change a resource",
            operation: operation,
            cluster: cluster,
            scope: $"k8s.mutate:{cluster.Id}",
            risk: ConsentRisk.Dangerous,
            allowRemember: false,
            paneId: paneId);

    private async Task<GateResult> _RequestAsync(string title, string operation, ClusterRegistration cluster, string scope, ConsentRisk risk, bool allowRemember, string? paneId)
    {
        var request = new ConsentRequest(
            Title: title,
            Action: operation,
            Source: new ConsentSource(paneId, PluginId: null, Label: SourceLabel),
            Scope: scope,
            Risk: risk,
            AllowRemember: allowRemember);

        var decision = await host.RequestConsentAsync(request);
        return decision.IsApproved
            ? GateResult.Allow
            : GateResult.Deny($"The operator did not approve this action on cluster \"{cluster.Label}\".");
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
