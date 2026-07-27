namespace Cockpit.Core.Mcp;

/// <summary>
/// What the cockpit knows about its standing with one MCP server before a session touches it (AC-353). It exists so
/// that "you are not signed in" is something the operator is told up front, rather than a 401 surfacing from the
/// depths at the first tool call.
/// </summary>
public enum McpAuthState
{
    /// <summary>The server asks for nothing, or carries a static key the operator already supplied.</summary>
    NotRequired,

    /// <summary>A usable access token is held for this server.</summary>
    Authorized,

    /// <summary>The server needs an OAuth sign-in that has not happened, or whose token can no longer be renewed.</summary>
    AuthorizationRequired,
}
