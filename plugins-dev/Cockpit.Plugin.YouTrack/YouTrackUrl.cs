namespace Cockpit.Plugin.YouTrack;

// The host of a URL (AC-116), or null when it is not a parseable absolute URL — shared by the result parser and the instance resolver so both read a host the same way.
internal static class YouTrackUrl
{
    public static string? HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
}
