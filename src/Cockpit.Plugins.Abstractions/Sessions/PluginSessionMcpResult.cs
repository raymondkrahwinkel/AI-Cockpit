namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>The selected MCP servers and whether a connection report covers the entire selection.</summary>
public sealed record PluginSessionMcpResult(bool Ok, IReadOnlyList<PluginSessionMcpServer> Servers, bool Confirmed, string? Error);

/// <summary>A server name and its connection status category.</summary>
public sealed record PluginSessionMcpServer(string Name, string Status);

/// <summary>Whether a server is configured, authorized and allowed by the plugin's session policy.</summary>
public sealed record PluginSessionMcpServerCheck(string Name, bool Ok, string? Error);
