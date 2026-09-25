namespace Cockpit.Plugin.YouTrack;

// The host of a URL (AC-116), or null when it is not a parseable absolute URL — shared by the result parser and the instance resolver so both read a host the same way.
internal static class YouTrackUrl
{
    public static string? HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;

    // The issue's web URL, derived from the API base URL by dropping a trailing "/api" — e.g. "https://x.youtrack.cloud/api" -> "https://x.youtrack.cloud/issue/PROJ-123".
    public static string BuildIssueUrl(string instanceBaseUrl, string idReadable)
    {
        var trimmed = instanceBaseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return $"{trimmed}/issue/{idReadable}";
    }
}
