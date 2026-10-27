namespace Cockpit.Plugin.Depot.UI;

public static class DepotTranscriptLinks
{
    private static readonly System.Text.RegularExpressions.Regex Path = new("[A-Za-z0-9._-]+/[A-Za-z0-9._-]+ › \\S+", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            && uri.AbsolutePath.StartsWith("/artifact/", StringComparison.Ordinal)
            && connectionUrls.Any(connection => Uri.TryCreate(connection, UriKind.Absolute, out var configured)
                && string.Equals(configured.Host, uri.Host, StringComparison.OrdinalIgnoreCase));
        start = 0;
        length = matches ? link.Length : 0;
        return matches;
    }
}
