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
    /// Whether <paramref name="url"/> is something this will hand to the shell: an absolute <c>http</c> or
    /// <c>https</c> address, and nothing else. Its own method so the decision can be tested in both directions — a
    /// test that exercised the opening half would start a browser on the machine running it, so without this the guard
    /// could be inverted and every test would still pass.
    /// <para>
    /// <c>Cockpit.Core</c>'s <see cref="Cockpit.Core.Projects.ProjectInfoField.IsWebLink"/> applies the same rule to
    /// decide whether to <em>draw</em> a value as a link. Two places by necessity — the core cannot reference the app
    /// — and both are tested, because a value drawn as followable that this then refuses is a link that does nothing.
    /// </para>
    /// </summary>
    public static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && _IsWebAddress(uri);

    /// <summary>
    /// Opens <paramref name="url"/> in the operator's browser. Returns false, having started nothing, when it is not
    /// an <c>http(s)</c> address or the browser refused to launch — a value typed by hand is as likely to be a note
    /// as a link, so a refusal is the ordinary case and not an error worth interrupting anyone over.
    /// </summary>
    public static bool TryOpen(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_IsWebAddress(uri))
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

    private static bool _IsWebAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
