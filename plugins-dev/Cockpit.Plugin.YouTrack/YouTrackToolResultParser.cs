using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>The issue an attach targets, read from a YouTrack tool result (AC-116): the issue id, and the host of its web URL when the result carried one (used to pick the instance among several).</summary>
internal sealed record YouTrackAttachTarget(string IssueId, string? Host);

/// <summary>
/// Reads the issue an attach should target out of a YouTrack create/update tool's result (AC-116). The result
/// is normally the JSON the JetBrains YouTrack MCP returns — <c>{"issueId":"AC-116","url":"…/issue/AC-116",…}</c>
/// for a create, <c>{"issueId":"AC-116","updatedFields":[…]}</c> for an update — so the id comes from
/// <c>issueId</c>/<c>idReadable</c>/<c>id</c> (YouTrack accepts either the readable or the internal id in the
/// attachments path) and the host, when present, from the <c>url</c>. Because that shape is an external
/// contract this plugin does not control, a result that is not that clean JSON object (multiple text blocks
/// concatenated, an array, a wrapping envelope, a human-readable line) falls back to scanning the text for a
/// YouTrack issue web URL. Returns null only when neither yields an issue, so an unrelated result is ignored.
/// </summary>
internal static partial class YouTrackToolResultParser
{
    public static YouTrackAttachTarget? TryParse(string resultContent)
    {
        if (string.IsNullOrWhiteSpace(resultContent))
        {
            return null;
        }

        return _FromJson(resultContent) ?? _FromIssueUrl(resultContent);
    }

    private static YouTrackAttachTarget? _FromJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var id = _String(root, "issueId") ?? _String(root, "idReadable") ?? _String(root, "id");
            return id is { Length: > 0 } ? new YouTrackAttachTarget(id, YouTrackUrl.HostOf(_String(root, "url"))) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Fallback for a result that is not the clean JSON object: the first YouTrack issue web URL in the text —
    // "https://host/…/issue/AC-116" — gives both the id and the host. Scoped to a URL ending in /issue/{id}, so
    // it does not match an id mentioned in prose (which carries no host to attach against anyway).
    private static YouTrackAttachTarget? _FromIssueUrl(string content)
    {
        var match = IssueUrlPattern().Match(content);
        return match.Success ? new YouTrackAttachTarget(match.Groups["id"].Value, match.Groups["host"].Value) : null;
    }

    private static string? _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Matches both the API-derived "https://host/issue/AC-1" (YouTrackClient.BuildIssueUrl) and the
    // JetBrains web form "https://host/youtrack/issue/AC-1" — the path segment before /issue/ is optional.
    [GeneratedRegex(@"https?://(?<host>[^/\s""']+)(?:/[^\s""']*?)?/issue/(?<id>[A-Za-z][A-Za-z0-9_]*-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IssueUrlPattern();
}
