namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// What a plugin hands the host via <see cref="ICockpitHost.AddMcpServer"/> (#60) to register an HTTP MCP
/// server into the shared registry — e.g. a JetBrains YouTrack remote MCP endpoint. A plugin-friendly DTO:
/// HTTP transport + an optional static bearer token only (the shape every currently-known remote MCP server
/// needs), no <c>Cockpit.Core</c> types in the signature so the plugin-ALC isolation stays intact (see the
/// isolation note on <see cref="ICockpitHost"/>).
/// </summary>
/// <param name="Name">
/// Unique display name / registry key, e.g. <c>"YouTrack: Prod"</c>. Drives the idempotent upsert-by-name the
/// host performs — calling this again with the same <paramref name="Name"/> refreshes the existing entry's
/// URL/token instead of adding a duplicate.
/// </param>
/// <param name="Url">The server's HTTP endpoint, e.g. <c>https://x.youtrack.cloud/mcp</c>.</param>
/// <param name="BearerToken">Static bearer token sent as <c>Authorization: Bearer …</c>, or <see langword="null"/>/empty for no auth.</param>
/// <param name="Scope">Which session worlds this server fans out to on first registration. Defaults to <see cref="McpContributionScope.All"/>.</param>
public sealed record McpServerContribution(
    string Name,
    string Url,
    string? BearerToken = null,
    McpContributionScope Scope = McpContributionScope.All)
{
    // AC-500: init-only properties rather than more positional-record parameters — a plugin prebuilt against an
    // older Cockpit.Plugins.Abstractions.dll still calls this record's original 4-parameter constructor by its
    // exact IL signature; widening the primary constructor itself would throw MissingMethodException the moment
    // such a plugin ran against a host carrying the newer assembly, even though AbstractionsContract.Version never
    // changed. This is the record equivalent of a default interface method: additive without touching the ctor.

    /// <summary>
    /// Set this instead of <see cref="BearerToken"/> when the server requires an OAuth 2.1 sign-in rather than a
    /// static token — its authorization-server/discovery base, e.g. <c>https://login.example.com</c>. A non-empty
    /// value is what tells the host to treat the contribution as OAuth: the cockpit then drives the same
    /// loopback-browser sign-in flow a registry-configured OAuth server gets, storing and refreshing the resulting
    /// token itself rather than the plugin ever seeing a bearer value.
    /// </summary>
    public string? OAuthAuthority { get; init; }

    /// <summary>
    /// OAuth client id for <see cref="OAuthAuthority"/>, or <see langword="null"/> to let the server register the
    /// cockpit dynamically (RFC 7591) on first sign-in. Ignored when <see cref="OAuthAuthority"/> is empty.
    /// </summary>
    public string? OAuthClientId { get; init; }
}
