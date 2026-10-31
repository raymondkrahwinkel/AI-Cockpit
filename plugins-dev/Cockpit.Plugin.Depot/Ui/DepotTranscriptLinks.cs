namespace Cockpit.Plugin.Depot.UI;

public static class DepotTranscriptLinks
{
    private static readonly System.Text.RegularExpressions.Regex Path = new("[A-Za-z0-9._-]+/[A-Za-z0-9._-]+ › \\S+", System.Text.RegularExpressions.RegexOptions.Compiled);

    // AC-1955: a /n/{token} link opens as a note here; an /artifact/ link is a Depot web page that belongs to
    // the system browser, so it is not claimed any longer.
    public static bool TryMatch(string link, IReadOnlyList<string> connectionUrls, out int start, out int length)
    {
        var path = Path.Match(link);
        if (path.Success)
        {
            start = path.Index;
            length = path.Length;
            return connectionUrls.Count > 0;
        }

        var matches = Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && IsNoteLink(link)
            && connectionUrls.Any(connection => Uri.TryCreate(connection, UriKind.Absolute, out var configured)
                && string.Equals(configured.Host, uri.Host, StringComparison.OrdinalIgnoreCase));
        start = 0;
        length = matches ? link.Length : 0;
        return matches;
    }

    // AC-1581: a /n/{token} link, which opens as a note in Cockpit instead of the file browser.
    public static bool IsNoteLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && DepotNoteRef.FromHref(uri.AbsolutePath) is not null;
}
