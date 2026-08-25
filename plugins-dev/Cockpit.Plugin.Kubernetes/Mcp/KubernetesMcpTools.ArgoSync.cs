using System.ComponentModel;
using ModelContextProtocol.Server;
using k8s;
using k8s.Models;
using Cockpit.Plugin.Kubernetes.Argo;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 5: the one tool in this ticket that rolls out a real change. Writes `operation.sync` through
// the plain K8s API, gated through AuthorizeNamespacedMutationAsync (a real cluster change, not argo_refresh's
// scope), and the approval shows the literal per-resource diff from Argo's own managed-resources API first.
internal sealed partial class KubernetesMcpTools
{
    [McpServerTool(Name = "argo_sync")]
    [Description("Syncs an Argo CD Application: applies the changes needed to bring the cluster back in line with Git. Requires an Argo CD API token configured for this cluster in the plugin settings — without one there is no way to show what would change, and this refuses to ask for approval without a diff to show. The operator's approval shows the literal per-resource diff read from Argo's managed-resources API. Does not prune resources Git no longer has and does not force anything.")]
    public async Task<string> ArgoSync(
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

        var argoToken = settings.GetArgoToken(registration.Id);
        if (argoToken is null)
        {
            return McpText.Error($"No Argo CD API token is configured for cluster \"{registration.Label}\". argo_sync refuses to ask for approval without a diff to show — add a read-only Argo CD project-role token for this cluster in the Kubernetes plugin settings.");
        }

        return await _WithClient(registration, (client, token) => _SyncAsync(client, registration, session, @namespace, name, argoToken, token), cancellationToken);
    }

    private async Task<string> _SyncAsync(
        IKubernetes client, ClusterRegistration registration, string session, string @namespace, string name, string argoToken, CancellationToken cancellationToken)
    {
        var (managedResources, error) = await ArgoApiClient.GetAsync(client, @namespace, argoToken, $"api/v1/applications/{name}/managed-resources", cancellationToken);
        if (managedResources is null)
        {
            return McpText.Error(error!);
        }

        var (diffLines, modifiedCount) = ArgoManagedResourcesDiff.Summarize(managedResources, MaxConsentDiffLength);
        if (modifiedCount == 0)
        {
            return McpText.Ok(new { ok = true, application = name, @namespace, synced = false, note = "No resources differ from Git — nothing to sync." });
        }

        var operation = $"sync Argo CD Application \"{name}\" in namespace \"{@namespace}\" — applies the changes below to bring the cluster back in line with Git";
        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, operation, session, diffLines);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
        await generic.PatchNamespacedAsync<RawKubernetesObject>(new V1Patch(ArgoSyncOperation.PatchJson, V1Patch.PatchType.MergePatch), @namespace, name, cancel: cancellationToken);

        return McpText.Ok(new
        {
            ok = true,
            application = name,
            @namespace,
            synced = true,
            resourcesChanged = modifiedCount,
            note = "Argo CD applies these changes asynchronously — call argo_last_sync to see the outcome.",
        });
    }
}
