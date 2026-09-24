using System.Text.Json;

namespace Cockpit.Core.Plugins;

// The parsed `plugin.json` a plugin folder carries. Read and validated before anything is loaded,
// so a malformed or version-mismatched plugin is rejected with a message rather than a
// `TypeLoadException` mid-load.
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string? EntryAssembly,
    int AbstractionsVersion,
    string? EntryType,
    string? MinHostVersion,
    string? Description,
    string? Author,
    IReadOnlyList<string>? SecretKeys = null,
    string? UiAssembly = null,
    string? UiEntryType = null)
{
    // AC-1013: storage keys the host can't guess as credentials (beyond token/apiKey/secret/password/
    // webhook); read before load so matching values decrypt on the way in instead of reaching the plugin
    // as ciphertext. (Omitted: the install-time credential-intent framing; see ticket.)
    public IReadOnlyList<string> SecretKeys { get; } = SecretKeys ?? [];

    // AC-1389: the assemblies the loader reads, backend part first. A UI-only plugin (a clock) has no entryAssembly,
    // and a plugin not yet split in two has no uiAssembly; parsing refuses one with neither.
    public IEnumerable<string> Assemblies => new[] { EntryAssembly, UiAssembly }.OfType<string>();

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
                || !TryGetNonEmptyString(root, "version", out var version))
            {
                error = "Missing required string field (id, name, version).";
                return false;
            }

            var entryAssembly = TryGetNonEmptyString(root, "entryAssembly", out var entry) ? entry : null;
            var uiAssembly = TryGetNonEmptyString(root, "uiAssembly", out var ui) ? ui : null;
            if (entryAssembly is null && uiAssembly is null)
            {
                error = "Missing both 'entryAssembly' and 'uiAssembly': a plugin names at least one assembly to load.";
                return false;
            }

            var uiEntryType = GetOptionalString(root, "uiEntryType");
            if (uiEntryType is not null && uiAssembly is null)
            {
                error = "'uiEntryType' needs 'uiAssembly': name the assembly the UI entry type is in.";
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
                GetOptionalString(root, "author"),
                GetOptionalStrings(root, "secretKeys"),
                uiAssembly,
                uiEntryType);
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

    private static IReadOnlyList<string>? GetOptionalStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return
        [
            .. element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
        ];
    }
}
