using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cockpit.App.Services;

/// <summary>
/// Hands a web address to whatever the operator browses with — the only thing in <c>Cockpit.App</c> that does. Two
/// rules, in one place: only <c>http</c> and <c>https</c> reach the shell, and a browser that fails to start must not
/// take the UI thread with it. Four views had grown their own copy of exactly this, each comment pointing at the last
/// (AC-315), and a guard duplicated per view holds only until someone tightens one copy.
/// <para>
/// <c>Cockpit.Infrastructure</c> keeps its own for the MCP OAuth sign-in: it cannot reference the app, so that one
/// stays a second implementation of the same rule rather than a caller of this.
/// </para>
/// </summary>
internal static class ExternalLink
{
    /// <summary>
    /// Parses <paramref name="url"/> only if it is an absolute <c>http(s)</c> address. Its own method so the decision
    /// is testable in both directions — exercising the opening half would start a browser on the machine running the
    /// test, so otherwise an inverted guard would leave every link dead with the suite still green. A caller that has
    /// to tell "not a link" from "the browser would not start" asks this first, then opens what it got back.
    /// </summary>
    public static bool TryParseWebAddress(string? url, [NotNullWhen(true)] out Uri? address)
    {
        address = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                  (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

        return address is not null;
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the operator's browser. Returns false, having started nothing, when it is not an
    /// <c>http(s)</c> address or the browser refused to launch — a value typed by hand is as likely to be a note as a
    /// link, so a refusal is the ordinary case and not worth interrupting anyone over.
    /// </summary>
    public static bool TryOpen(string? url) =>
        TryParseWebAddress(url, out var address) && TryOpen(address);

    /// <summary>
    /// Opens an address a caller already parsed to decide something else first. Re-checks the scheme rather than
    /// trusting them: a rule this class owns but only its callers apply is not enforced, and a caller reaching the
    /// shell this way writes no shell-out of its own for the source scan to notice.
    /// </summary>
    public static bool TryOpen(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            // Best-effort: a failed browser launch must not crash the UI thread.
            return false;
        }
    }
}
