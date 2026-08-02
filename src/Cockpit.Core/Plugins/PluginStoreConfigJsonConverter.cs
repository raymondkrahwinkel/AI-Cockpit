using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Plugins;

// Reads a `PluginStoreConfig` from either shape it can take in `cockpit.json` (AC-7): a bare
// URL string — how every store was written before AC-7, read as a public remote store — or an object with
// `kind`, `location` and an optional `token`. It always writes the object form, so the file
// migrates itself the first time it is saved. The token is written under the name `token` so the host's
// secret layer encrypts it at rest and scrubs it from backups.
public sealed class PluginStoreConfigJsonConverter : JsonConverter<PluginStoreConfig>
{
    public override PluginStoreConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var url = reader.GetString();

            return string.IsNullOrWhiteSpace(url) ? null : PluginStoreConfig.Remote(url);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A plugin store must be a URL string or an object.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        // location is the canonical key; url/path are accepted so a hand-edited config still reads.
        var location = _String(root, "location") ?? _String(root, "url") ?? _String(root, "path");
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var kind = string.Equals(_String(root, "kind"), "local", StringComparison.OrdinalIgnoreCase)
            ? PluginStoreKind.Local
            : PluginStoreKind.Remote;

        return new PluginStoreConfig(kind, location, kind == PluginStoreKind.Local ? null : _NullIfBlank(_String(root, "token")));
    }

    public override void Write(Utf8JsonWriter writer, PluginStoreConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind == PluginStoreKind.Local ? "local" : "remote");
        writer.WriteString("location", value.Location);

        if (value.HasToken)
        {
            writer.WriteString("token", value.Token);
        }

        writer.WriteEndObject();
    }

    private static string? _String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? _NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
