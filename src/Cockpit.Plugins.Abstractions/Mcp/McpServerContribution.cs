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
/// <param name="Url">
/// The server's HTTP endpoint, e.g. <c>https://x.youtrack.cloud/mcp</c>.
/// </param>
/// <param name="BearerToken">
/// Static bearer token sent as <c>Authorization: Bearer …</c>, or <see langword="null"/>/empty for no auth.
/// </param>
/// <param name="Scope">
/// Which session worlds this server fans out to on first registration. Defaults to <see cref="McpContributionScope.All"/>.
/// </param>
public sealed record McpServerContribution(
    string Name,
    string Url,
    string? BearerToken = null,
    McpContributionScope Scope = McpContributionScope.All)
{
    // AC-500: init-only properties rather than widening the primary constructor — a plugin prebuilt against an
    // older assembly still calls the original 4-parameter ctor by its exact IL signature; widening it would throw
    // MissingMethodException. This is additive without touching the ctor, like a default interface method.

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

    /// <summary>
    /// A stable id for the thing this contribution stands for (AC-403) — an id of the plugin's own, unchanged
    /// when the operator edits whatever it derives <see cref="Name"/> from. Set it and the cockpit files this
    /// server's OAuth token under it instead of under the name, so a rename keeps its sign-in.
    /// </summary>
    /// <remarks>
    /// Only meaningful for a plugin whose <see cref="Name"/> is derived from something renamable; a fixed-name
    /// plugin can leave this null. ⚠️ Must be stable and the plugin's own — a GUID minted once per connection, not
    /// derived from the name, and never reused across two connections. Setting it requires
    /// <c>minHostVersion</c> 0.16.0.
    /// </remarks>
    public string? Id { get; init; }
}
