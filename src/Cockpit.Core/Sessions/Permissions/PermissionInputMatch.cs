using System.Text;
using System.Text.Json;

namespace Cockpit.Core.Sessions.Permissions;

// Produces a stable fingerprint for a tool-call input so two inputs that differ only in property
// order or insignificant whitespace compare equal. Used to match an exact-scope
// `PermissionRule` against a proposed call.
public static class PermissionInputMatch
{
    // Canonicalizes `inputJson` to a deterministic string: object keys sorted, whitespace stripped.
    // Malformed/empty JSON canonicalizes to its trimmed self, so it only matches an identically unparseable input.
    public static string Canonicalize(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var builder = new StringBuilder();
            Write(document.RootElement, builder);
            return builder.ToString();
        }
        catch (JsonException)
        {
            return inputJson.Trim();
        }
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                // Bug #27: re-serialize the decoded string through the same encoder — the stream tool_use JSON
                // and the MCP permission_prompt JSON escape '>' differently, so GetRawText() left an exact
                // rule for any '>'/'<'/'&' input never matching; GetString() decodes both the same way first.
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
