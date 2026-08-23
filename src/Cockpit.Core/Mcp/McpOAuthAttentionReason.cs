namespace Cockpit.Core.Mcp;

// AC-524: why the cockpit cannot present a credential to an OAuth-protected MCP server. Distinct cases because
// they ask different things of the operator — e.g. an unreachable server is not fixed by signing in again.
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

    // The renewal worked but the token expires sooner than the use it was asked for needs. Nothing is expired and
    // signing in again yields the same short-lived token — what the operator can act on is the endpoint that would
    // have made the lifetime irrelevant, not the sign-in.
    TokenTooShortLived,

    // A call could not be relayed because its body was too large to hold on to, and the credential it went out with
    // was refused — so the retry that would have fixed it could not repeat the request.
    CallCouldNotBeRepeated,

    // AC-550: server refused a credential just renewed for it. Deliberately not `SignInExpired` — a revoked grant
    // and a server refusing one live token look identical here, so it is reported as unconfirmed instead.
    RenewedCredentialRefused,

    // AC-646: a silent renewal failed without the authorization server ever saying the grant was dead. Deliberately
    // not `SignInExpired` — concluding "revoked" from the absence of success wrongly sent one bad
    // read to re-authorization, so this is reported as unconfirmed instead.
    RenewalCouldNotBeConfirmed,
}
