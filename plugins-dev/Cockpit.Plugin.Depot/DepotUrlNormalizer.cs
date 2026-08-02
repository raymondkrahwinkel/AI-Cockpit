namespace Cockpit.Plugin.Depot;

// Cleans up the Depot instance URL an operator enters (AC-499): trims whitespace, drops trailing slashes, and
// strips one trailing `/mcp` segment. Depot's own documentation tells the operator to paste the full
// endpoint (`https://host/mcp`), but `DepotPlugin` appends `/mcp` itself when it builds the
// MCP contribution — pasting the documented URL unchanged used to double it into `…/mcp/mcp`, which answered
// with a 404 carrying no `WWW-Authenticate` header, so OAuth discovery never even started (the bug this
// ticket exists to close).
//
// *The guaranteed property is a round trip, not idempotency:* for every endpoint URL an operator actually
// pastes, `Normalize(endpointUrl) + "/mcp" == endpointUrl`. That holds for a root deployment
// (`https://host/mcp` → `https://host`, and back), a subpath deployment (`https://host/depot/mcp`
// → `https://host/depot`, and back) — and, the case a "strip every trailing /mcp" loop gets wrong, a
// deployment whose own base path ends in `/mcp` (`https://host/mcp/mcp` → `https://host/mcp`, and
// back to `https://host/mcp/mcp`, not the origin). A loop that strips repeatedly would reduce that last case
// all the way to `https://host`, and the plugin's own `+"/mcp"` would then dial the wrong endpoint for
// that operator.
//
// *Normalize must therefore run exactly once per stored value* — unlike the loop version this replaced, it is
// not safe to call this again on output it already produced: a base that legitimately ends in `/mcp` would
// lose that segment on a second pass. Only where operator input actually enters (a row's own
// `ToRegistration()`, and the live-textbox comparison in `_IsUnderStoredIdentity`) calls this; a
// pre-fix stored value is cleaned up exactly once by `DepotSettings`, not by every call site that reads it.
//
// A literal trailing-substring transform, not a URI parse: it only ever looks at the end of the string, so a value
// with no scheme, an unparsable value, or plain non-URL text all pass through unharmed (merely trimmed) rather
// than throwing. A trailing query string or fragment (`?token=…`, `#section`) is deliberately left
// untouched: it stops the literal `/mcp` suffix from matching, so the value is returned as-is rather than
// this class guessing where the "real" URL ends inside it — Depot's own documented URL never carries either.
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

    // The scheme+host+port of an already-`Normalize`d URL. Depot's protected-resource metadata names
    // the origin (e.g. `https://depot.krahwinkel-it.nl/`), not a path, as its `authorization_servers`
    // entry — a defined operation (drop the path, keep scheme/host/port), not a guess about where Depot's issuer
    // actually lives. `null` when `normalizedUrl` is not a parseable absolute
    // URL, so a caller can fall back to the normalized URL itself rather than losing the field entirely.
    public static string? Origin(string normalizedUrl) =>
        Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
}
