using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-1061 fase 2: roll a Helm release back to an earlier revision without the helm binary, by applying the manifest
// that revision already stored in its release secret. The approval carries the literal manifest diff, and the same
// diff is what gets applied — one computation, so what the operator saw is what ran.
internal sealed partial class KubernetesMcpTools
{
    // The consent card renders one wrapped block, so the diff goes on it whole but bounded; past this it says how
    // many resources it left out rather than pushing the Approve button off the card.
    private const int MaxConsentDiffLength = 3_500;

    [McpServerTool(Name = "helm_rollback", ReadOnly = false, Destructive = true)]
    [Description("""
        Rolls a Helm release back to an earlier revision by applying the manifest that revision stored in its release secret — no helm binary, no chart, no repository. Call helm_history first to pick a revision. The operator must approve, and the approval shows the literal manifest diff (which resources are created, updated with the changed lines, and deleted), never just the release name.

        This is NOT full helm parity, and the difference can matter in production. Helm does a three-way merge over the old manifest, the new manifest and the live state; this applies the stored target manifest as a JSON merge patch per resource. Concretely: (1) each resource is written with a server-side apply under helm's own field manager, so a field helm owned and the target manifest no longer sets is removed, and whole resources the target manifest no longer has are deleted; (2) the apply is never forced — a field another controller has taken over is reported as a conflict for that resource instead of being seized; (3) an immutable field the apiserver refuses is reported as a failure for that resource — nothing is force-recreated; (4) helm's pre-rollback and post-rollback hooks are NOT run; (5) there is no transaction — every resource is attempted, and a rollback that partially failed is recorded as a failed revision, listing what did and did not apply. Anything this refuses can still be done with helm itself.

        Bookkeeping follows helm: a rollback writes a NEW revision carrying the target's manifest and values and supersedes the one that was deployed; it does not resurrect the old revision's secret. There is no helm_install and no helm_uninstall.
        """)]
    public async Task<string> HelmRollback(
        [Description("The cluster label.")] string cluster,
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The namespace the release lives in.")] string @namespace,
        [Description("The Helm release name.")] string release,
        [Description("The revision to roll back to, as listed by helm_history. Required — there is no implicit \"previous\".")] int revision,
        CancellationToken cancellationToken = default)
    {
        var (registration, clusterError) = _ResolveCluster(cluster);
        if (registration is null)
        {
            return clusterError!;
        }

        if (revision < 1)
        {
            return McpText.Error("revision must be the number of an existing revision — call helm_history to see them.");
        }

        // Reading the two release secrets is the same credential-material read the other helm tools do; the change
        // itself asks again, with the diff, once there is something to show.
        var decision = await gate.AuthorizeSensitiveNamespacedReadAsync(registration, @namespace, $"read Helm release \"{release}\" revisions {revision} and current in namespace \"{@namespace}\"", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _WithClient(registration, (client, token) => _RollbackAsync(client, registration, session, @namespace, release, revision, token), cancellationToken);
    }

    private async Task<string> _RollbackAsync(
        IKubernetes client, ClusterRegistration registration, string session, string @namespace, string release, int revision, CancellationToken cancellationToken)
    {
        var secrets = await client.CoreV1.ListNamespacedSecretAsync(@namespace, labelSelector: $"owner=helm,name={release}", fieldSelector: $"type={HelmReleaseLedger.SecretType}", cancellationToken: cancellationToken);
        if (secrets.Items.Count == 0)
        {
            return McpText.Error($"No Helm release \"{release}\" found in namespace \"{@namespace}\".");
        }

        var ordered = secrets.Items.OrderByDescending(HelmReleaseLedger.RevisionOf).ToList();
        var current = ordered[0];
        var currentRevision = HelmReleaseLedger.RevisionOf(current);
        if (currentRevision == revision)
        {
            return McpText.Error($"Helm release \"{release}\" is already at revision {revision}.");
        }

        var target = ordered.FirstOrDefault(secret => HelmReleaseLedger.RevisionOf(secret) == revision);
        if (target is null)
        {
            var available = string.Join(", ", ordered.Select(HelmReleaseLedger.RevisionOf).Where(candidate => candidate > 0));
            return McpText.Error($"Helm release \"{release}\" has no revision {revision} in namespace \"{@namespace}\". Kept revisions: {available}.");
        }

        var currentRelease = HelmReleaseSecretCodec.TryDecodeRaw(current, out var currentError);
        var targetRelease = HelmReleaseSecretCodec.TryDecodeRaw(target, out var targetError);
        if (currentRelease is null || targetRelease is null)
        {
            return McpText.Error(currentError ?? targetError!);
        }

        var targetManifest = _Manifest(targetRelease);
        if (string.IsNullOrWhiteSpace(targetManifest))
        {
            return McpText.Error($"Revision {revision} of \"{release}\" stores no rendered manifest, so there is nothing to roll back to.");
        }

        var diff = ManifestDiff.Compute(_Manifest(currentRelease), targetManifest);
        var (plan, planError) = await HelmApplyPlan.ResolveAsync(client, diff, @namespace, registration.AllowClusterScoped, cancellationToken);
        if (plan is null)
        {
            return McpText.Error(planError!);
        }

        // AC-1062: the diff goes to the gate as separate lines, not pre-joined with `\n`, so the gate can escape
        // and join them itself instead of collapsing the whole block to one unreadable line.
        var operation = $"roll back Helm release \"{release}\" in namespace \"{@namespace}\" from revision {currentRevision} to revision {revision}";
        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, operation, session, diff.ToConsentLines(MaxConsentDiffLength));
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        var outcome = await HelmRevisionWriter.CarryOutAsync(
            client, plan, @namespace, release, ordered, targetRelease, $"Rollback to {revision}", HelmReleaseLedger.PendingRollback, cancellationToken);
        if (outcome.Error is { } settleError)
        {
            return McpText.Error(settleError);
        }

        return McpText.Ok(new
        {
            ok = outcome.Succeeded,
            release,
            rolledBackFrom = currentRevision,
            rolledBackTo = revision,
            newRevision = outcome.NewRevision,
            status = outcome.Status,
            diff = diff.ToJson(),
            resources = outcome.Results,
            note = outcome.Succeeded
                ? "Hooks were not run and no three-way merge was done — see the tool description for what that leaves untouched."
                : "Partially applied: this rollback is recorded as a failed revision. The resources listed with an error were not changed.",
        });
    }

    private static string? _Manifest(JsonObject release) =>
        release["manifest"] is JsonValue value && value.TryGetValue<string>(out var manifest) ? manifest : null;
}
