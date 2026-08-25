using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;

namespace Cockpit.Plugin.Kubernetes.Argo;

// AC-576 phase 3: reaches Argo CD's own REST API through the kube-apiserver's service proxy — no ingress, no
// tunnel, just the kubeconfig connection the plugin already has plus a bearer token (measured against a real
// cluster: `GET /api/v1/namespaces/<ns>/services/argocd-server:80/proxy/<path>`). Never leaks on failure.
internal static class ArgoApiClient
{
    private const string ServiceName = "argocd-server:80";

    public static async Task<(JsonNode? Body, string? Error)> GetAsync(
        IKubernetes client, string @namespace, string token, string path, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>> { ["Authorization"] = [$"Bearer {token}"] };

        try
        {
            using var response = await client.CoreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                name: ServiceName, namespaceParameter: @namespace, path: path, customHeaders: headers, cancellationToken: cancellationToken);

            if (!response.Response.IsSuccessStatusCode)
            {
                return (null, $"Argo CD API error: {(int)response.Response.StatusCode} {response.Response.ReasonPhrase}.");
            }

            var body = await JsonNode.ParseAsync(response.Body, cancellationToken: cancellationToken);
            return (body, null);
        }
        catch (HttpOperationException exception)
        {
            // The kube-apiserver itself refused the proxy call — typically no `get` on services/proxy in this
            // namespace. Status and reason only, same as every other Kubernetes error this plugin surfaces.
            return exception.Response is { } response
                ? (null, $"Could not reach the Argo CD API: {(int)response.StatusCode} {response.ReasonPhrase}.")
                : (null, "Could not reach the Argo CD API.");
        }
        catch (OperationCanceledException)
        {
            return (null, "The call to the Argo CD API was cancelled.");
        }
        catch (Exception)
        {
            // Generic on purpose: a DNS/TLS/transport failure's message can carry the apiserver URL or other host
            // detail (mirrors `_WithClient` in KubernetesMcpTools) — and never the token used to build the request.
            return (null, $"Could not reach the Argo CD API through the cluster's service proxy in namespace \"{@namespace}\". Check that \"argocd-server\" exists there and that this cluster's credentials can read services/proxy.");
        }
    }
}
