using System.Diagnostics;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Opens a GitHub URL in the operator's browser. Only ever http(s): anything else is reported rather than handed
/// to the shell. A browser that will not start is worth saying out loud, and not worth taking the cockpit down
/// for — hence a message back instead of an exception, and instead of the silence it used to keep.
/// </summary>
internal static class GitHubBrowser
{
    /// <summary>Launches <paramref name="url"/>, and returns <see langword="null"/> when it did — otherwise the reason it did not, ready to show the operator.</summary>
    public static string? Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"Not a web address this can open: \"{url}\".";
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        catch (Exception exception)
        {
            return $"Could not open the browser: {exception.Message}";
        }
    }
}
