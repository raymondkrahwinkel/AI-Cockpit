namespace Cockpit.Plugin.Depot.Model;

// One registered Depot instance (AC-243), kept in `Settings.DepotSettings` as plain (non-secret) `IPluginStorage`
// metadata — Depot's OAuth 2.1+PKCE auth path means the plugin never sees a bearer token or client secret.
// `Id` stays independent of `Name` so a rename doesn't orphan the contributed MCP server.
public sealed record DepotConnectionRegistration(string Id, string Name, string Url)
{
    // The name this connection is contributed under (AC-243) — a fixed `"Depot: "` prefix so it can never
    // silently collide with a hand-configured server of the same name. Since AC-504 a same-named registry
    // entry is shadowed by `McpServerCatalog.Merge` and reclaimed by `DepotPlugin.Initialize`'s startup cleanup.
    public string McpServerName => $"Depot: {Name}";
}
