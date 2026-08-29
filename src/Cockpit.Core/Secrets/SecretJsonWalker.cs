using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Secrets;

// Walks the cockpit's settings and rewrites every credential-bearing string, wherever it sits. Shared by the
// backup scrubber and the encryption layer so both traverse the same way — including into a plugin's settings,
// which are stored as a JSON string *inside* the cockpit's JSON and would otherwise be missed.
public static class SecretJsonWalker
{
    // Applies `transform` to every secret-named string value in `root`, in place, passing the field's JSON path
    // and current value; returns `null` to leave untouched. Returns the paths it rewrote.
    public static IReadOnlyList<string> Transform(JsonNode root, SecretFields fields, Func<string, string, string?> transform)
    {
        var rewritten = new List<string>();
        Walk(root, string.Empty, fields, transform, rewritten);

        return rewritten;
    }

    // Whether `root` carries any credential at all. Reads rather than rewrites, so a caller that only wants the
    // answer needs no clone of the whole document to keep `Transform` from changing it on the way (AC-1152).
    public static bool ContainsSecret(JsonNode root, SecretFields fields)
    {
        var found = false;
        Walk(root, string.Empty, fields, (_, _) =>
        {
            found = true;

            return null;
        }, []);

        return found;
    }

    private static void Walk(
        JsonNode? node,
        string path,
        SecretFields fields,
        Func<string, string, string?> transform,
        List<string> rewritten)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, value) in json.ToList())
                {
                    var here = path.Length == 0 ? key : $"{path}.{key}";

                    if (fields.IsSecret(key) && value is JsonValue)
                    {
                        if (value.GetValue<object>()?.ToString() is { Length: > 0 } current
                            && transform(here, current) is { } replacement)
                        {
                            json[key] = replacement;
                            rewritten.Add(here);
                        }

                        continue;
                    }

                    Walk(value, here, fields, transform, rewritten);

                    // A plugin's storage is JSON inside a string. Rewritten only when something in it actually
                    // changed, so this does not gratuitously reformat every plugin's settings.
                    if (value is JsonValue text && text.TryGetValue<string>(out var raw) && Embedded(raw, fields) is { } embedded)
                    {
                        var before = rewritten.Count;
                        Walk(embedded, here, fields, transform, rewritten);

                        if (rewritten.Count > before)
                        {
                            json[key] = embedded.ToJsonString();
                        }
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    Walk(array[index], $"{path}[{index}]", fields, transform, rewritten);
                }

                break;
        }
    }

    // A string that is itself a JSON object or array — how a plugin stores its settings inside the cockpit's.
    // Built into a tree only when a scan of it finds something to rewrite: parsing every plugin's cache on every
    // read and every write was over half of what AC-1152 measured. The scan decides that, never the size.
    private static JsonNode? Embedded(string value, SecretFields fields)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return WorthWalking(value, fields) ? JsonNode.Parse(value) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Whether anything in `value` could be rewritten: a credential-named property, or a string that is itself
    // JSON and so may hold one a level further down. Reads the names off the bytes rather than materialising a
    // tree, so an escaped name is compared the way the parser would have compared it.
    private static bool WorthWalking(string value, SecretFields fields)
    {
        var utf8 = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
        try
        {
            var reader = new Utf8JsonReader(utf8.AsSpan(0, Encoding.UTF8.GetBytes(value, utf8)));
            while (reader.Read())
            {
                var worth = reader.TokenType switch
                {
                    JsonTokenType.PropertyName => fields.IsSecret(reader.GetString() ?? string.Empty),

                    // A backslash opens an escape, and an escaped first character could be either brace.
                    JsonTokenType.String => reader.ValueSpan is [(byte)'{' or (byte)'[' or (byte)'\\', ..],
                    _ => false,
                };

                if (worth)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }
}
