using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Configuration;

// AC-866: reads `WindowBounds` in either shape — the pre-AC-866 flat form (main window only, read as the
// "main" entry) or the keyed per-window form — and always writes the keyed form, migrating on next save.
internal sealed class WindowBoundsSectionJsonConverter : JsonConverter<Dictionary<string, WindowBoundsEntry>>
{
    private const string MainKey = "main";

    public override Dictionary<string, WindowBoundsEntry>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        // The old flat form has Width directly on the object; the keyed form nests an object per key.
        if (root.TryGetProperty("Width", out _))
        {
            var entry = root.Deserialize<WindowBoundsEntry>(options);
            return entry is null ? null : new Dictionary<string, WindowBoundsEntry> { [MainKey] = entry };
        }

        return root.Deserialize<Dictionary<string, WindowBoundsEntry>>(options);
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, WindowBoundsEntry> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
