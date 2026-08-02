using System.Text.Json;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// (De)serializes `CockpitProjectDefinition` — the one place that owns the JSON options, so a reader
// and a writer never quietly disagree on them (AC-244).
public static class CockpitProjectDefinitionJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions _Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Serialize(CockpitProjectDefinition definition) =>
        JsonSerializer.Serialize(definition, _Options);

    // Never throws, including for a null `json` — a corrupt, truncated or empty MCP response costs this one call, not the caller (AC-244).
    public static bool TryDeserialize(string? json, out CockpitProjectDefinition? definition, out string? error)
    {
        if (string.IsNullOrEmpty(json))
        {
            definition = null;
            error = "The project definition was empty.";
            return false;
        }

        try
        {
            definition = JsonSerializer.Deserialize<CockpitProjectDefinition>(json, _Options);
            if (definition is null)
            {
                error = "The project definition was empty.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            definition = null;
            error = $"Couldn't read .cockpit/project.json: {exception.Message}";
            return false;
        }
    }
}
