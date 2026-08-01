namespace Cockpit.Core.Mcp;

/// <summary>
/// Why the cockpit cannot present a credential to an OAuth-protected MCP server (AC-524). Three cases rather than
/// one, because they ask different things of the operator: a server that cannot be reached is not fixed by signing
/// in again, and telling someone their sign-in expired when they never made one sends them looking for something
/// that was never there.
/// </summary>
public enum McpOAuthAttentionReason
{
    /// <summary>Nothing is wrong — no attention is being asked for. The value a successful acquire carries.</summary>
    None,

    /// <summary>No sign-in was ever made for this server (or the stored one belongs to a different address).</summary>
    NeverSignedIn,

    /// <summary>A sign-in exists but can no longer be renewed: the refresh grant was refused, revoked or has run out.</summary>
    SignInExpired,

    /// <summary>The renewal never got an answer — the server or its authorization server could not be reached.</summary>
    ServerUnreachable,

    /// <summary>
    /// The renewal worked and produced a token that expires sooner than the use it was asked for needs. Its own case
    /// because the two obvious pieces of advice are both wrong here: nothing is expired, and signing in again yields
    /// another token exactly like this one. What the operator can act on is the endpoint that would have made the
    /// lifetime irrelevant, not the sign-in.
    /// </summary>
    TokenTooShortLived,

    /// <summary>
    /// A call could not be relayed because its body was too large to hold on to, and the credential it went out with
    /// was refused — so the retry that would have fixed it could not repeat the request.
    /// </summary>
    CallCouldNotBeRepeated,

    /// <summary>
    /// The server refused a credential that had just been renewed for it (AC-550). Deliberately not
    /// <see cref="SignInExpired"/>: a grant revoked at the far end and a server that refused one live token look
    /// identical from here, and the renewal that came before this is evidence against the sign-in being the problem.
    /// Where the two cannot be told apart, what is reported is that it could not be confirmed — the same rule
    /// <c>McpProbeOutcome.Failed</c> keeps.
    /// </summary>
    RenewedCredentialRefused,
}
