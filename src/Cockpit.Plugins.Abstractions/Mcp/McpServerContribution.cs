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
/// <param name="OAuthAuthority">
/// Set this (AC-500) instead of <paramref name="BearerToken"/> when the server requires an OAuth 2.1 sign-in
/// rather than a static token — its authorization-server/discovery base, e.g. <c>https://login.example.com</c>.
/// A non-empty value is what tells the host to treat the contribution as OAuth: the cockpit then drives the
/// same loopback-browser sign-in flow a registry-configured OAuth server gets, storing and refreshing the
/// resulting token itself rather than the plugin ever seeing a bearer value.
/// </param>
/// <param name="OAuthClientId">
/// OAuth client id for <paramref name="OAuthAuthority"/>, or <see langword="null"/> to let the server register
/// the cockpit dynamically (RFC 7591) on first sign-in. Ignored when <paramref name="OAuthAuthority"/> is empty.
/// </param>
public sealed record McpServerContribution(
    string Name,
    string Url,
    string? BearerToken = null,
    McpContributionScope Scope = McpContributionScope.All,
    string? OAuthAuthority = null,
    string? OAuthClientId = null);
