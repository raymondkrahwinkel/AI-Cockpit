using Cockpit.Core.Sessions.Permissions;
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

    /// <summary>
    /// Each connected tool's permission class (AC-79), keyed by tool name, derived from its MCP read-only/
    /// destructive annotations at connect. Feeds the delegated non-interactive gate: a tool absent from the map
    /// is treated as <see cref="ToolPermissionClass.Unknown"/>.
    /// </summary>
    IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses { get; }
}
