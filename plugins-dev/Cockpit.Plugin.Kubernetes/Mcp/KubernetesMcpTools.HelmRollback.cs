using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using k8s;
using k8s.Autorest;
using k8s.Models;
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

    [McpServerTool(Name = "helm_rollback")]
    [Description("""
        Rolls a Helm release back to an earlier revision by applying the manifest that revision stored in its release secret — no helm binary, no chart, no repository. Call helm_history first to pick a revision. The operator must approve, and the approval shows the literal manifest diff (which resources are created, updated with the changed lines, and deleted), never just the release name.

        This is NOT full helm parity, and the difference can matter in production. Helm does a three-way merge over the old manifest, the new manifest and the live state; this applies the stored target manifest as a JSON merge patch per resource. Concretely: (1) a field that the current revision sets and the target revision does not is left as it is on a resource that exists in both — only whole resources that the target revision no longer has are deleted; (2) a field another controller owns (an autoscaler's replicas) is overwritten wherever the target manifest spells it out, and not forced away where it does not; (3) an immutable field the apiserver refuses is reported as a failure for that resource — nothing is force-recreated; (4) helm's pre-rollback and post-rollback hooks are NOT run; (5) there is no transaction — every resource is attempted, and a rollback that partially failed is recorded as a failed revision, listing what did and did not apply. Anything this refuses can still be done with helm itself.

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
        var (plan, planError) = await HelmRollbackPlan.ResolveAsync(client, diff, @namespace, registration.AllowClusterScoped, cancellationToken);
        if (plan is null)
        {
            return McpText.Error(planError!);
        }

        var operation = $"roll back Helm release \"{release}\" in namespace \"{@namespace}\" from revision {currentRevision} to revision {revision} — {diff.ToConsentText(MaxConsentDiffLength)}";
        var decision = await gate.AuthorizeNamespacedMutationAsync(registration, @namespace, operation, session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        return await _CarryOutAsync(client, plan, diff, @namespace, release, ordered, targetRelease, currentRevision, revision, cancellationToken);
    }

    // The order helm uses: record the new revision as pending first, so a rollback interrupted halfway leaves a
    // visible pending-rollback rather than a record that claims the cluster is somewhere it is not.
    private static async Task<string> _CarryOutAsync(
        IKubernetes client, HelmRollbackPlan plan, ManifestDiff diff, string @namespace, string release,
        List<V1Secret> existing, JsonObject targetRelease, int currentRevision, int targetRevision, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var newRevision = existing.Max(HelmReleaseLedger.RevisionOf) + 1;
        var (pending, payload) = HelmReleaseLedger.NewRevision(targetRelease, release, @namespace, newRevision, targetRevision, now);
        var recorded = await client.CoreV1.CreateNamespacedSecretAsync(pending, @namespace, cancellationToken: cancellationToken);

        var results = await plan.ApplyAsync(cancellationToken);
        var failures = results.Where(result => result.Error is not null).ToList();
        var status = failures.Count == 0 ? HelmReleaseLedger.Deployed : HelmReleaseLedger.Failed;

        HelmReleaseLedger.Restamp(recorded, payload, status, now);
        try
        {
            await client.CoreV1.ReplaceNamespacedSecretAsync(recorded, recorded.Metadata.Name, @namespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException)
        {
            // The cluster has already been changed at this point. Saying only "the call failed" would leave the
            // caller unable to tell that from a rollback that never started, so name the state it is actually in.
            return McpText.Error($"The resources were applied, but revision {newRevision} of \"{release}\" could not be settled to \"{status}\" and still reads as \"{HelmReleaseLedger.PendingRollback}\". Check the release with helm_history before changing anything else.");
        }

        if (failures.Count == 0)
        {
            await _SupersedeAsync(client, @namespace, existing, newRevision, now, cancellationToken);
        }

        return McpText.Ok(new
        {
            ok = failures.Count == 0,
            release,
            rolledBackFrom = currentRevision,
            rolledBackTo = targetRevision,
            newRevision,
            status,
            diff = diff.ToJson(),
            resources = results,
            note = failures.Count == 0
                ? "Hooks were not run and no three-way merge was done — see the tool description for what that leaves untouched."
                : "Partially applied: this rollback is recorded as a failed revision. The resources listed with an error were not changed.",
        });
    }

    private static async Task _SupersedeAsync(
        IKubernetes client, string @namespace, List<V1Secret> existing, int newRevision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var secret in existing.Where(candidate => HelmReleaseLedger.StatusOf(candidate) == HelmReleaseLedger.Deployed && HelmReleaseLedger.RevisionOf(candidate) != newRevision))
        {
            if (HelmReleaseSecretCodec.TryDecodeRaw(secret, out _) is not { } payload)
            {
                continue;
            }

            HelmReleaseLedger.Restamp(secret, payload, HelmReleaseLedger.Superseded, now);
            try
            {
                await client.CoreV1.ReplaceNamespacedSecretAsync(secret, secret.Metadata.Name, @namespace, cancellationToken: cancellationToken);
            }
            catch (HttpOperationException)
            {
                // Tidying, not truth: the new revision is already recorded as deployed and helm reads the current
                // one by highest number, so an older one left saying "deployed" is untidy rather than wrong.
            }
        }
    }

    private static string? _Manifest(JsonObject release) =>
        release["manifest"] is JsonValue value && value.TryGetValue<string>(out var manifest) ? manifest : null;
}
