using System.Diagnostics;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>Opens a GitHub URL in the operator's browser. Only ever http(s), and a browser that will not open is not worth taking the cockpit down for.</summary>
internal static class GitHubBrowser
{
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to do about it, and nothing worth crashing for.
        }
    }
}
