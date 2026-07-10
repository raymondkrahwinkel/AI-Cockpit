namespace Cockpit.Core.Mcp;

/// <summary>How a (remote/HTTP) MCP server authenticates the cockpit (#26 tools/MCP).</summary>
public enum McpServerAuth
{
    /// <summary>No authentication — a local stdio server, or an open HTTP server.</summary>
    None,

    /// <summary>A static bearer token / API key sent in the <c>Authorization</c> header.</summary>
    ApiKey,

    /// <summary>An OAuth 2.1 authorization-code flow (like the Depot project's), for servers that require a login.</summary>
    OAuth,
}
