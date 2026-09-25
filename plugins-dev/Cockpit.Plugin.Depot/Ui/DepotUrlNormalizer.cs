namespace Cockpit.Plugin.Depot.UI;

// Strips one trailing `/mcp` an operator may paste (AC-499) — the backend part appends `/mcp` itself, so
// leaving it in doubled to `…/mcp/mcp`, a 404 with no `WWW-Authenticate` that broke OAuth discovery.
// Must run exactly once per stored value: repeat calls would strip a legitimate trailing `/mcp`.
//
// The UI part's own copy of the backend part's identically-named class (AC-1394) — see Contracts/DepotChannel.cs's
// own remarks for why this is a separate copy rather than a linked one.
internal static class DepotUrlNormalizer
{
    private const string McpSuffix = "/mcp";

    public static string Normalize(string? url)
    {
        var normalized = (url ?? string.Empty).Trim().TrimEnd('/');
        return normalized.EndsWith(McpSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^McpSuffix.Length].TrimEnd('/')
            : normalized;
    }
}
