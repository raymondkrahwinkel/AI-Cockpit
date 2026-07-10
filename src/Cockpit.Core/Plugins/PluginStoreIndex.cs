using System.Text.Json;

namespace Cockpit.Core.Plugins;

/// <summary>
/// The catalogue a plugin store publishes (#14): the store's name and the plugins it offers. Fetched from
/// a public repo's <c>index.json</c>. The catalogue advertises plugins; the zip's own <c>plugin.json</c>
/// remains the source of truth at install time (consent + hash pin still apply).
/// </summary>
public sealed record PluginStoreIndex(string? Name, IReadOnlyList<PluginStoreEntry> Plugins)
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

            index = parsed.Plugins is null ? parsed with { Plugins = [] } : parsed;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Invalid store index JSON: {exception.Message}";
            return false;
        }
    }
}
