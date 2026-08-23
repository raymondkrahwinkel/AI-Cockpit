namespace Cockpit.Core.Mcp;

// AC-524: the one sentence the operator is given when an OAuth-protected MCP server cannot be used — one place
// because three call sites (coordinator log, session-start line, tool-call error) would otherwise drift. Every
// reason gets its own action, replacing a blanket "sign in again" that was wrong for e.g. an unreachable server.
public static class McpOAuthSignInGuidance
{
    public static string For(string serverName, McpOAuthAttentionReason reason) => reason switch
    {
        McpOAuthAttentionReason.NeverSignedIn =>
            $"the cockpit has never signed in to '{serverName}'. Open Settings ▸ MCP servers and press Sign in on that row.",

        McpOAuthAttentionReason.SignInExpired =>
            $"the cockpit's sign-in for '{serverName}' can no longer be renewed — it was revoked or has run out. "
            + "Open Settings ▸ MCP servers and press Sign in on that row to authorize it again.",

        McpOAuthAttentionReason.ServerUnreachable =>
            $"'{serverName}' could not be reached, so the cockpit could not renew its sign-in. "
            + "Signing in again will not help while it is down; it is retried on its own once the server answers.",

        McpOAuthAttentionReason.TokenTooShortLived =>
            $"'{serverName}' hands out access tokens that expire sooner than a session lasts, and the cockpit could "
            + "not put its own endpoint in front of it this time — so it is left out rather than added and lost a few "
            + "minutes later. Restarting the cockpit usually restores that endpoint; signing in again will not, "
            + "because the next token is no longer-lived than this one.",

        McpOAuthAttentionReason.CallCouldNotBeRepeated =>
            $"'{serverName}' refused the credential this call went out with, and the call was too large to send "
            + "again — so the renewal that would have fixed it could not be applied to this one. The credential has "
            + "been renewed; sending the same request again should work.",

        McpOAuthAttentionReason.RenewedCredentialRefused =>
            $"'{serverName}' refused a credential the cockpit had renewed for it moments earlier, so whether its "
            + "sign-in is still good could not be confirmed. Sending the same request again is the first thing to "
            + "try; only if it keeps being refused is the sign-in itself worth renewing from Settings ▸ MCP servers.",

        McpOAuthAttentionReason.RenewalCouldNotBeConfirmed =>
            $"the cockpit could not renew its sign-in for '{serverName}' just now, and '{serverName}' did not say the "
            + "sign-in was the problem — so whether it is still good could not be confirmed. Sending the same request "
            + "again is the first thing to try; only if it keeps failing is the sign-in itself worth renewing from "
            + "Settings ▸ MCP servers.",

        // Not reached from a failure: None is what a successful acquire carries. Answering with a sentence rather
        // than throwing keeps a logging path from becoming the thing that breaks a session.
        _ => $"the cockpit cannot present a credential to '{serverName}'.",
    };
}
