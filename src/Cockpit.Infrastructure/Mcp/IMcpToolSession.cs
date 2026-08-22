using Cockpit.Core.Mcp;
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
    /// — a named outcome distinct from a plain unreachable/misconfigured server (only an absence from
    /// <see cref="ConnectedServerNames"/> plus a log line). Lets a caller tell "no tools" from "waiting on sign-in".
    /// </summary>
    IReadOnlyList<string> ServersNeedingSignIn { get; }

    /// <summary>
    /// Every enabled server that ended this session with no tools (AC-997): the same servers behind
    /// <see cref="ServersNeedingSignIn"/> plus every other connect failure, each with a one-line reason.
    /// </summary>
    IReadOnlyList<McpServerConnectionIssue> ConnectionIssues { get; }

    /// <summary>
    /// Each connected tool's permission class (AC-79), keyed by tool name, derived from its MCP read-only/
    /// destructive annotations at connect. Feeds the delegated non-interactive gate: a tool absent from the map
    /// is treated as <see cref="ToolPermissionClass.Unknown"/>.
    /// </summary>
    IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses { get; }

    /// <summary>
    /// The pane token this session minted (AC-89), or <c>null</c> when it connected without a pane id. Read by a
    /// caller that needs the same live token elsewhere (AC-994), so it never mints a second one for the same pane.
    /// </summary>
    string? PaneToken { get; }
}

// One tool of a connected session, with the server it came from and whether that server is always mounted
// (AC-963). The origin travels with the tool rather than in a name-keyed side map because two servers may expose
// the same tool name, and the search layer has to be able to tell the operator which of the two it found.
internal sealed record McpSessionTool(AIFunction Function, string ServerName, bool AlwaysMounted);
