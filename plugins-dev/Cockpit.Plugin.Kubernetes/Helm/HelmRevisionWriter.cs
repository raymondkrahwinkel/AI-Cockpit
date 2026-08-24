using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Helm;

// What writing one revision did: the number it got, the status it settled on, per-resource results, and — only when
// the cluster was changed but the bookkeeping could not be settled — the message the caller must hand back as is.
internal sealed record HelmRevisionOutcome(int NewRevision, string Status, IReadOnlyList<ManifestApplyResult> Results, string? Error)
{
    public bool Succeeded => Error is null && Results.All(result => result.Error is null);
}

// Carries out a rollback or an upgrade against the cluster and records it the way helm does (AC-1061). One
// implementation for both: this is the part where getting the order wrong leaves a release helm can no longer read.
internal static class HelmRevisionWriter
{
    // The order helm uses: record the new revision as pending first, so a change interrupted halfway leaves a
    // visible pending revision rather than a record claiming the cluster is somewhere it is not.
    public static async Task<HelmRevisionOutcome> CarryOutAsync(
        IKubernetes client, HelmApplyPlan plan, string @namespace, string release, IReadOnlyList<V1Secret> existing,
        JsonObject targetRelease, string description, string pendingStatus, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var newRevision = existing.Max(HelmReleaseLedger.RevisionOf) + 1;
        var (pending, payload) = HelmReleaseLedger.NewRevision(targetRelease, release, @namespace, newRevision, description, pendingStatus, now);
        var recorded = await client.CoreV1.CreateNamespacedSecretAsync(pending, @namespace, cancellationToken: cancellationToken);

        var results = await plan.ApplyAsync(cancellationToken);
        var failed = results.Any(result => result.Error is not null);
        var status = failed ? HelmReleaseLedger.Failed : HelmReleaseLedger.Deployed;

        HelmReleaseLedger.Restamp(recorded, payload, status, now);
        try
        {
            await client.CoreV1.ReplaceNamespacedSecretAsync(recorded, recorded.Metadata.Name, @namespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException)
        {
            // The cluster has already been changed at this point. Saying only "the call failed" would leave the
            // caller unable to tell that from a change that never started, so name the state it is actually in.
            return new HelmRevisionOutcome(newRevision, pendingStatus, results,
                $"The resources were applied, but revision {newRevision} of \"{release}\" could not be settled to \"{status}\" and still reads as \"{pendingStatus}\". Check the release with helm_history before changing anything else.");
        }

        if (!failed)
        {
            await _SupersedeAsync(client, @namespace, existing, newRevision, now, cancellationToken);
        }

        return new HelmRevisionOutcome(newRevision, status, results, null);
    }

    private static async Task _SupersedeAsync(
        IKubernetes client, string @namespace, IReadOnlyList<V1Secret> existing, int newRevision, DateTimeOffset now, CancellationToken cancellationToken)
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
}
