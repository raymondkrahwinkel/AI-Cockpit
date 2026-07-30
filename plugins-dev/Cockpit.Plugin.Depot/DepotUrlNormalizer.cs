namespace Cockpit.Plugin.Depot;

/// <summary>
/// Cleans up the Depot instance URL an operator enters (AC-499): trims whitespace, drops trailing slashes, and
/// strips one trailing <c>/mcp</c> segment. Depot's own documentation tells the operator to paste the full
/// endpoint (<c>https://host/mcp</c>), but <see cref="DepotPlugin"/> appends <c>/mcp</c> itself when it builds the
/// MCP contribution — pasting the documented URL unchanged used to double it into <c>…/mcp/mcp</c>, which answered
/// with a 404 carrying no <c>WWW-Authenticate</c> header, so OAuth discovery never even started (the bug this
/// ticket exists to close).
/// <para>
/// <b>The guaranteed property is a round trip, not idempotency:</b> for every endpoint URL an operator actually
/// pastes, <c>Normalize(endpointUrl) + "/mcp" == endpointUrl</c>. That holds for a root deployment
/// (<c>https://host/mcp</c> → <c>https://host</c>, and back), a subpath deployment (<c>https://host/depot/mcp</c>
/// → <c>https://host/depot</c>, and back) — and, the case a "strip every trailing /mcp" loop gets wrong, a
/// deployment whose own base path ends in <c>/mcp</c> (<c>https://host/mcp/mcp</c> → <c>https://host/mcp</c>, and
/// back to <c>https://host/mcp/mcp</c>, not the origin). A loop that strips repeatedly would reduce that last case
/// all the way to <c>https://host</c>, and the plugin's own <c>+"/mcp"</c> would then dial the wrong endpoint for
/// that operator.
/// </para>
/// <para>
/// <b>Normalize must therefore run exactly once per stored value</b> — unlike the loop version this replaced, it is
/// not safe to call this again on output it already produced: a base that legitimately ends in <c>/mcp</c> would
/// lose that segment on a second pass. Only where operator input actually enters (a row's own
/// <c>ToRegistration()</c>, and the live-textbox comparison in <c>_IsUnderStoredIdentity</c>) calls this; a
/// pre-fix stored value is cleaned up exactly once by <c>DepotSettings</c>, not by every call site that reads it.
/// </para>
/// <para>
/// A literal trailing-substring transform, not a URI parse: it only ever looks at the end of the string, so a value
/// with no scheme, an unparsable value, or plain non-URL text all pass through unharmed (merely trimmed) rather
/// than throwing. A trailing query string or fragment (<c>?token=…</c>, <c>#section</c>) is deliberately left
/// untouched: it stops the literal <c>/mcp</c> suffix from matching, so the value is returned as-is rather than
/// this class guessing where the "real" URL ends inside it — Depot's own documented URL never carries either.
/// </para>
/// </summary>
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

    /// <summary>
    /// The scheme+host+port of an already-<see cref="Normalize"/>d URL. Depot's protected-resource metadata names
    /// the origin (e.g. <c>https://depot.krahwinkel-it.nl/</c>), not a path, as its <c>authorization_servers</c>
    /// entry — a defined operation (drop the path, keep scheme/host/port), not a guess about where Depot's issuer
    /// actually lives. <see langword="null"/> when <paramref name="normalizedUrl"/> is not a parseable absolute
    /// URL, so a caller can fall back to the normalized URL itself rather than losing the field entirely.
    /// </summary>
    public static string? Origin(string normalizedUrl) =>
        Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
}
