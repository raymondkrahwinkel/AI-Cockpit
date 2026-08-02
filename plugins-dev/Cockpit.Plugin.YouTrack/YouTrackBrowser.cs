using System.Diagnostics;

namespace Cockpit.Plugin.YouTrack;

// Opens an issue URL in the operator's browser. The URL is built from the instance address the operator typed
// into the settings, so it is checked before it is handed to the shell: only http(s) is launched, and anything
// else is reported rather than executed. A browser that will not start is worth saying out loud, and not worth
// taking the cockpit down for — hence a message back instead of an exception.
internal static class YouTrackBrowser
{
    // Launches `url`, and returns `null` when it did — otherwise the reason it did not, ready to show the operator.
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
