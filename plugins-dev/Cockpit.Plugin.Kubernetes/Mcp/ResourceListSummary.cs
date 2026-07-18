using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Mcp;

/// <summary>
/// Turns a resource list into a compact summary — each item's name, namespace and creation time — so a list tool
/// returns something an agent can scan without shipping the full body of every item (and, for a secret list, without
/// its data). Extracted from the tools so it can be tested directly against a literal payload.
/// </summary>
internal static class ResourceListSummary
{
    public static JsonNode Summarize(RawKubernetesList list)
    {
        var items = new JsonArray();
        foreach (var item in list.Items)
        {
            string? name = null, itemNamespace = null, created = null;
            if (item.Data.TryGetValue("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                name = _StringProperty(metadata, "name");
                itemNamespace = _StringProperty(metadata, "namespace");
                created = _StringProperty(metadata, "creationTimestamp");
            }

            items.Add(new JsonObject
            {
                ["name"] = name,
                ["namespace"] = itemNamespace,
                ["creationTimestamp"] = created,
            });
        }

        return new JsonObject { ["count"] = items.Count, ["items"] = items };
    }

    private static string? _StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
