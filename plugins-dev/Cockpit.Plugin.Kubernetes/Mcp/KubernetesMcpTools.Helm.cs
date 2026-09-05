using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using k8s.Models;
using Cockpit.Plugin.Kubernetes.Helm;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-1061 fase 1: read a cluster's Helm releases straight from their `helm.sh/release.v1` secrets, no helm binary.
// Shares `KubernetesMcpTools`' cluster resolution and client plumbing; the payload is credential material, so every
// call is gated exactly like `get_resource` on a secret (`ClusterAccessGate.AuthorizeSensitiveNamespacedReadAsync`).
internal sealed partial class KubernetesMcpTools
{
    [McpServerTool(Name = "helm_list", ReadOnly = true)]
    [Description("Lists the current revision of every Helm release in a namespace, read straight from its `helm.sh/release.v1` secrets. Each entry: release name, revision, status, chart (name-version), appVersion and when it was last deployed. Reading a release secret is credential material, so it always asks the operator to approve, even inside an allowed namespace.")]
    public async Task<string> HelmList(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace to list Helm releases in.")] string @namespace,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeSensitiveNamespacedReadAsync(registration, @namespace, $"list Helm releases in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: "owner=helm", fieldSelector: $"type={HelmReleaseLedger.SecretType}", cancellationToken: token);
            var latestPerRelease = secrets.Items
                .Where(secret => secret.Metadata?.Labels?.ContainsKey("name") == true)
                .GroupBy(secret => secret.Metadata!.Labels!["name"])
                .Select(group => group.OrderByDescending(HelmReleaseLedger.RevisionOf).First());

            var releases = new JsonArray();
            foreach (var secret in latestPerRelease)
            {
                var decoded = HelmReleaseSecretCodec.TryDecode(secret, out var error);
                releases.Add(decoded is null
                    ? new JsonObject { ["name"] = secret.Metadata!.Labels!["name"], ["error"] = error }
                    : decoded.ToListEntry());
            }

            return McpText.Ok(new { ok = true, releases });
        }, cancellationToken);
    }

    [McpServerTool(Name = "helm_status", ReadOnly = true)]
    [Description("Reads one Helm release's status: chart, appVersion, when it was first/last deployed, and its notes. Defaults to the current revision; pass revision to inspect an older one. Always asks the operator to approve, even inside an allowed namespace.")]
    public Task<string> HelmStatus(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name.")] string release,
        [Description("The revision to read, or 0 for the current one.")] int revision = 0,
        CancellationToken cancellationToken = default) =>
        _WithHelmRelease(cluster, session, @namespace, release, revision,
            $"read Helm release \"{release}\" status in namespace \"{@namespace}\"",
            found => McpText.Ok(new { ok = true, release = found.ToStatus() }),
            cancellationToken);

    [McpServerTool(Name = "helm_history", ReadOnly = true)]
    [Description("Lists every revision Helm has kept for a release — revision number, status (deployed/superseded/failed/...), chart version and appVersion — newest first. Always asks the operator to approve, even inside an allowed namespace.")]
    public async Task<string> HelmHistory(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name.")] string release,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeSensitiveNamespacedReadAsync(registration, @namespace, $"read Helm release \"{release}\" history in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: $"owner=helm,name={release}", fieldSelector: $"type={HelmReleaseLedger.SecretType}", cancellationToken: token);
            if (secrets.Items.Count == 0)
            {
                return McpText.Error($"No Helm release \"{release}\" found in namespace \"{@namespace}\".");
            }

            var revisions = new JsonArray();
            foreach (var secret in secrets.Items.OrderByDescending(HelmReleaseLedger.RevisionOf))
            {
                var decoded = HelmReleaseSecretCodec.TryDecode(secret, out var error);
                revisions.Add(decoded is null
                    ? new JsonObject { ["revision"] = HelmReleaseLedger.RevisionOf(secret), ["error"] = error }
                    : decoded.ToHistoryEntry());
            }

            return McpText.Ok(new { ok = true, release, revisions });
        }, cancellationToken);
    }

    [McpServerTool(Name = "helm_values", ReadOnly = true)]
    [Description("Reads the values a Helm release was installed or upgraded with. Defaults to the current revision; pass revision to inspect an older one. Set includeChartDefaults to also return the chart's own default values. Always asks the operator to approve, even inside an allowed namespace.")]
    public Task<string> HelmValues(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name.")] string release,
        [Description("The revision to read, or 0 for the current one.")] int revision = 0,
        [Description("Also return the chart's own default values alongside the operator-supplied ones.")] bool includeChartDefaults = false,
        CancellationToken cancellationToken = default) =>
        _WithHelmRelease(cluster, session, @namespace, release, revision,
            $"read Helm release \"{release}\" values in namespace \"{@namespace}\"",
            found => McpText.Ok(new { ok = true, release = found.ToValues(includeChartDefaults) }),
            cancellationToken);

    [McpServerTool(Name = "helm_manifest", ReadOnly = true)]
    [Description("Reads the full rendered manifest Helm applied for one revision of a release — every resource it created, as the literal YAML Helm generated. Defaults to the current revision; pass revision to inspect an older one. Always asks the operator to approve, even inside an allowed namespace.")]
    public Task<string> HelmManifest(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name.")] string release,
        [Description("The revision to read, or 0 for the current one.")] int revision = 0,
        CancellationToken cancellationToken = default) =>
        _WithHelmRelease(cluster, session, @namespace, release, revision,
            $"read Helm release \"{release}\" manifest in namespace \"{@namespace}\"",
            found => McpText.Ok(new { ok = true, release = found.ToManifest() }),
            cancellationToken);

    // Shared by the three single-revision tools (status/values/manifest): resolve the cluster, gate the read as
    // Dangerous credential material, fetch and decode the one release secret, then let the caller shape its own
    // slice of the result.
    private async Task<string> _WithHelmRelease(
        string cluster, string session, string @namespace, string release, int revision, string operation,
        Func<HelmRelease, string> project, CancellationToken cancellationToken)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        var decision = await gate.AuthorizeSensitiveNamespacedReadAsync(registration, @namespace, operation, session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, async (client, token) =>
        {
            var (secret, resolveError) = await _ResolveReleaseSecretAsync(client, @namespace, release, revision, token);
            if (secret is null)
            {
                return McpText.Error(resolveError!);
            }

            var decoded = HelmReleaseSecretCodec.TryDecode(secret, out var decodeError);
            return decoded is null ? McpText.Error(decodeError!) : project(decoded);
        }, cancellationToken);
    }

    // revision > 0 names its secret directly (`sh.helm.release.v1.<release>.v<revision>`, Helm's own naming);
    // otherwise the current revision is whichever secret carries the highest "version" label.
    private static async Task<(V1Secret? Secret, string? Error)> _ResolveReleaseSecretAsync(
        IKubernetes client, string @namespace, string release, int revision, CancellationToken cancellationToken)
    {
        if (revision > 0)
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(HelmReleaseLedger.SecretName(release, revision), @namespace, cancellationToken: cancellationToken);
            return (secret, null);
        }

        var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: $"owner=helm,name={release}", fieldSelector: $"type={HelmReleaseLedger.SecretType}", cancellationToken: cancellationToken);
        var latest = secrets.Items.OrderByDescending(HelmReleaseLedger.RevisionOf).FirstOrDefault();
        return latest is null
            ? (null, $"No Helm release \"{release}\" found in namespace \"{@namespace}\".")
            : (latest, null);
    }
}
