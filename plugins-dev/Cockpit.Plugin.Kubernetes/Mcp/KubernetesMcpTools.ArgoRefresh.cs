using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-576 phase 4: the first Argo tool that writes anything. Sets `argocd.argoproj.io/refresh` (types.go:3329)
// on the Application itself — Argo removes it once reconciled — and rolls out, updates or deletes nothing.
// Gated through its own AuthorizeArgoRefreshAsync scope, not the generic mutation bucket a real change would use.
internal sealed partial class KubernetesMcpTools
{
    private const string ArgoRefreshAnnotation = "argocd.argoproj.io/refresh";

    [McpServerTool(Name = "argo_refresh", ReadOnly = false, Destructive = false)]
    [Description("Asks Argo CD to re-read Git and refresh an Application's status now, instead of waiting for its normal poll interval. This sets a refresh annotation on the Application itself, which Argo CD removes once it has reconciled — no resource is rolled out, updated or deleted. Set hard to true to also bypass Argo's manifest cache (normal is enough for almost every case). The operator must approve.")]
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
        var operation = $"refresh Argo CD Application \"{name}\" in namespace \"{@namespace}\" ({refreshKind}) — this sets a refresh annotation on the Application, which Argo CD removes itself once it has reconciled; no resource is rolled out, updated or deleted";
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
                note = "Argo CD re-reads Git and updates status; no resource was rolled out, updated or deleted. Argo removes this annotation itself once it has reconciled.",
            });
        }, cancellationToken);
    }
}
