namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Runtime coordinates of the cockpit's shared MCP permission server, published once it has
/// bound a port and written its <c>--mcp-config</c> file. Sessions read this at spawn time to
/// append the permission flags. Null before the server has started.
/// </summary>
public interface IPermissionServerState
{
    /// <summary>Absolute path to the generated <c>--mcp-config</c> file, or null before the server is ready.</summary>
    string? McpConfigPath { get; }

    /// <summary>The <c>--permission-prompt-tool</c> argument (e.g. <c>mcp__cockpit__permission_prompt</c>), or null before ready.</summary>
    string? PermissionPromptToolName { get; }

    /// <summary>The permission server's loopback MCP endpoint (e.g. <c>http://127.0.0.1:&lt;port&gt;/mcp</c>), or null before ready. Used to rebuild the merged <c>--mcp-config</c> with the shared registry at each spawn.</summary>
    string? PermissionMcpUrl { get; }
}
