using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Helm;

// One decoded `helm.sh/release.v1` secret (AC-1061 fase 1) — the fields the read tools report, pulled out of the
// release JSON once so `helm_list`/`helm_status`/`helm_history`/`helm_values`/`helm_manifest` each shape their own
// slice of it instead of re-walking the raw JSON tree.
internal sealed record HelmRelease(
    string Name,
    string Namespace,
    int Revision,
    string Status,
    string? FirstDeployed,
    string? LastDeployed,
    string? Notes,
    string? ChartName,
    string? ChartVersion,
    string? AppVersion,
    JsonNode? Config,
    JsonNode? ChartDefaultValues,
    string? Manifest)
{
    private string? ChartDisplay => ChartName is null ? null : ChartVersion is null ? ChartName : $"{ChartName}-{ChartVersion}";

    public static HelmRelease FromJson(JsonObject release)
    {
        var info = release["info"] as JsonObject;
        var chart = release["chart"] as JsonObject;
        var metadata = chart?["metadata"] as JsonObject;

        return new HelmRelease(
            Name: _String(release["name"]) ?? string.Empty,
            Namespace: _String(release["namespace"]) ?? string.Empty,
            Revision: release["version"]?.GetValue<int>() ?? 0,
            Status: _String(info?["status"]) ?? "unknown",
            FirstDeployed: _String(info?["first_deployed"]),
            LastDeployed: _String(info?["last_deployed"]),
            Notes: _String(info?["notes"]),
            ChartName: _String(metadata?["name"]),
            ChartVersion: _String(metadata?["version"]),
            AppVersion: _String(metadata?["appVersion"]),
            Config: release["config"]?.DeepClone(),
            ChartDefaultValues: chart?["values"]?.DeepClone(),
            Manifest: _String(release["manifest"]));
    }

    public JsonObject ToListEntry() => new()
    {
        ["name"] = Name,
        ["revision"] = Revision,
        ["status"] = Status,
        ["chart"] = ChartDisplay,
        ["appVersion"] = AppVersion,
        ["lastDeployed"] = LastDeployed,
    };

    public JsonObject ToHistoryEntry() => new()
    {
        ["revision"] = Revision,
        ["status"] = Status,
        ["chart"] = ChartDisplay,
        ["appVersion"] = AppVersion,
        ["deployed"] = LastDeployed,
    };

    public JsonObject ToStatus() => new()
    {
        ["name"] = Name,
        ["namespace"] = Namespace,
        ["revision"] = Revision,
        ["status"] = Status,
        ["chart"] = ChartDisplay,
        ["appVersion"] = AppVersion,
        ["firstDeployed"] = FirstDeployed,
        ["lastDeployed"] = LastDeployed,
        ["notes"] = Notes,
    };

    public JsonObject ToValues(bool includeChartDefaults)
    {
        var values = new JsonObject { ["revision"] = Revision, ["values"] = Config?.DeepClone() };
        if (includeChartDefaults)
        {
            values["chartDefaultValues"] = ChartDefaultValues?.DeepClone();
        }

        return values;
    }

    public JsonObject ToManifest() => new()
    {
        ["revision"] = Revision,
        ["manifest"] = Manifest,
    };

    private static string? _String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
