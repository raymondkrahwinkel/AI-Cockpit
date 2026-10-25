using System.Text.Json;

namespace Cockpit.Plugin.Browser.UI.Share;

// AC-1728: the decisions control rests on, with nothing the page's own scripts can set. MayAct runs on CEF's UI thread
// right before the input goes in; Navigation runs in CEF's resource handler for every frame; ClickTarget reads what
// DevTools (not the page) says lies under the point.
internal static class ControlPolicy
{
    public enum Verdict
    {
        Allow,
        Hold,
        Drop,
    }

    private static readonly HashSet<string> FrameOwners = new(StringComparer.OrdinalIgnoreCase) { "IFRAME", "FRAME", "OBJECT", "EMBED", "PORTAL", "FENCEDFRAME" };

    // The very grant that authorized the action still stands, the tab is still on its origin, and for keys the focused
    // frame is on that origin too, so typing never lands in another site's iframe.
    public static bool MayAct(TabGrantRegistry grants, string paneId, string tabId, TabGrant grant, string? mainUrl, string? focusedUrl, bool needsFocus) =>
        ReferenceEquals(grants.Authorize(paneId, tabId, control: true), grant)
        && SameOrigin(mainUrl, grant.Origin)
        && (needsFocus == false || SameOrigin(focusedUrl, grant.Origin));

    // While a tab is controlled, any frame's form submission and any non-GET navigation waits for the operator, and one
    // to another origin is dropped. Sending a held one continues that very request, so nothing else rides along.
    public static Verdict Navigation(string? controlOrigin, string method, bool formSubmit, string url)
    {
        if (controlOrigin is null || formSubmit == false)
        {
            return Verdict.Allow;
        }

        return SameOrigin(url, controlOrigin) ? Verdict.Hold : Verdict.Drop;
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
        && string.Equals(TabGrantRegistry.OriginOf(address), origin, StringComparison.OrdinalIgnoreCase);
}
