using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cockpit.App.Services;

/// <summary>
/// Hands a web address to whatever the operator browses with — the one place in the app that does. The same two rules
/// every surface with a link needs: only <c>http</c> and <c>https</c> ever reach the shell, and a browser that fails to
/// start must not take the UI thread with it.
/// <para>
/// It exists because four views had grown their own copy of exactly this, each one's comment pointing at the last
/// (AC-315). A guard duplicated per view is a guard that only holds until someone tightens one copy.
/// </para>
/// </summary>
internal static class ExternalLink
{
    /// <summary>
    /// Parses <paramref name="url"/> if and only if it is something this will hand to the shell: an absolute
    /// <c>http</c> or <c>https</c> address, and nothing else. The decision is its own method so it can be tested in
    /// both directions — a test that exercised the opening half would start a browser on the machine running it, so
    /// otherwise the guard could be inverted, leaving every link silently dead, with the suite still green.
    /// <para>
    /// A caller that has to tell "not a link" apart from "the browser would not start" — the terminal, which only
    /// claims a click it can act on — asks this first and then opens the address it got back.
    /// </para>
    /// <para>
    /// <c>Cockpit.Core</c>'s <see cref="Cockpit.Core.Projects.ProjectInfoField.IsWebLink"/> applies the same rule to
    /// decide whether to <em>draw</em> a value as a link. Two places by necessity — the core cannot reference the app
    /// — and both are tested, because a value drawn as followable that this then refuses is a link that does nothing.
    /// </para>
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
    /// Opens <paramref name="url"/> in the operator's browser. Returns false, having started nothing, when it is not
    /// an <c>http(s)</c> address or the browser refused to launch — a value typed by hand is as likely to be a note
    /// as a link, so a refusal is the ordinary case and not an error worth interrupting anyone over.
    /// </summary>
    public static bool TryOpen(string? url) =>
        TryParseWebAddress(url, out var address) && TryOpen(address);

    /// <summary>
    /// Opens an address already known to be a web address, for a caller that parsed it to decide something else first.
    /// Returns whether the browser started.
    /// <para>
    /// It re-checks the scheme rather than trusting the caller. This class is the one place the "only http(s) reaches
    /// the shell" rule lives, and a rule enforced only by the discipline of whoever calls it is not enforced: a future
    /// caller holding a <see cref="Uri"/> from a config file or a plugin could reach the shell past the guard, and the
    /// test that watches for new shell-outs would not see it, because such a caller writes none of its own.
    /// </para>
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
