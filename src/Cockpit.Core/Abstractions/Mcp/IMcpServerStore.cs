using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Persists the shared registry of user-configured MCP servers (#26) in the <c>mcpServers</c> section of
/// <c>cockpit.json</c>. One registry feeds every session — the local-LLM tool-loop and the Claude CLI's
/// own MCP config both read from it.
/// </summary>
public interface IMcpServerStore
{
    Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default);
}
