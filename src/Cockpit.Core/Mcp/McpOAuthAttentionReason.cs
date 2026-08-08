namespace Cockpit.Core.Mcp;

// Why the cockpit cannot present a credential to an OAuth-protected MCP server (AC-524). Three cases rather than
// one, because they ask different things of the operator: a server that cannot be reached is not fixed by signing
// in again, and telling someone their sign-in expired when they never made one sends them looking for something
// that was never there.
public enum McpOAuthAttentionReason
{
    // Nothing is wrong — no attention is being asked for. The value a successful acquire carries.
    None,

    // No sign-in was ever made for this server (or the stored one belongs to a different address).
    NeverSignedIn,

    // A sign-in exists but can no longer be renewed: the authorization server refused the refresh grant itself
    // (`invalid_grant`), or there is no refresh grant behind the stored token to renew from. Never concluded from a
    // renewal that merely failed (AC-646) — that is the reason below.
    SignInExpired,

    // The renewal never got an answer — the server or its authorization server could not be reached.
    ServerUnreachable,

    // The renewal worked and produced a token that expires sooner than the use it was asked for needs. Its own case
    // because the two obvious pieces of advice are both wrong here: nothing is expired, and signing in again yields
    // another token exactly like this one. What the operator can act on is the endpoint that would have made the
    // lifetime irrelevant, not the sign-in.
    TokenTooShortLived,

    // A call could not be relayed because its body was too large to hold on to, and the credential it went out with
    // was refused — so the retry that would have fixed it could not repeat the request.
    CallCouldNotBeRepeated,

    // The server refused a credential that had just been renewed for it (AC-550). Deliberately not
    // `SignInExpired`: a grant revoked at the far end and a server that refused one live token look
    // identical from here, and the renewal that came before this is evidence against the sign-in being the problem.
    // Where the two cannot be told apart, what is reported is that it could not be confirmed — the same rule
    // `McpProbeOutcome.Failed` keeps.
    RenewedCredentialRefused,

    // A silent renewal failed without the authorization server ever saying the grant was dead (AC-646) — the token
    // endpoint answered with something other than `invalid_grant`, or the handshake ended in a way that yielded no
    // credential and no verdict. Deliberately not `SignInExpired`: concluding "revoked" from the absence of success
    // is what made one bad second read as an account to re-authorize, and stopped an agent that had nothing to wait
    // for. The same rule `RenewedCredentialRefused` and `McpProbeOutcome.Failed` keep — where it cannot be told, what
    // is reported is that it could not be confirmed.
    RenewalCouldNotBeConfirmed,
}
