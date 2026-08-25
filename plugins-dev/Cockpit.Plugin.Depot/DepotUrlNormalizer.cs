namespace Cockpit.Plugin.Depot;

// Strips one trailing `/mcp` an operator may paste (AC-499) — `DepotPlugin` appends `/mcp` itself, so
// leaving it in doubled to `…/mcp/mcp`, a 404 with no `WWW-Authenticate` that broke OAuth discovery.
// Must run exactly once per stored value: repeat calls would strip a legitimate trailing `/mcp`.
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

    // The scheme+host+port of an already-`Normalize`d URL. Depot's protected-resource metadata names the
    // origin, not a path, as its `authorization_servers` entry. Null when unparseable, so a caller can
    // fall back to the normalized URL itself rather than losing the field entirely.
    public static string? Origin(string normalizedUrl) =>
        Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
}
