using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Configuration;

// Reads the `WindowBounds` section of `cockpit.json` in either shape it can take: the pre-AC-866 flat form (a
// single `WindowBoundsEntry` object, for the main window alone) or the keyed form
// (`{"main": {...}, "assistant": {...}}`, one entry per window). A flat object reads as the `"main"` entry —
// smallest diff for the data already on disk — and this always writes the keyed form, so the file migrates
// itself the first time anything saves.
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
