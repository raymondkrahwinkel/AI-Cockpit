using System.Net;
using System.Text.Json;
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

        using var generic = _ClientFor(document, resource.Plural);
        try
        {
            // A JSON merge patch, not a server-side apply: it sets what the target revision spells out and leaves
            // every other field alone, so no controller loses a field it owns. The cost is in the tool description.
            var patch = new V1Patch(json.ToJsonString(), V1Patch.PatchType.MergePatch);
            _ = resource.Namespaced
                ? await generic.PatchNamespacedAsync<RawKubernetesObject>(patch, releaseNamespace, document.Name, cancellationToken)
                : await generic.PatchAsync<RawKubernetesObject>(patch, document.Name, cancellationToken);
            return new ManifestApplyResult(document.Display, "updated", null);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return await _CreateAsync(generic, resource, json, cancellationToken);
        }
        catch (HttpOperationException exception)
        {
            return new ManifestApplyResult(document.Display, "apply", _Describe(exception));
        }
    }

    private async Task<ManifestApplyResult> _CreateAsync(GenericClient generic, PlannedResource resource, JsonObject json, CancellationToken cancellationToken)
    {
        var document = resource.Change.Document;
        try
        {
            var body = json.Deserialize<RawKubernetesObject>();
            if (body is null)
            {
                return new ManifestApplyResult(document.Display, "create", "The rendered document could not be read back as a resource.");
            }

            _ = resource.Namespaced
                ? await generic.CreateNamespacedAsync(body, releaseNamespace, cancellationToken)
                : await generic.CreateAsync(body, cancellationToken);
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
        using var generic = _ClientFor(document, resource.Plural);
        try
        {
            if (resource.Namespaced)
            {
                await generic.DeleteNamespacedAsync<RawKubernetesObject>(releaseNamespace, document.Name, cancellationToken);
            }
            else
            {
                await generic.DeleteAsync<RawKubernetesObject>(document.Name, cancellationToken);
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

    private GenericClient _ClientFor(ManifestDocument document, string plural)
    {
        var reference = ApiVersionRef.Parse(document.ApiVersion);
        return new GenericClient(client, reference.Group, reference.Version, plural, disposeClient: false);
    }

    // Status and reason only: the response body of a Kubernetes rejection can name the kubeconfig's user or
    // service account, which is the host detail the rest of the plugin keeps from the agent (security review F3).
    private static string _Describe(HttpOperationException exception) =>
        exception.Response is { } response ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : "the call failed";
}
