using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using k8s.Models;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Mcp;

/// <summary>
/// The MCP tools an agent uses to work with a registered cluster (AC-80), exposed as <c>mcp__k8s__*</c>. Every tool
/// that touches a cluster routes through <see cref="ClusterAccessGate"/> first — opening the cluster, the namespace
/// jail, and (for a change) an always-fresh consent — and only then reaches the kube-apiserver through the
/// plugin-held client. The agent never sees a kubeconfig; it names a cluster by its label and a resource by its
/// apiVersion/plural, and passes its own <c>COCKPIT_PANE_ID</c> as <c>session</c> so a remembered approval is scoped
/// to the session that asked. Whether a resource is namespaced or cluster-scoped is decided by its real REST scope
/// (<see cref="ResourceScope"/>), never by whether the agent left the namespace blank.
/// </summary>
internal sealed class KubernetesMcpTools(KubernetesSettings settings, ClusterAccessGate gate, ClusterConnectionFactory connections, PortForwardManager portForwards)
{
    private static readonly TimeSpan PortForwardMaxLifetime = TimeSpan.FromMinutes(30);

    [McpServerTool(Name = "list_clusters")]
    [Description("Lists the Kubernetes clusters the operator registered, with each cluster's label, its allowed namespaces, and which extra capabilities (cluster-scoped resources, exec) are turned on for it. Reading or changing anything else goes through the other tools and asks the operator for consent. Start here to see what you can reach.")]
    public string ListClusters() =>
        McpText.Ok(new
        {
            ok = true,
            clusters = settings.Clusters.Select(cluster => new
            {
                label = cluster.Label,
                allowedNamespaces = cluster.AllowedNamespaces,
                clusterScoped = cluster.AllowClusterScoped,
                exec = cluster.AllowExec,
                usesExecAuth = cluster.UsesExecAuth,
            }),
        });

    [McpServerTool(Name = "list_resources")]
    [Description("Lists resources of one kind. apiVersion is like \"v1\" (core) or \"apps/v1\"; plural is the resource plural, e.g. \"pods\", \"deployments\", \"services\", \"configmaps\". For a namespaced kind a namespace is required and one outside the cluster's allowed list asks the operator first; a genuinely cluster-scoped kind (nodes, namespaces, persistentvolumes) needs the cluster to allow cluster-scoped access. Returns each item's name, namespace and creation time.")]
    public async Task<string> ListResources(
        [Description("The cluster label, as returned by list_clusters.")] string cluster,
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The resource apiVersion, e.g. \"v1\" or \"apps/v1\".")] string apiVersion,
        [Description("The resource plural, e.g. \"pods\", \"deployments\".")] string plural,
        [Description("The namespace to list in; required for a namespaced kind, left blank only for a cluster-scoped kind.")] string? @namespace = null,
        [Description("Optional label selector, e.g. \"app=web\".")] string? labelSelector = null,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (_ValidateResource(apiVersion, plural) is { } invalid)
        {
            return invalid;
        }

        var reference = ApiVersionRef.Parse(apiVersion);
        var (decision, clusterScoped) = await _AuthorizeReadAsync(registration, reference, plural, @namespace, $"list {plural} ({apiVersion})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, reference.Group, reference.Version, plural, disposeClient: false);
            var list = clusterScoped
                ? await generic.ListAsync<RawKubernetesList>(labelSelector: labelSelector, limit: 200, cancel: token)
                : await generic.ListNamespacedAsync<RawKubernetesList>(_RequireNamespace(@namespace), labelSelector: labelSelector, limit: 200, cancel: token);
            return McpText.Node(ResourceListSummary.Summarize(list));
        }, cancellationToken);
    }

    [McpServerTool(Name = "get_resource")]
    [Description("Reads one resource in full. apiVersion like \"v1\" or \"apps/v1\", plural like \"pods\"/\"deployments\". A namespaced kind needs its namespace (outside the allowed list asks first; a secret always asks); a cluster-scoped kind needs cluster-scoped access on. Returns the resource as JSON.")]
    public async Task<string> GetResource(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The resource apiVersion, e.g. \"v1\" or \"apps/v1\".")] string apiVersion,
        [Description("The resource plural, e.g. \"pods\".")] string plural,
        [Description("The resource name.")] string name,
        [Description("The namespace; required for a namespaced kind.")] string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (_ValidateResource(apiVersion, plural) is { } invalid)
        {
            return invalid;
        }

        var reference = ApiVersionRef.Parse(apiVersion);
        var (decision, clusterScoped) = await _AuthorizeReadAsync(registration, reference, plural, @namespace, $"get {plural}/{name} ({apiVersion})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, reference.Group, reference.Version, plural, disposeClient: false);
            var resource = clusterScoped
                ? await generic.ReadAsync<RawKubernetesObject>(name, cancel: token)
                : await generic.ReadNamespacedAsync<RawKubernetesObject>(_RequireNamespace(@namespace), name, cancel: token);
            return McpText.Node(JsonSerializer.SerializeToNode(resource));
        }, cancellationToken);
    }

    [McpServerTool(Name = "pod_logs")]
    [Description("Reads the logs of a pod. Returns the last tailLines lines (default 200). A namespace outside the cluster's allowed list asks the operator first.")]
    public async Task<string> PodLogs(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace of the pod.")] string @namespace,
        [Description("The pod name.")] string pod,
        [Description("The container name, if the pod has more than one.")] string? container = null,
        [Description("How many lines from the end to return (default 200).")] int tailLines = 200,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (tailLines < 1)
        {
            return McpText.Error("tailLines must be at least 1.");
        }

        var decision = await gate.AuthorizeNamespacedReadAsync(registration, @namespace, $"read logs of pod \"{pod}\" in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(pod, @namespace, container: container, tailLines: tailLines, cancellationToken: token);
            using var reader = new StreamReader(stream);
            var logs = await reader.ReadToEndAsync(token);
            return McpText.Ok(new { ok = true, logs });
        }, cancellationToken);
    }

    [McpServerTool(Name = "delete_resource")]
    [Description("Deletes one resource. This is a change, so it always asks the operator to approve — showing the literal resource — and is never remembered. apiVersion like \"v1\"/\"apps/v1\", plural like \"pods\"/\"deployments\".")]
    public async Task<string> DeleteResource(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The resource apiVersion.")] string apiVersion,
        [Description("The resource plural.")] string plural,
        [Description("The resource name.")] string name,
        [Description("The namespace; required for a namespaced kind.")] string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (_ValidateResource(apiVersion, plural) is { } invalid)
        {
            return invalid;
        }

        var reference = ApiVersionRef.Parse(apiVersion);
        var (decision, clusterScoped) = await _AuthorizeMutationAsync(registration, reference, plural, @namespace, $"delete {plural}/{name} ({apiVersion})", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            using var generic = new GenericClient(client, reference.Group, reference.Version, plural, disposeClient: false);
            if (clusterScoped)
            {
                await generic.DeleteAsync<RawKubernetesObject>(name, cancel: token);
            }
            else
            {
                await generic.DeleteNamespacedAsync<RawKubernetesObject>(_RequireNamespace(@namespace), name, cancel: token);
            }

            return McpText.Ok(new { ok = true, deleted = name });
        }, cancellationToken);
    }

    [McpServerTool(Name = "scale_resource")]
    [Description("Scales a deployment or statefulset to a replica count. A change, so it always asks the operator to approve and is never remembered. kind is \"deployments\" or \"statefulsets\".")]
    public async Task<string> ScaleResource(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace.")] string @namespace,
        [Description("\"deployments\" or \"statefulsets\".")] string kind,
        [Description("The workload name.")] string name,
        [Description("The desired replica count.")] int replicas,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (replicas < 0)
        {
            return McpText.Error("replicas cannot be negative.");
        }

        if (kind is not ("deployments" or "statefulsets"))
        {
            return McpText.Error("kind must be \"deployments\" or \"statefulsets\".");
        }

        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, $"scale {kind}/{name} to {replicas} replica(s) in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            if (kind == "deployments")
            {
                var scale = await client.AppsV1.ReadNamespacedDeploymentScaleAsync(name, @namespace, cancellationToken: token);
                scale.Spec.Replicas = replicas;
                await client.AppsV1.ReplaceNamespacedDeploymentScaleAsync(scale, name, @namespace, cancellationToken: token);
            }
            else
            {
                var scale = await client.AppsV1.ReadNamespacedStatefulSetScaleAsync(name, @namespace, cancellationToken: token);
                scale.Spec.Replicas = replicas;
                await client.AppsV1.ReplaceNamespacedStatefulSetScaleAsync(scale, name, @namespace, cancellationToken: token);
            }

            return McpText.Ok(new { ok = true, scaled = name, replicas });
        }, cancellationToken);
    }

    [McpServerTool(Name = "patch_resource")]
    [Description("Applies a JSON merge-patch to an existing resource — the way to change a field or two (an image, an env var, an annotation). patchJson is a JSON object with just the fields to change. A change, so it always asks the operator to approve and is never remembered. (To create a resource from scratch, do it from the terminal — this v1 patches existing resources only.)")]
    public async Task<string> PatchResource(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The resource apiVersion.")] string apiVersion,
        [Description("The resource plural.")] string plural,
        [Description("The resource name.")] string name,
        [Description("The namespace.")] string @namespace,
        [Description("A JSON merge-patch: an object with only the fields to change, e.g. {\"spec\":{\"replicas\":3}}.")] string patchJson,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (_ValidateResource(apiVersion, plural) is { } invalid)
        {
            return invalid;
        }

        if (!_IsJsonObject(patchJson))
        {
            return McpText.Error("patchJson must be a JSON object with the fields to change.");
        }

        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, $"patch {plural}/{name} ({apiVersion}) in namespace \"{@namespace}\" with {patchJson}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            var reference = ApiVersionRef.Parse(apiVersion);
            using var generic = new GenericClient(client, reference.Group, reference.Version, plural, disposeClient: false);
            var patched = await generic.PatchNamespacedAsync<RawKubernetesObject>(new V1Patch(patchJson, V1Patch.PatchType.MergePatch), @namespace, name, cancel: token);
            return McpText.Node(JsonSerializer.SerializeToNode(patched));
        }, cancellationToken);
    }

    [McpServerTool(Name = "exec")]
    [Description("Runs a single, non-interactive command in a pod and returns its stdout, stderr and exit code. exec is off unless the operator turned it on for this cluster, and reaches past the namespace boundary, so it always asks afresh with the literal command shown, and is never remembered. The command runs as \"/bin/sh -c <command>\".")]
    public async Task<string> Exec(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace of the pod.")] string @namespace,
        [Description("The pod name.")] string pod,
        [Description("The shell command to run, e.g. \"ls -la /app\".")] string command,
        [Description("The container name, if the pod has more than one.")] string? container = null,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        var decision = await gate.AuthorizeDangerAsync(registration, DangerCapability.Exec, @namespace, $"exec in pod \"{pod}\" (namespace \"{@namespace}\"): /bin/sh -c {command}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var callback = new ExecAsyncCallback(async (_, outStream, errStream) =>
            {
                // Drain stdout and stderr concurrently — reading one fully then the other can deadlock on the pipe
                // buffer when both carry output.
                var outText = new StreamReader(outStream).ReadToEndAsync(token);
                var errText = new StreamReader(errStream).ReadToEndAsync(token);
                await Task.WhenAll(outText, errText);
                stdout.Append(await outText);
                stderr.Append(await errText);
            });

            var exitCode = await client.NamespacedPodExecAsync(
                pod, @namespace, container, ["/bin/sh", "-c", command], tty: false, callback, token);

            return McpText.Ok(new { ok = true, exitCode, stdout = stdout.ToString(), stderr = stderr.ToString() });
        }, cancellationToken);
    }

    [McpServerTool(Name = "port_forward")]
    [Description("Opens a port-forward tunnel from a local loopback port to a pod port. port-forward is off unless the operator turned it on for this cluster, and it reaches past the namespace boundary, so it always asks afresh with the literal target shown, and is never remembered. The tunnel appears in the status bar with a Kill button and auto-closes after 30 minutes. Returns the bound local address and a tunnel id.")]
    public async Task<string> PortForward(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace of the pod.")] string @namespace,
        [Description("The pod name.")] string pod,
        [Description("The pod port to forward to.")] int remotePort,
        [Description("The local loopback port to bind, or 0 to let the OS pick a free one.")] int localPort = 0,
        CancellationToken cancellationToken = default)
    {
        if (_FindCluster(cluster) is not { } registration)
        {
            return _UnknownCluster(cluster);
        }

        if (remotePort is < 1 or > 65535)
        {
            return McpText.Error("remotePort must be between 1 and 65535.");
        }

        if (localPort is < 0 or > 65535)
        {
            return McpText.Error("localPort must be between 0 and 65535 (0 picks a free port).");
        }

        var target = localPort == 0 ? "an OS-assigned local port" : $"127.0.0.1:{localPort}";
        var decision = await gate.AuthorizeDangerAsync(registration, DangerCapability.PortForward, @namespace, $"port-forward pod \"{pod}\" (namespace \"{@namespace}\") port {remotePort} to {target}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, (client, _) =>
        {
            var tunnel = portForwards.Start(client, registration.Label, @namespace, pod, remotePort, localPort, PortForwardMaxLifetime);
            return Task.FromResult(McpText.Ok(new
            {
                ok = true,
                tunnelId = tunnel.Id,
                localAddress = $"127.0.0.1:{tunnel.LocalPort}",
                remotePort,
                pod,
                note = "Listed in the status bar with a Kill button; auto-closes after 30 minutes.",
            }));
        }, cancellationToken);
    }

    // Reads share one authorization path: a cluster-scoped kind takes the opt-in cluster-scoped gate; a namespaced
    // kind requires a namespace (blank is refused, never silently listed cluster-wide — security review F1) and goes
    // through the jail, with secrets asking afresh even inside an allowed namespace (F2).
    private async Task<(GateResult Decision, bool ClusterScoped)> _AuthorizeReadAsync(
        ClusterRegistration cluster, ApiVersionRef reference, string plural, string? @namespace, string describe, string? session)
    {
        if (ResourceScope.IsClusterScoped(reference.Group, plural))
        {
            return (await gate.AuthorizeClusterScopedReadAsync(cluster, $"{describe} cluster-wide", session), true);
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return (GateResult.Deny($"\"{plural}\" is a namespaced resource — a namespace is required."), false);
        }

        var operation = $"{describe} in namespace \"{@namespace}\"";
        var decision = ResourceScope.IsSensitive(reference.Group, plural)
            ? await gate.AuthorizeSensitiveNamespacedReadAsync(cluster, @namespace, operation, session)
            : await gate.AuthorizeNamespacedReadAsync(cluster, @namespace, operation, session);
        return (decision, false);
    }

    private async Task<(GateResult Decision, bool ClusterScoped)> _AuthorizeMutationAsync(
        ClusterRegistration cluster, ApiVersionRef reference, string plural, string? @namespace, string describe, string? session)
    {
        if (ResourceScope.IsClusterScoped(reference.Group, plural))
        {
            return (await gate.AuthorizeClusterScopedMutationAsync(cluster, $"{describe} cluster-wide", session), true);
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            return (GateResult.Deny($"\"{plural}\" is a namespaced resource — a namespace is required."), false);
        }

        return (await gate.AuthorizeNamespacedMutationAsync(cluster, @namespace, $"{describe} in namespace \"{@namespace}\"", session), false);
    }

    private ClusterRegistration? _FindCluster(string label) =>
        settings.Clusters.FirstOrDefault(candidate => string.Equals(candidate.Label, label, StringComparison.Ordinal));

    private static string _UnknownCluster(string label) =>
        McpText.Error($"No registered cluster labelled \"{label}\". Call list_clusters to see the ones that are configured.");

    private static string? _ValidateResource(string apiVersion, string plural) =>
        string.IsNullOrWhiteSpace(apiVersion) || string.IsNullOrWhiteSpace(plural)
            ? McpText.Error("apiVersion and plural are required.")
            : null;

    // The namespaced client calls only run after _AuthorizeReadAsync/_AuthorizeMutationAsync refused a blank
    // namespace, so this restates that invariant for the compiler instead of a null-forgiving operator.
    private static string _RequireNamespace(string? @namespace) =>
        @namespace ?? throw new InvalidOperationException("A namespaced call reached the client without a namespace.");

    private async Task<string> _WithClient(ClusterRegistration cluster, Func<IKubernetes, CancellationToken, Task<string>> call, CancellationToken cancellationToken)
    {
        var (client, error) = connections.Connect(cluster);
        if (client is null)
        {
            return McpText.Error(error ?? $"Could not connect to cluster \"{cluster.Label}\".");
        }

        try
        {
            return await call(client, cancellationToken);
        }
        catch (k8s.Autorest.HttpOperationException exception)
        {
            // Hand the agent the status only — the raw response body can name the kubeconfig's user/service-account
            // in an RBAC denial (security review F3).
            return McpText.Error($"Kubernetes API error: {(int)exception.Response.StatusCode} {exception.Response.ReasonPhrase}.");
        }
        catch (OperationCanceledException)
        {
            return McpText.Error("The call was cancelled.");
        }
        catch (Exception exception)
        {
            return McpText.Error($"The call to cluster \"{cluster.Label}\" failed: {exception.Message}");
        }
    }

    private static bool _IsJsonObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
