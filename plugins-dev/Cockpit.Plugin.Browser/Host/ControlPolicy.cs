using System.Text.Json;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

// AC-1728: the decisions control rests on, with nothing the page's own scripts can set. MayAct runs on CEF's UI thread
// right before the input goes in; Navigation runs in CEF's resource handler for every frame; ClickTarget reads what
// DevTools (not the page) says lies under the point. AC-2149: the engine host runs this very file, not a copy.
internal static class ControlPolicy
{
    public enum Verdict
    {
        Allow,
        Hold,
        Drop,
    }

    public const int MaxTyped = 2_000;

    private static readonly HashSet<string> FrameOwners = new(StringComparer.OrdinalIgnoreCase) { "IFRAME", "FRAME", "OBJECT", "EMBED", "PORTAL", "FENCEDFRAME" };

    // The very grant that authorized the action still stands, the tab is still on its origin, and for keys the focused
    // frame is on that origin too, so typing never lands in another site's iframe.
    public static bool MayAct(bool grantStands, string? origin, string? mainUrl, string? focusedUrl, bool needsFocus) =>
        grantStands
        && origin is not null
        && SameOrigin(mainUrl, origin)
        && (needsFocus == false || SameOrigin(focusedUrl, origin));

    // The browser's frames as one string, so a frame added under a click point between hit test and click refuses it.
    public static string FrameSignature(CefBrowser browser) => string.Join(",", browser.GetFrameIdentifiers().Order(StringComparer.Ordinal));

    // While a tab is controlled, any frame's form submission and any non-GET navigation waits for the operator, one to
    // another origin is dropped, and so is the top frame leaving the origin unless the host itself (the address bar)
    // sent it there. Sending a held one continues that very request, so nothing else rides along.
    public static Verdict Navigation(string? controlOrigin, string method, bool formSubmit, string url, bool mainFrame, bool fromHost)
    {
        if (controlOrigin is null)
        {
            return Verdict.Allow;
        }

        var sameOrigin = SameOrigin(url, controlOrigin);
        if (mainFrame && fromHost == false && sameOrigin == false)
        {
            return Verdict.Drop;
        }

        if (formSubmit == false && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return Verdict.Allow;
        }

        return sameOrigin ? Verdict.Hold : Verdict.Drop;
    }

    // A click lands only on an element of the tab's own top document: never in a frame, never on a frame's owner.
    public static bool ClickTarget(string? nodeForLocation, string? frameTree, string? describedNode)
    {
        try
        {
            using var node = JsonDocument.Parse(nodeForLocation ?? string.Empty);
            using var tree = JsonDocument.Parse(frameTree ?? string.Empty);
            using var described = JsonDocument.Parse(describedNode ?? string.Empty);
            var mainFrame = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            var frame = node.RootElement.TryGetProperty("frameId", out var id) ? id.GetString() : null;
            var name = described.RootElement.GetProperty("node").GetProperty("nodeName").GetString();
            return mainFrame is not null && frame == mainFrame && name is not null && FrameOwners.Contains(name) == false;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool SameOrigin(string? address, string origin) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.Equals(OriginOf(address), origin, StringComparison.OrdinalIgnoreCase);

    // A tab's origin as a grant compares it: scheme, host and port, so a path change on the same site never pauses.
    public static string OriginOf(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri && uri.Host.Length > 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : address;
}
