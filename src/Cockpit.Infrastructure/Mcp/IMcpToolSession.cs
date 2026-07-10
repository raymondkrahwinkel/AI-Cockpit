using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// A live connection to the MCP servers of the shared registry (#26): the tools they exposed and the
/// names of the servers that connected. Disposing it closes every underlying MCP client (and, for stdio
/// servers, ends their processes).
/// </summary>
internal interface IMcpToolSession : IAsyncDisposable
{
    IReadOnlyList<AIFunction> Tools { get; }

    IReadOnlyList<string> ConnectedServerNames { get; }
}
