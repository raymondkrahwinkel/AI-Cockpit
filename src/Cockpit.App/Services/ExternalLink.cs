using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cockpit.App.Services;

// AC-315: Hands a web address to whatever the operator browses with, the only thing in `Cockpit.App` that does
// (only `http`/`https` reach the shell, a failed browser start must not take the UI thread with it), replacing
// four views that had each grown their own copy of this guard. `Cockpit.Infrastructure` keeps a second copy since it cannot reference the app.
internal static class ExternalLink
{
    // AC-1013: Parses `url` only if it is an absolute `http(s)` address, kept as its own testable method since
    // exercising the opening half would start a real browser and let an inverted guard pass with the suite green.
    // A caller distinguishing "not a link" from "the browser would not start" asks this first.
    public static bool TryParseWebAddress(string? url, [NotNullWhen(true)] out Uri? address)
    {
        address = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                  (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

        return address is not null;
    }

    // Opens `url` in the operator's browser. Returns false, having started nothing, when it is not an
    // `http(s)` address or the browser refused to launch — a value typed by hand is as likely to be a note as a
    // link, so a refusal is the ordinary case and not worth interrupting anyone over.
    public static bool TryOpen(string? url) =>
        TryParseWebAddress(url, out var address) && TryOpen(address);

    // Opens an address a caller already parsed to decide something else first. Re-checks the scheme rather than
    // trusting them: a rule this class owns but only its callers apply is not enforced, and a caller reaching the
    // shell this way writes no shell-out of its own for the source scan to notice.
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

    // Opens a filesystem path with the operator's default system application — FilePreviewWindow's "Openen"
    // button (AC-642). A separate method rather than widening TryOpen's http(s)-only filter: a path is not a
    // URL, and only the explicit second click behind that button reaches this, never a link click.
    public static bool TryOpenWithSystemApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
