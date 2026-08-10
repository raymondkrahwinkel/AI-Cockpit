using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Closes the gap CockpitProjectDefinitionSecrecyTests once pinned as open (AC-607 decision 3): an unrecognised
// ExtensionData field from a newer Cockpit build is refused, not blindly forwarded, when its key looks secret-shaped
// (SensitiveFieldNameHeuristic) and its value is not already provably encrypted (ProjectSecretProtector.IsProtected).
// A key that does not match the heuristic still forwards untouched — ordinary forward-compat for a genuinely benign
// future field is preserved; only the secret-shaped gap is closed.
//
// Walks top-level keys plus one level of nested JSON objects, not into arrays — the same "narrow and defensible
// rather than a general scanner" reasoning ProjectResourceSecretPathHeuristic already states for itself.
public static class CockpitProjectDefinitionExtensionDataGuard
{
    public static (Dictionary<string, JsonElement>? Kept, IReadOnlyList<string> DroppedKeys) Apply(
        Dictionary<string, JsonElement>? extensionData)
    {
        if (extensionData is null || extensionData.Count == 0)
        {
            return (null, []);
        }

        var dropped = new List<string>();
        var kept = new Dictionary<string, JsonElement>();

        foreach (var (key, element) in extensionData)
        {
            var node = JsonNode.Parse(element.GetRawText());
            if (node is null)
            {
                kept[key] = element;
                continue;
            }

            if (node is JsonValue leaf)
            {
                if (_IsRefusedLeaf(key, leaf, key, dropped))
                {
                    continue;
                }
            }
            else if (node is JsonObject nested)
            {
                foreach (var childKey in nested.Select(property => property.Key).ToList())
                {
                    if (nested[childKey] is JsonValue childLeaf && _IsRefusedLeaf(childKey, childLeaf, $"{key}.{childKey}", dropped))
                    {
                        nested.Remove(childKey);
                    }
                }
            }

            kept[key] = _ToElement(node);
        }

        return (kept.Count == 0 ? null : kept, dropped);
    }

    // AC-607 review finding 4: CockpitProjectSensitiveFieldEntry carries its own [JsonExtensionData] passthrough
    // (the same forward-compat idiom every wire row has), which the top-level Apply above never reaches — a future
    // build could otherwise smuggle a plaintext fallback field through a sensitive-field row unnoticed. Same rule,
    // applied per row, reported as `SensitiveFields.{Label}.{key}`.
    public static (List<CockpitProjectSensitiveFieldEntry>? Kept, IReadOnlyList<string> DroppedKeys) ApplyToSensitiveFields(
        IReadOnlyList<CockpitProjectSensitiveFieldEntry>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return (null, []);
        }

        var dropped = new List<string>();
        var kept = new List<CockpitProjectSensitiveFieldEntry>(fields.Count);

        foreach (var field in fields)
        {
            var (fieldKept, fieldDropped) = Apply(field.ExtensionData);
            if (fieldDropped.Count == 0)
            {
                kept.Add(field);
                continue;
            }

            dropped.AddRange(fieldDropped.Select(key => $"SensitiveFields.{field.Label}.{key}"));
            kept.Add(new CockpitProjectSensitiveFieldEntry { Label = field.Label, Value = field.Value, ExtensionData = fieldKept });
        }

        return (kept, dropped);
    }

    private static bool _IsRefusedLeaf(string nameKey, JsonValue value, string reportedKey, List<string> dropped)
    {
        if (!SensitiveFieldNameHeuristic.IsSecretName(nameKey) || !value.TryGetValue<string>(out var text))
        {
            return false;
        }

        if (ProjectSecretProtector.IsProtected(text))
        {
            return false;
        }

        dropped.Add(reportedKey);
        return true;
    }

    private static JsonElement _ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
