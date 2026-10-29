using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Cockpit.Core.Swarm;

public static class SwarmRecordJson
{
    // The envelope on the wire. Strict on nulls and missing fields, because every record arrives from another machine.
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    // The payload's one spelling that signatures cover: properties in ordinal name order, no whitespace, strings escaped by the default
    // encoder, integers only (a fraction or exponent has more than one spelling). Duplicate names are refused, since
    // a reader taking the last one would see a different payload than the signer meant.
    public static byte[] Canonicalize(JsonElement payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            _Write(writer, payload);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static bool TryCanonicalize(JsonElement payload, [NotNullWhen(true)] out byte[]? canonical)
    {
        try
        {
            canonical = Canonicalize(payload);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            canonical = null;
            return false;
        }
    }

    private static void _Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previous = null;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.Name == previous)
                    {
                        throw new FormatException($"Duplicate property '{property.Name}' in a swarm payload.");
                    }

                    previous = property.Name;
                    writer.WritePropertyName(property.Name);
                    _Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    _Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number when element.TryGetInt64(out var number):
                writer.WriteNumberValue(number);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new FormatException($"A swarm payload cannot hold {element.ValueKind} '{element}'; numbers must be integers.");
        }
    }
}
