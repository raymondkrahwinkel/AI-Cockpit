using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Schedules.Engine;

// One shape on disk for every store: enums by name, so a reordered enum never turns a Failed into a Succeeded.
internal static class StoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string? json) where T : class
    {
        try
        {
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<T>(json, Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
