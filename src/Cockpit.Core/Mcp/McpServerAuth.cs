namespace Cockpit.Core.Mcp;

// How a (remote/HTTP) MCP server authenticates the cockpit (#26 tools/MCP).
public enum McpServerAuth
{
    // No authentication — a local stdio server, or an open HTTP server.
    None,

    // A static bearer token / API key sent in the `Authorization` header.
    ApiKey,

    // An OAuth 2.1 authorization-code flow (like the Depot project's), for servers that require a login.
    OAuth,
}
