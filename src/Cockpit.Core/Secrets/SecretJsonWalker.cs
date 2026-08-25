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
                    if (value is JsonValue text && text.TryGetValue<string>(out var raw) && Embedded(raw) is { } embedded)
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
    private static JsonNode? Embedded(string value)
    {
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
