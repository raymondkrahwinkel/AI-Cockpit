using System.Text.Json;

namespace Cockpit.Core.Plugins;

// AC-1013: The catalogue a plugin store publishes (#14, plus #69 workflow templates), fetched from a
// public repo's `index.json`; the zip's own `plugin.json` stays the install-time source of truth.
// `Templates`/`Icon`/`IconUrl` are additive with fallbacks (default glyph, `Icon` before monogram).
public sealed record PluginStoreIndex(
    string? Name,
    IReadOnlyList<PluginStoreEntry> Plugins,
    IReadOnlyList<WorkflowTemplateStoreEntry>? Templates = null,
    string? Icon = null,
    string? IconUrl = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static bool TryParse(string json, out PluginStoreIndex? index, out string? error)
    {
        index = null;
        error = null;

        try
        {
            var parsed = JsonSerializer.Deserialize<PluginStoreIndex>(json, Options);
            if (parsed is null)
            {
                error = "The store index is empty or not a JSON object.";
                return false;
            }

            index = parsed with
            {
                Plugins = parsed.Plugins ?? [],
                Templates = parsed.Templates ?? [],
            };

            return true;
        }
        catch (JsonException exception)
        {
            error = $"Invalid store index JSON: {exception.Message}";
            return false;
        }
    }
}
