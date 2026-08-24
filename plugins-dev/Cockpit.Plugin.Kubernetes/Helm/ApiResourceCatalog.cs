using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Helm;

// What the apiserver says a kind really is: its plural (the REST path the generic client needs) and whether it is
// namespaced. A manifest names kinds, not plurals, and guessing the plural from the kind is wrong often enough to
// matter (Endpoints, NetworkPolicy, any CRD), so this asks discovery and caches the answer for the call.
internal sealed class ApiResourceCatalog(IKubernetes client)
{
    private readonly Dictionary<string, Dictionary<string, (string Plural, bool Namespaced)>> _byApiVersion = new(StringComparer.Ordinal);

    public async Task<(string Plural, bool Namespaced)?> ResolveAsync(string apiVersion, string kind, CancellationToken cancellationToken)
    {
        if (!_byApiVersion.TryGetValue(apiVersion, out var kinds))
        {
            kinds = await _LoadAsync(apiVersion, cancellationToken);
            _byApiVersion[apiVersion] = kinds;
        }

        return kinds.TryGetValue(kind, out var resource) ? resource : null;
    }

    private async Task<Dictionary<string, (string Plural, bool Namespaced)>> _LoadAsync(string apiVersion, CancellationToken cancellationToken)
    {
        var reference = ApiVersionRef.Parse(apiVersion);
        var kinds = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        V1APIResourceList list;
        try
        {
            list = string.IsNullOrEmpty(reference.Group)
                ? await client.CoreV1.GetAPIResourcesAsync(cancellationToken)
                : await client.CustomObjects.GetAPIResourcesAsync(reference.Group, reference.Version, cancellationToken);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // An apiVersion this cluster does not serve at all — an empty catalogue, so the caller reports the kind
            // it could not resolve rather than the generic "the call to the cluster failed".
            return kinds;
        }

        foreach (var resource in list.Resources)
        {
            // A subresource ("deployments/status") shares its parent's kind and would otherwise win the entry.
            if (!resource.Name.Contains('/'))
            {
                kinds[resource.Kind] = (resource.Name, resource.Namespaced);
            }
        }

        return kinds;
    }
}
