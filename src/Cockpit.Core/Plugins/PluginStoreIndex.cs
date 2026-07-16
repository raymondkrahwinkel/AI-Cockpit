using System.Text.Json;

namespace Cockpit.Core.Plugins;

/// <summary>
/// The catalogue a plugin store publishes (#14): the store's name, the plugins it offers, and — since #69 — the
/// workflow templates it offers. Fetched from a public repo's <c>index.json</c>. The catalogue advertises plugins; the
/// zip's own <c>plugin.json</c> remains the source of truth at install time (consent + hash pin still apply).
/// <para>
/// <see cref="Templates"/>, <see cref="Icon"/> and <see cref="IconUrl"/> are additive and default to empty/null, so
/// an <c>index.json</c> published before they existed still parses. A store that offers no templates has none to
/// show; one that sets neither icon falls back to a default storefront glyph in the Manage-stores dialog.
/// <see cref="Icon"/> is a single emoji/glyph (like a plugin's <see cref="PluginStoreEntry.Icon"/>);
/// <see cref="IconUrl"/> is a real logo image — an http(s) URL, or one relative to the index — fetched and shown
/// when present, with <see cref="Icon"/> then the default glyph as fallbacks.
/// </para>
/// </summary>
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
