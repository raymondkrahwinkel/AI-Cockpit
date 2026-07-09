using System.Text.Json;

namespace Cockpit.Core.Plugins;

/// <summary>
/// The parsed <c>plugin.json</c> a plugin folder carries. Read and validated before anything is loaded,
/// so a malformed or version-mismatched plugin is rejected with a message rather than a
/// <c>TypeLoadException</c> mid-load.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string EntryAssembly,
    int AbstractionsVersion,
    string? EntryType,
    string? MinHostVersion,
    string? Description,
    string? Author)
{
    public static bool TryParse(string json, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            error = $"Invalid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Manifest root must be a JSON object.";
                return false;
            }

            if (!TryGetNonEmptyString(root, "id", out var id)
                || !TryGetNonEmptyString(root, "name", out var name)
                || !TryGetNonEmptyString(root, "version", out var version)
                || !TryGetNonEmptyString(root, "entryAssembly", out var entryAssembly))
            {
                error = "Missing required string field (id, name, version, entryAssembly).";
                return false;
            }

            if (!root.TryGetProperty("abstractionsVersion", out var abstractionsElement)
                || abstractionsElement.ValueKind != JsonValueKind.Number
                || !abstractionsElement.TryGetInt32(out var abstractionsVersion))
            {
                error = "Missing or non-numeric required field 'abstractionsVersion'.";
                return false;
            }

            manifest = new PluginManifest(
                id,
                name,
                version,
                entryAssembly,
                abstractionsVersion,
                GetOptionalString(root, "entryType"),
                GetOptionalString(root, "minHostVersion"),
                GetOptionalString(root, "description"),
                GetOptionalString(root, "author"));
            return true;
        }
    }

    private static bool TryGetNonEmptyString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        return false;
    }

    private static string? GetOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
