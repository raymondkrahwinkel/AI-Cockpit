namespace Cockpit.Core.Mcp;

/// <summary>
/// The one sentence the operator is given when an OAuth-protected MCP server cannot be used (AC-524): what went
/// wrong, and what they can do about it. One place, because it is written into three of them — the log line the
/// coordinator raises on the transition, the line a session start leaves behind, and the error a running session's
/// tool call comes back with — and three copies of an instruction are three instructions that drift.
/// <para>
/// Every reason gets its own action. A blanket "sign in again" is the mistake this replaces: it is wrong advice for
/// a server that was simply unreachable, and it sends someone who never signed in looking for a sign-in to renew.
/// </para>
/// </summary>
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

        // Not reached from a failure: None is what a successful acquire carries. Answering with a sentence rather
        // than throwing keeps a logging path from becoming the thing that breaks a session.
        _ => $"the cockpit cannot present a credential to '{serverName}'.",
    };
}
