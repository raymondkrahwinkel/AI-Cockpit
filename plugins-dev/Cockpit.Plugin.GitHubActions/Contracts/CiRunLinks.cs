using System.Diagnostics;

namespace Cockpit.Plugin.GitHubActions.Contracts;

// Opening a run's URL is a local, client-side action (no repository or `gh` involved), so it needs no round trip
// through the channel — both parts link this file as source and the UI calls it directly (AC-1394).
internal static class CiRunLinks
{
    // Whether a run URL is a safe https github.com link to hand to the OS browser opener.
    internal static bool IsGitHubRunUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal));

    // Opens a run's URL in the OS's default browser handler (never a shell string), shared by the header dot and
    // the dock panel's rows. Best effort: a non-GitHub url or a machine with no handler does nothing.
    public static void OpenRunInBrowser(string? url)
    {
        if (url is not { Length: > 0 } || !IsGitHubRunUrl(url))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is a convenience — a machine without a handler just does nothing.
        }
    }
}
