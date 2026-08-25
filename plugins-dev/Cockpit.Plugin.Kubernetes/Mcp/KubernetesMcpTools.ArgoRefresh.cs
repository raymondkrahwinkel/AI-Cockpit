using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 4: the first Argo tool that writes anything. Sets `argocd.argoproj.io/refresh` (types.go:3329),
// which only makes Argo re-read Git and update status — nothing is deployed or changed. Gated through its own
// AuthorizeArgoRefreshAsync scope, not the generic mutation bucket a real change would use.
internal sealed partial class KubernetesMcpTools
{
    private const string ArgoRefreshAnnotation = "argocd.argoproj.io/refresh";

    [McpServerTool(Name = "argo_refresh")]
    [Description("Asks Argo CD to re-read Git and refresh an Application's status now, instead of waiting for its normal poll interval. This changes nothing on the cluster — no resource is deployed, updated or deleted — it only makes sync/health status current. Set hard to true to also bypass Argo's manifest cache (normal is enough for almost every case). The operator must approve; Argo removes the annotation itself once it has reconciled.")]
    public async Task<string> ArgoRefresh(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the Application lives in, e.g. \"argocd\".")] string @namespace,
        [Description("The Application name.")] string name,
        [Description("Also bypass Argo's manifest cache (normal, the default, is enough for almost every case).")] bool hard = false,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var refreshKind = hard ? "hard" : "normal";
        var operation = $"refresh Argo CD Application \"{name}\" in namespace \"{@namespace}\" ({refreshKind}) — this only makes Argo re-read Git and update sync/health status; nothing is deployed, updated or deleted on the cluster";
        var decision = await gate.AuthorizeArgoRefreshAsync(registration, @namespace, operation, session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, ArgoGroup, ArgoVersion, ArgoApplicationPlural, disposeClient: false);
            var patch = new JsonObject { ["metadata"] = new JsonObject { ["annotations"] = new JsonObject { [ArgoRefreshAnnotation] = refreshKind } } };
            var patchJson = patch.ToJsonString();
            await generic.PatchNamespacedAsync<RawKubernetesObject>(new V1Patch(patchJson, V1Patch.PatchType.MergePatch), @namespace, name, cancel: token);
            return McpText.Ok(new
            {
                ok = true,
                application = name,
                @namespace,
                refreshRequested = refreshKind,
                note = "Argo CD re-reads Git and updates status; nothing was deployed or changed. It removes this annotation itself once it has reconciled.",
            });
        }, cancellationToken);
    }
}
