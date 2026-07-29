namespace Cockpit.Plugin.Depot.Model;

/// <summary>
/// One registered Depot instance (AC-243): a name and its base URL, kept in <see cref="Settings.DepotSettings"/>.
/// Nothing here is secret — Depot has a single auth path (OpenIddict OAuth 2.1 + PKCE) and the plugin never sees a
/// bearer token or client secret; the host's own <see cref="Cockpit.Plugins.Abstractions.Mcp.McpServerContribution"/>
/// carries only the authority, and the resulting credential lives entirely in the host's OAuth store. There is
/// nothing for this record to protect, so it is plain (non-secret) <c>IPluginStorage</c> metadata like
/// <c>ClusterRegistration</c>'s label/context fields.
/// </summary>
/// <param name="Id">Stable id, independent of <see cref="Name"/> so a rename does not orphan the contributed MCP server registered under the old name.</param>
/// <param name="Name">Operator-chosen label for this connection, shown in the settings row and used to derive the contributed MCP server's display name.</param>
/// <param name="Url">The Depot instance's base URL, e.g. <c>https://depot.example.com</c> — no trailing slash, no <c>/mcp</c> suffix (the plugin appends that when contributing the server).</param>
public sealed record DepotConnectionRegistration(string Id, string Name, string Url)
{
    /// <summary>
    /// The name this connection is contributed under (AC-243) — a fixed <c>"Depot: "</c> prefix so a Depot
    /// connection managed here can never silently collide with (and overwrite, since <c>AddMcpServer</c> is an
    /// upsert-by-name) an unrelated server an operator configured by hand in the MCP-servers dialog.
    /// </summary>
    public string McpServerName => $"Depot: {Name}";
}
