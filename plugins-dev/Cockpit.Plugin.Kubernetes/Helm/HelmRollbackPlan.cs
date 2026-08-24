using System.Net;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Helm;

// What happened to one resource in a rollback.
internal sealed record ManifestApplyResult(string Resource, string Action, string? Error);

// One resource of a rollback, resolved against the apiserver: what the manifest says, and the REST plural and scope
// discovery says it is.
internal sealed record PlannedResource(ManifestResourceChange Change, string Plural, bool Namespaced);

// Turns a manifest diff into the calls that carry it out (AC-1061 fase 2). Everything that can refuse the rollback
// is decided in `ResolveAsync`, before the operator is asked: a kind the cluster does not know, a resource outside
// the release namespace, a cluster-scoped resource on a cluster where that is off. Nothing half-approved.
internal sealed class HelmRollbackPlan(IKubernetes client, string releaseNamespace, IReadOnlyList<PlannedResource> resources)
{
    // Every write claims helm's own field manager: the apiserver only reconciles an Update entry with helm's
    // server-side Apply when the manager name matches. Measured on a cluster — under any other name the next
    // `helm rollback` fails with `conflict with "unknown"`.
    private const string FieldManager = "helm";

    public IReadOnlyList<PlannedResource> Resources => resources;

    public static async Task<(HelmRollbackPlan? Plan, string? Error)> ResolveAsync(
        IKubernetes client, ManifestDiff diff, string releaseNamespace, bool allowClusterScoped, CancellationToken cancellationToken)
    {
        var catalog = new ApiResourceCatalog(client);
        var planned = new List<PlannedResource>();
        foreach (var change in diff.Applied.Concat(diff.Deletions))
        {
            var document = change.Document;
            if (document.Namespace is { } declared && declared != releaseNamespace)
            {
                // The approval and the namespace jail are both about one namespace; a chart that renders into a
                // second one would reach past what the operator was shown, so refuse rather than widen the prompt.
                return (null, $"{document.Display} is rendered into namespace \"{declared}\", not the release namespace \"{releaseNamespace}\". Roll this release back with helm itself.");
            }

            var resource = await catalog.ResolveAsync(document.ApiVersion, document.Kind, cancellationToken);
            if (resource is not { } resolved)
            {
                return (null, $"The cluster does not serve {document.ApiVersion} {document.Kind} ({document.Display}), so this rollback cannot be applied. A CustomResourceDefinition the release itself installs has to exist before the resources that use it.");
            }

            if (!resolved.Namespaced && !allowClusterScoped)
            {
                return (null, $"{document.Display} is cluster-scoped and cluster-scoped resources are off for this cluster. Turn cluster-scoped access on for it in the Kubernetes plugin settings, or roll this release back with helm itself.");
            }

            planned.Add(new PlannedResource(change, resolved.Plural, resolved.Namespaced));
        }

        return (new HelmRollbackPlan(client, releaseNamespace, planned), null);
    }

    // Applies the target manifest, then removes what the target revision no longer has. Every resource is attempted:
    // a rollback is not a transaction, and stopping at the first refusal would leave a state that is neither
    // revision AND hide the rest of what is wrong from the operator.
    public async Task<IReadOnlyList<ManifestApplyResult>> ApplyAsync(CancellationToken cancellationToken)
    {
        var results = new List<ManifestApplyResult>();
        foreach (var resource in resources.Where(candidate => candidate.Change.Change != ManifestChangeKind.Deleted))
        {
            results.Add(await _ApplyAsync(resource, cancellationToken));
        }

        foreach (var resource in resources.Where(candidate => candidate.Change.Change == ManifestChangeKind.Deleted))
        {
            results.Add(await _DeleteAsync(resource, cancellationToken));
        }

        return results;
    }

    private async Task<ManifestApplyResult> _ApplyAsync(PlannedResource resource, CancellationToken cancellationToken)
    {
        var document = resource.Change.Document;
        if (document.ToJson() is not { } json)
        {
            return new ManifestApplyResult(document.Display, "apply", "The rendered document is not a YAML mapping.");
        }

        var reference = ApiVersionRef.Parse(document.ApiVersion);
        try
        {
            // A JSON merge patch, not a server-side apply: it sets what the target revision spells out and leaves
            // every other field alone, so no controller loses a field it owns. The cost is in the tool description.
            var patch = new V1Patch(json.ToJsonString(), V1Patch.PatchType.MergePatch);
            _ = resource.Namespaced
                ? await client.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(patch, reference.Group, reference.Version, releaseNamespace, resource.Plural, document.Name, fieldManager: FieldManager, cancellationToken: cancellationToken)
                : await client.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(patch, reference.Group, reference.Version, resource.Plural, document.Name, fieldManager: FieldManager, cancellationToken: cancellationToken);
            return new ManifestApplyResult(document.Display, "updated", null);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return await _CreateAsync(reference, resource, json, cancellationToken);
        }
        catch (HttpOperationException exception)
        {
            return new ManifestApplyResult(document.Display, "apply", _Describe(exception));
        }
    }

    private async Task<ManifestApplyResult> _CreateAsync(ApiVersionRef reference, PlannedResource resource, JsonObject json, CancellationToken cancellationToken)
    {
        var document = resource.Change.Document;
        try
        {
            _ = resource.Namespaced
                ? await client.CustomObjects.CreateNamespacedCustomObjectWithHttpMessagesAsync(json, reference.Group, reference.Version, releaseNamespace, resource.Plural, fieldManager: FieldManager, cancellationToken: cancellationToken)
                : await client.CustomObjects.CreateClusterCustomObjectWithHttpMessagesAsync(json, reference.Group, reference.Version, resource.Plural, fieldManager: FieldManager, cancellationToken: cancellationToken);
            return new ManifestApplyResult(document.Display, "created", null);
        }
        catch (HttpOperationException exception)
        {
            return new ManifestApplyResult(document.Display, "create", _Describe(exception));
        }
    }

    private async Task<ManifestApplyResult> _DeleteAsync(PlannedResource resource, CancellationToken cancellationToken)
    {
        var document = resource.Change.Document;
        var reference = ApiVersionRef.Parse(document.ApiVersion);
        try
        {
            if (resource.Namespaced)
            {
                await client.CustomObjects.DeleteNamespacedCustomObjectWithHttpMessagesAsync(reference.Group, reference.Version, releaseNamespace, resource.Plural, document.Name, cancellationToken: cancellationToken);
            }
            else
            {
                await client.CustomObjects.DeleteClusterCustomObjectWithHttpMessagesAsync(reference.Group, reference.Version, resource.Plural, document.Name, cancellationToken: cancellationToken);
            }

            return new ManifestApplyResult(document.Display, "deleted", null);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new ManifestApplyResult(document.Display, "already gone", null);
        }
        catch (HttpOperationException exception)
        {
            return new ManifestApplyResult(document.Display, "delete", _Describe(exception));
        }
    }

    // Status and reason only: the response body of a Kubernetes rejection can name the kubeconfig's user or
    // service account, which is the host detail the rest of the plugin keeps from the agent (security review F3).
    private static string _Describe(HttpOperationException exception) =>
        exception.Response is { } response ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : "the call failed";
}
