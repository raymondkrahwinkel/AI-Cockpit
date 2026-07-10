namespace Cockpit.Core.Mcp;

/// <summary>
/// A user-configured MCP server the cockpit can expose to sessions as tools (#26). One shared registry
/// fans out to both worlds: the local-LLM driver hosts these servers itself (the agentic tool-loop), and
/// the Claude CLI receives them through its own <c>--mcp-config</c>. Matches the standard
/// <c>{ "mcpServers": { name: { command/args | url/headers } } }</c> shape so that fan-out is a direct map.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Unique display name / key for the server.</summary>
    public required string Name { get; init; }

    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>Which session worlds this server fans out to. Defaults to <see cref="McpServerScope.All"/> so an unscoped server behaves as before.</summary>
    public McpServerScope Scope { get; init; } = McpServerScope.All;

    /// <summary>Executable for a stdio server (e.g. <c>npx</c>, <c>uvx</c>, a path).</summary>
    public string? Command { get; init; }

    /// <summary>Arguments for the stdio command.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Endpoint URL for an HTTP server.</summary>
    public string? Url { get; init; }

    public McpServerAuth Auth { get; init; } = McpServerAuth.None;

    /// <summary>Static bearer token when <see cref="Auth"/> is <see cref="McpServerAuth.ApiKey"/>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>OAuth authorization-server/discovery base when <see cref="Auth"/> is <see cref="McpServerAuth.OAuth"/>.</summary>
    public string? OAuthAuthority { get; init; }

    /// <summary>OAuth client id when <see cref="Auth"/> is <see cref="McpServerAuth.OAuth"/>.</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>Whether this server is active — a disabled server is kept in the registry but not connected.</summary>
    public bool Enabled { get; init; } = true;
}
