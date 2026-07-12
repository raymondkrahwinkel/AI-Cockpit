using System.Text;
using System.Text.Json;

namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// Produces a stable fingerprint for a tool-call input so two inputs that differ only in property
/// order or insignificant whitespace compare equal. Used to match an exact-scope
/// <see cref="PermissionRule"/> against a proposed call.
/// </summary>
public static class PermissionInputMatch
{
    /// <summary>
    /// Canonicalizes <paramref name="inputJson"/> to a deterministic string: object keys sorted,
    /// whitespace stripped. Malformed or empty JSON canonicalizes to its trimmed self so a rule can
    /// still be compared without throwing — an unparseable input simply only matches an identically
    /// unparseable one.
    /// </summary>
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
                // Re-serialize the decoded string through the same encoder so a value the two
                // sources escaped differently canonicalizes identically. The stream tool_use JSON
                // carries a '>' as the literal character, while the MCP permission_prompt JSON emits
                // it as the ">" unicode escape; GetRawText() keeps each source's raw form, so an
                // exact rule for any input with '>' / '<' / '&' (i.e. most shell commands) never
                // matched. GetString() decodes both to the same char before re-encoding. See bug #27.
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
