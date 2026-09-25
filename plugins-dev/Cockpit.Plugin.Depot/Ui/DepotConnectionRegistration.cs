namespace Cockpit.Plugin.Depot.UI;

// One registered Depot instance (AC-243), kept in `UI.DepotSettings` as plain (non-secret) `IPluginStorage`
// metadata — Depot's OAuth 2.1+PKCE auth path means the plugin never sees a bearer token or client secret.
// `Id` stays independent of `Name` so a rename doesn't orphan the contributed MCP server.
//
// The UI part's own copy of the backend part's identically-named record under Model/ (AC-1394) — see
// Contracts/DepotChannel.cs's own remarks for why this is a separate copy rather than a linked one.
internal sealed record DepotConnectionRegistration(string Id, string Name, string Url)
{
    // The name this connection is contributed under (AC-243) — a fixed `"Depot: "` prefix so it can never
    // silently collide with a hand-configured server of the same name.
    public string McpServerName => $"Depot: {Name}";
}
