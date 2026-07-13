using System.Text.Json;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The human half of a YouTrack error body. A refused update answers with JSON like
/// <c>{"error":"Bad Request","error_description":"Value is not valid"}</c>; showing the operator that raw blob
/// tells them less than the one sentence inside it. Falls back to the body as-is when it is not that shape.
/// </summary>
internal static class YouTrackErrorMessage
{
    public static string From(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no reason given";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return body.Trim();
            }

            var description = _String(document.RootElement, "error_description") ?? _String(document.RootElement, "error");
            return description ?? body.Trim();
        }
        catch (JsonException)
        {
            return body.Trim();
        }
    }

    private static string? _String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
