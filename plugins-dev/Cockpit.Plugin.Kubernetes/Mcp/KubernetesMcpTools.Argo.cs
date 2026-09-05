using System.ComponentModel;
using ModelContextProtocol.Server;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 1: reads Argo CD's GitOps state straight from the Application CRD via the Kubernetes API the
// plugin already has — no Argo token, no binary. An Application is not credential material (unlike a Helm
// release secret), so these use the plain namespaced read gate, same as get_resource on an ordinary resource.
internal sealed partial class KubernetesMcpTools
{
    private const string ArgoGroup = "argoproj.io";
    private const string ArgoVersion = "v1alpha1";
    private const string ArgoApplicationPlural = "applications";

    [McpServerTool(Name = "argo_apps", ReadOnly = true)]
    [Description("Lists Argo CD Applications in a namespace — usually \"argocd\", not the namespace they deploy to. Each entry: name, project, sync status, health, sourceType (Helm/Kustomize/Directory), the current revision (abbreviated) and how many of its resources are OutOfSync. Reads the Application CRD directly — no Argo token, no Argo API call.")]
    public async Task<string> ArgoApps(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace Argo CD Applications live in, e.g. \"argocd\" — not the namespace they deploy to.")] string @namespace,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeNamespacedReadAsync(registration, @namespace, $"list Argo CD Applications in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
            var list = await generic.ListNamespacedAsync<RawKubernetesList>(@namespace, limit: ListPageLimit, cancel: token);
            return McpText.Node(ArgoApplicationSummary.SummarizeList(list));
        }, cancellationToken);
    }

    [McpServerTool(Name = "argo_app", ReadOnly = true)]
    [Description("Reads one Argo CD Application: its source (repo, path, targetRevision), destination, whether syncPolicy has auto-sync/selfHeal on, sync status, health, sourceType, and per resource kind/name/sync status/health. Reads the Application CRD directly — no Argo token, no Argo API call.")]
    public async Task<string> ArgoApp(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the Application lives in, e.g. \"argocd\".")] string @namespace,
        [Description("The Application name.")] string name,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeNamespacedReadAsync(registration, @namespace, $"read Argo CD Application \"{name}\" in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
            var app = await generic.ReadNamespacedAsync<RawKubernetesObject>(@namespace, name, cancel: token);
            return McpText.Node(ArgoApplicationSummary.SummarizeApp(app));
        }, cancellationToken);
    }

    [McpServerTool(Name = "argo_history", ReadOnly = true)]
    [Description("Lists the revisions Argo CD has deployed for an Application — commit revision, when, and who or what initiated it. Use this to find the commit to `git revert` to. `sync.revision` on the Application is only the last revision that was rolled out, not necessarily what is in Git right now (see argo_apps/argo_app). Reads the Application CRD directly.")]
    public async Task<string> ArgoHistory(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the Application lives in, e.g. \"argocd\".")] string @namespace,
        [Description("The Application name.")] string name,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeNamespacedReadAsync(registration, @namespace, $"read Argo CD Application \"{name}\" history in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
            var app = await generic.ReadNamespacedAsync<RawKubernetesObject>(@namespace, name, cancel: token);
            return McpText.Node(ArgoApplicationSummary.SummarizeHistory(app));
        }, cancellationToken);
    }

    [McpServerTool(Name = "argo_last_sync", ReadOnly = true)]
    [Description("Reads what the last sync operation on an Argo CD Application did: phase, message, start/finish time, who initiated it, and per resource the literal line from Argo's own sync result. Reads the Application CRD directly — no Argo token, no Argo API call.")]
    public async Task<string> ArgoLastSync(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the Application lives in, e.g. \"argocd\".")] string @namespace,
        [Description("The Application name.")] string name,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeNamespacedReadAsync(registration, @namespace, $"read Argo CD Application \"{name}\" last sync in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
            var app = await generic.ReadNamespacedAsync<RawKubernetesObject>(@namespace, name, cancel: token);
            return McpText.Node(ArgoApplicationSummary.SummarizeLastSync(app));
        }, cancellationToken);
    }
}
