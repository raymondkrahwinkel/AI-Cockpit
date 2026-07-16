using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// A Claude profile's configuration as this plugin reads it from the opaque config JSON the host round-trips
/// (Fase 4). Mirrors the host's <c>ClaudeConfig(ConfigDir, ExecutablePath)</c> — the plugin cannot reference the
/// core type, so it deserializes the same two fields into its own shape, the way every provider plugin does.
/// </summary>
internal sealed record ClaudeProviderConfig(
    [property: JsonPropertyName("configDir")] string? ConfigDir = null,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath = null)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a config JSON blob, or an empty config when it is blank/unreadable — a profile-less or default session.</summary>
    public static ClaudeProviderConfig Parse(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new ClaudeProviderConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<ClaudeProviderConfig>(configJson, JsonOptions) ?? new ClaudeProviderConfig();
        }
        catch (JsonException)
        {
            return new ClaudeProviderConfig();
        }
    }
}
