using System.Diagnostics;

namespace Cockpit.App.Services;

/// <summary>
/// Hands a web address to whatever the operator browses with. The same two rules every view that opens a link
/// already applies, in one place: only <c>http</c> and <c>https</c> ever reach the shell, and a browser that fails
/// to start must not take the UI thread with it.
/// <para>
/// The dialogs, the markdown renderer and the terminal each still carry their own copy of this — consolidating those
/// touches four views that this change is not about. New callers use this one.
/// </para>
/// </summary>
internal static class ExternalLink
{
    /// <summary>
    /// Opens <paramref name="url"/> in the operator's browser. Returns false, having started nothing, when it is not
    /// an <c>http(s)</c> address or the browser refused to launch — a value typed by hand is as likely to be a note
    /// as a link, so a refusal is the ordinary case and not an error worth interrupting anyone over.
    /// </summary>
    public static bool TryOpen(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            // Best-effort: a failed browser launch must not crash the UI thread.
            return false;
        }
    }
}
