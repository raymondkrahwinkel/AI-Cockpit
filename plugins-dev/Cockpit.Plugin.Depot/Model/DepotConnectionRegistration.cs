namespace Cockpit.Plugin.Depot.Model;

// One registered Depot instance (AC-243): a name and its base URL, kept in `Settings.DepotSettings`.
// Nothing here is secret — Depot has a single auth path (OpenIddict OAuth 2.1 + PKCE) and the plugin never sees a
// bearer token or client secret; the host's own `Cockpit.Plugins.Abstractions.Mcp.McpServerContribution`
// carries only the authority, and the resulting credential lives entirely in the host's OAuth store. There is
// nothing for this record to protect, so it is plain (non-secret) `IPluginStorage` metadata like
// `ClusterRegistration`'s label/context fields.
//
// `Id`: Stable id, independent of `Name` so a rename does not orphan the contributed MCP server registered under the old name.
// `Name`: Operator-chosen label for this connection, shown in the settings row and used to derive the contributed MCP server's display name.
// `Url`:
// The Depot instance's base URL, e.g. `https://depot.example.com` — no trailing slash, no `/mcp` suffix
// (the plugin appends that when contributing the server). Not merely a convention: `Ui.DepotConnectionRowControl
// .ToRegistration` runs whatever the operator typed through `DepotUrlNormalizer.Normalize` before it lands
// here, since Depot's own docs tell the operator to paste the full endpoint including `/mcp`.
public sealed record DepotConnectionRegistration(string Id, string Name, string Url)
{
    // The name this connection is contributed under (AC-243) — a fixed `"Depot: "` prefix so a Depot
    // connection managed here can never silently collide with an unrelated server an operator configured by hand
    // in the MCP-servers dialog. Since AC-504 that collision would show up differently than a plain overwrite: a
    // hand-configured registry entry under the same name is shadowed for a session by `McpServerCatalog.Merge`
    // (this plugin's own live answer wins over a same-named registry row), and reclaimed outright — deleted, not
    // merely refreshed — by `DepotPlugin.Initialize`'s startup cleanup of whatever an older, push-based
    // install (pre-AC-504) left behind.
    public string McpServerName => $"Depot: {Name}";
}
