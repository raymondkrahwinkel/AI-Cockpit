using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Closes the gap CockpitProjectDefinitionSecrecyTests pinned as open (AC-607 decision 3): an unrecognised
// ExtensionData field is refused, not blindly forwarded, when its key looks secret-shaped and its value isn't
// already provably encrypted. Walks top-level keys plus one nesting level, not arrays — narrow on purpose.
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

    // AC-607 review finding 4: CockpitProjectSensitiveFieldEntry's own [JsonExtensionData] passthrough is never
    // reached by the top-level Apply above — a future build could smuggle a plaintext fallback through a
    // sensitive-field row unnoticed. Same rule, applied per row, reported as `SensitiveFields.{Label}.{key}`.
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
