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
    IReadOnlyList<McpSessionTool> Tools { get; }

    IReadOnlyList<string> ConnectedServerNames { get; }

    /// <summary>
    /// Enabled servers whose connect failed while <see cref="Cockpit.Core.Mcp.McpServerAuth.OAuth"/> was set (AC-500)
    /// — a named outcome distinct from a plain unreachable/misconfigured server, which only ever shows up as an
    /// absence from <see cref="ConnectedServerNames"/> and a log line. This is what a caller can read to tell the two
    /// apart without inspecting the log: "no tools from this server" versus "this server is waiting on a sign-in".
    /// </summary>
    IReadOnlyList<string> ServersNeedingSignIn { get; }

    /// <summary>
    /// Each connected tool's permission class (AC-79), keyed by tool name, derived from its MCP read-only/
    /// destructive annotations at connect. Feeds the delegated non-interactive gate: a tool absent from the map
    /// is treated as <see cref="ToolPermissionClass.Unknown"/>.
    /// </summary>
    IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses { get; }
}

// One tool of a connected session, with the server it came from and whether that server is always mounted
// (AC-963). The origin travels with the tool rather than in a name-keyed side map because two servers may expose
// the same tool name, and the search layer has to be able to tell the operator which of the two it found.
internal sealed record McpSessionTool(AIFunction Function, string ServerName, bool AlwaysMounted);
