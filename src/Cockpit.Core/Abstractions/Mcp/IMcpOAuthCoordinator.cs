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
    /// Raised once when a server goes from usable to needing the operator's sign-in (never signed in, or no longer
    /// renewable), so the cockpit can name it and offer Sign in instead of a browser tab opening by itself (AC-1517).
    /// </summary>
    event Action<McpServerConfig>? SignInNeeded;

    /// <summary>
    /// Raised when the account stored for a server may have changed (AC-1665): an interactive sign-in left a new
    /// token behind, or the server was signed out. Not raised for a silent renewal, nor when a sign-in was abandoned
    /// and the previous token put back — whatever a plugin cached for the old account is only wrong after this.
    /// </summary>
    event Action<McpServerConfig>? AccountChanged;

    /// <summary>
    /// The credential to present to <paramref name="server"/>. A non-OAuth server answers
    /// <see cref="McpOAuthAccess.NotRequired"/> with no work. When <paramref name="interactive"/> is false this never
    /// opens a browser: an unrenewable token comes back as <see cref="McpAuthState.AuthorizationRequired"/> instead.
    /// </summary>
    Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Reports that <paramref name="server"/> answered <paramref name="accessToken"/> with 403 and
    /// <c>error="insufficient_scope"</c>. A renewal cannot add scope, so the server needs a sign-in from then on,
    /// exactly as if it had no usable token, until a sign-in grants a different scope.
    /// </summary>
    Task ReportInsufficientScopeAsync(McpServerConfig server, string accessToken, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
