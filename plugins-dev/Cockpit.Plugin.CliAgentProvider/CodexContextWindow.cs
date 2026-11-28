using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.CliAgentProvider;

// AC-2421: the window Codex will give a model, known before a start only from the two files in its home directory.
internal static partial class CodexContextWindow
{
    // Null when no model is named, or Codex has not yet cached a window for it; models_cache.json is Codex's own file.
    public static long? Read(string? codexHome, string? model)
    {
        var home = !string.IsNullOrWhiteSpace(codexHome) ? codexHome : Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        try
        {
            var settings = _TopLevelSettings(Path.Combine(home, "config.toml"));
            var configured = long.TryParse(settings.GetValueOrDefault("model_context_window")?.Replace("_", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : (long?)null;
            var slug = !string.IsNullOrWhiteSpace(model) ? model : settings.GetValueOrDefault("model");
            var cache = Path.Combine(home, "models_cache.json");
            if (slug is null || !File.Exists(cache))
            {
                return configured;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(cache));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return configured;
            }

            foreach (var entry in models.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("slug", out var name)
                    && name.ValueKind == JsonValueKind.String && name.GetString() == slug)
                {
                    // Codex lets config.toml widen or narrow the window, never past the model's own maximum.
                    return configured is { } wanted
                        ? Math.Min(wanted, _Number(entry, "max_context_window") ?? wanted)
                        : _Number(entry, "context_window");
                }
            }

            return configured;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static long? _Number(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : null;

    // ponytail: only the plain `key = value` lines above the first table; a Codex `profile` table is not followed.
    private static Dictionary<string, string> _TopLevelSettings(string path)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return settings;
        }

        foreach (var line in File.ReadLines(path).TakeWhile(line => !line.TrimStart().StartsWith('[')))
        {
            if (_Setting().Match(line) is { Success: true } match)
            {
                settings[match.Groups["key"].Value] = match.Groups["value"].Value;
            }
        }

        return settings;
    }

    [GeneratedRegex("""^\s*(?<key>model|model_context_window)\s*=\s*["']?(?<value>[^"'#\s]+)""")]
    private static partial Regex _Setting();
}
