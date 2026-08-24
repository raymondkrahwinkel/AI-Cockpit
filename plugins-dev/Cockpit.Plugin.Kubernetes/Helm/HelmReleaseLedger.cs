using System.Globalization;
using System.Text.Json.Nodes;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Helm;

// The release-secret bookkeeping a rollback owes helm (AC-1061 fase 2). Helm never resurrects an old revision: it
// writes a NEW one carrying the target's manifest and values, and supersedes the one that was deployed. Getting this
// wrong is worse than not rolling back at all — the next `helm upgrade` diffs against whatever is recorded here.
internal static class HelmReleaseLedger
{
    public const string SecretType = "helm.sh/release.v1";
    public const string PendingRollback = "pending-rollback";
    public const string Deployed = "deployed";
    public const string Superseded = "superseded";
    public const string Failed = "failed";

    public static int RevisionOf(V1Secret secret) =>
        secret.Metadata?.Labels?.TryGetValue("version", out var version) == true && int.TryParse(version, out var parsed) ? parsed : 0;

    public static string? StatusOf(V1Secret secret) =>
        secret.Metadata?.Labels?.TryGetValue("status", out var status) == true ? status : null;

    // The secret for the revision a rollback creates: the target revision's release JSON, renumbered, restamped and
    // relabelled. `info.first_deployed` deliberately stays as it was — it belongs to the release, not the revision.
    public static (V1Secret Secret, JsonObject Payload) NewRevision(JsonObject targetRelease, string release, string @namespace, int revision, int fromRevision, DateTimeOffset now)
    {
        var payload = (JsonObject)targetRelease.DeepClone();
        payload["version"] = revision;
        var info = payload["info"] as JsonObject;
        if (info is null)
        {
            info = [];
            payload["info"] = info;
        }

        info["last_deployed"] = _Timestamp(now);
        info["description"] = $"Rollback to {fromRevision}";
        SetStatus(payload, PendingRollback);

        var secret = new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Type = SecretType,
            Metadata = new V1ObjectMeta
            {
                Name = SecretName(release, revision),
                NamespaceProperty = @namespace,
                Labels = _Labels(release, revision, PendingRollback, now),
            },
            Data = new Dictionary<string, byte[]> { ["release"] = HelmReleaseSecretCodec.Encode(payload) },
        };

        return (secret, payload);
    }

    public static string SecretName(string release, int revision) => $"sh.helm.release.v1.{release}.v{revision}";

    // Status lives twice — in the payload helm reads and in the label helm selects on — so both move together or
    // `helm list` and a label selector disagree about the same revision.
    public static void SetStatus(JsonObject release, string status)
    {
        if (release["info"] is not JsonObject info)
        {
            info = [];
            release["info"] = info;
        }

        info["status"] = status;
    }

    public static void Restamp(V1Secret secret, JsonObject payload, string status, DateTimeOffset now)
    {
        SetStatus(payload, status);
        secret.Data = new Dictionary<string, byte[]> { ["release"] = HelmReleaseSecretCodec.Encode(payload) };
        secret.Metadata ??= new V1ObjectMeta();
        secret.Metadata.Labels ??= new Dictionary<string, string>();
        secret.Metadata.Labels["status"] = status;
        secret.Metadata.Labels["modifiedAt"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> _Labels(string release, int revision, string status, DateTimeOffset now) => new()
    {
        ["owner"] = "helm",
        ["name"] = release,
        ["status"] = status,
        ["version"] = revision.ToString(CultureInfo.InvariantCulture),
        ["modifiedAt"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
    };

    private static string _Timestamp(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture);
}
