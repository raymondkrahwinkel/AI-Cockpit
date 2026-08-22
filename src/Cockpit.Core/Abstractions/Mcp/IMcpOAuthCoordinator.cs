using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Owns the cockpit's standing with OAuth-protected MCP servers (AC-353): answers what credential a session may
/// present, and renews one gone stale. A session start never opens a browser unasked, so it asks non-interactively
/// and gets <see cref="McpAuthState.AuthorizationRequired"/> when renewal needs a person; only signing in asks interactively.
/// </summary>
public interface IMcpOAuthCoordinator
{
    /// <summary>
    /// The credential to present to <paramref name="server"/>. A non-OAuth server answers
    /// <see cref="McpOAuthAccess.NotRequired"/> with no work. When <paramref name="interactive"/> is false this never
    /// opens a browser: an unrenewable token comes back as <see cref="McpAuthState.AuthorizationRequired"/> instead.
    /// </summary>
    Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default);

    /// <summary>
    /// The credential to bake into a session's config (AC-524). Never interactive; unlike <see cref="AcquireAsync"/>
    /// (one request), this is held for the session's life, so minutes left on the token is a session whose tools
    /// disappear minutes in. Separate entry point, not a flag: the TTY spawn path passes its token positionally, so a parameter slid in before it is a trap that compiles.
    /// </summary>
    Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that <paramref name="rejectedAccessToken"/> was turned away by the server, and answers with whatever
    /// replaces it (AC-524) — only the server knows a token is dead, and without this every later call would repeat
    /// the refusal. At most one round trip per concurrent refusal: a caller whose token is no longer stored just gets the current one, the rest coalesce.
    /// </summary>
    Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the cockpit stands with <paramref name="server"/>, for showing rather than using (AC-355). Reads what
    /// is stored and nothing else — no network, no browser, no renewal — because a status is drawn for every server
    /// in a list, and one that connected somewhere would make opening a dialog an event on someone else's server.
    /// </summary>
    Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what the cockpit holds for <paramref name="server"/>, so the next use asks for a sign-in again.
    /// One place to withdraw from, which is the reason the token lives in one place to begin with.
    /// </summary>
    Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default);
}
