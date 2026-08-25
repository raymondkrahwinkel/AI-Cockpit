using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Argo;

// AC-576 phase 5: shapes Argo's managed-resources response into the consent lines argo_sync's approval card
// shows — same bounded-block form as ManifestDiff.ToConsentLines from the Helm tools (AC-1061), so the operator
// sees the literal per-resource diff before approving a sync, not just an app name.
internal static class ArgoManagedResourcesDiff
{
    // The lines for the consent card, and how many resources actually differ from Git — the caller uses the
    // count to skip asking for approval entirely when there is nothing to sync.
    public static (IReadOnlyList<string> Lines, int ModifiedCount) Summarize(JsonNode? managedResources, int maxLength)
    {
        var items = _Items(managedResources);
        var modified = items.Where(_IsModified).ToList();
        var lines = new List<string> { $"{modified.Count} resource(s) differ from Git ({items.Count - modified.Count} unchanged)" };
        var length = lines[0].Length;

        var truncated = 0;
        foreach (var item in modified)
        {
            var entryLines = _EntryLines(item);
            var entryLength = entryLines.Sum(line => line.Length) + entryLines.Length;
            if (length + entryLength > maxLength)
            {
                truncated++;
                continue;
            }

            lines.AddRange(entryLines);
            length += entryLength;
        }

        if (truncated > 0)
        {
            lines.Add($"… and {truncated} more resource(s) not shown — read them with argo_app first");
        }

        return (lines, modified.Count);
    }

    private static IReadOnlyList<JsonObject> _Items(JsonNode? root) => root switch
    {
        JsonArray array => array.OfType<JsonObject>().ToList(),
        JsonObject single when single["items"] is JsonArray items => items.OfType<JsonObject>().ToList(),
        _ => [],
    };

    private static bool _IsModified(JsonObject item) =>
        item["modified"] is JsonValue value && value.TryGetValue<bool>(out var modified) && modified;

    private static string[] _EntryLines(JsonObject item)
    {
        var identity = $"{_String(item["kind"])}/{_String(item["name"])}".Trim('/');
        var ns = _String(item["namespace"]);
        var header = string.IsNullOrEmpty(ns) ? $"~ {identity}" : $"~ {identity} in {ns}";
        var diff = _String(item["diff"]);
        return string.IsNullOrWhiteSpace(diff) ? [header] : new[] { header }.Concat(diff.Split('\n')).ToArray();
    }

    private static string? _String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
