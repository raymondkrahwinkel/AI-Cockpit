using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Owns the cockpit's standing with the OAuth-protected MCP servers (AC-353): it answers what credential a session
/// may present, and renews one that has gone stale.
/// <para>
/// The distinction that matters is <em>interactive</em>. Starting a session must never open a browser the operator
/// did not ask for, so it asks non-interactively and is told <see cref="McpAuthState.AuthorizationRequired"/> when a
/// renewal is not possible without a person. Signing in is the operator's own act, and only that asks interactively.
/// </para>
/// </summary>
public interface IMcpOAuthCoordinator
{
    /// <summary>
    /// The credential to present to <paramref name="server"/>. A server that is not OAuth-protected answers
    /// <see cref="McpOAuthAccess.NotRequired"/> without any work. When <paramref name="interactive"/> is
    /// <see langword="false"/> this never opens a browser: a token that cannot be renewed silently comes back as
    /// <see cref="McpAuthState.AuthorizationRequired"/> instead.
    /// </summary>
    Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default);

    /// <summary>
    /// The credential to bake into a session's config (AC-524). Never interactive, and it keeps a far wider margin
    /// than <see cref="AcquireAsync"/> does: that one answers a single request, this one is read once at session
    /// start and then held for as long as the session lives, so a token with minutes on the clock is a session whose
    /// tools disappear minutes in.
    /// <para>
    /// Separate entry point rather than a flag on <see cref="AcquireAsync"/> on purpose: the TTY spawn path passes
    /// its cancellation token positionally, so a parameter slid in before it is a trap that compiles.
    /// </para>
    /// </summary>
    Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that <paramref name="rejectedAccessToken"/> was turned away by the server itself, and answers with
    /// whatever replaces it (AC-524).
    /// <para>
    /// This exists because the cockpit decides on its own clock whether a token is still good, and the server is the
    /// only one who actually knows. A grant revoked at the far end, or a rotation race lost to another session,
    /// leaves a token that looks healthy here for another forty minutes and is dead everywhere that matters — and
    /// without this every later call would present the same dead token and get the same refusal.
    /// </para>
    /// <para>
    /// Renewing is at most one round trip no matter how many callers report the same refusal at once: a caller whose
    /// rejected token is no longer the stored one is simply given the current one, and the rest coalesce onto the one
    /// renewal.
    /// </para>
    /// </summary>
    Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where the cockpit stands with <paramref name="server"/>, for showing rather than for using (AC-355). Reads
    /// what is stored and nothing else: no network, no browser, no renewal. That restraint is the point — a status
    /// is drawn for every server in a list, and a status that connected somewhere would make opening a dialog an
    /// event on someone else's server.
    /// </summary>
    Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what the cockpit holds for <paramref name="server"/>, so the next use asks for a sign-in again.
    /// One place to withdraw from, which is the reason the token lives in one place to begin with.
    /// </summary>
    Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default);
}
