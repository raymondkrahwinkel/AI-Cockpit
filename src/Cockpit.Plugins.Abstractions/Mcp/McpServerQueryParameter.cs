namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// A query parameter the host adds to the address of one of the plugin's own MCP servers for one session (AC-2220),
/// behind the session gateway, so the agent never sees it.
/// </summary>
/// <param name="ServerName">The name of the plugin's own MCP server whose address gets the parameter.</param>
/// <param name="Name">The parameter name.</param>
/// <param name="Value">The parameter value, unescaped; the host escapes it.</param>
public sealed record McpServerQueryParameter(string ServerName, string Name, string Value);
