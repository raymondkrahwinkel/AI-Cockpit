namespace Cockpit.Core.Mcp;

// AC-2220: one query parameter a memory source hangs on the address of one of its own MCP servers for one session.
public sealed record McpServerQueryParameter(string ServerName, string Name, string Value)
{
    public McpServerConfig ApplyTo(McpServerConfig server) =>
        !string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(server.Url, UriKind.Absolute, out var url)
            ? server
            : server with { Url = new UriBuilder(url) { Query = string.Join('&', new[] { url.Query.TrimStart('?'), $"{Uri.EscapeDataString(Name)}={Uri.EscapeDataString(Value)}" }.Where(part => part.Length > 0)) }.Uri.AbsoluteUri };
}
