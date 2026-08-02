namespace Cockpit.Core.Mcp;

// The answer to "can this session use that server, and with what credential" (AC-353). Both halves travel together
// because a caller that reads only the token cannot tell "needs no auth" from "needs a sign-in nobody has done" —
// and those two lead to opposite things being said to the operator.
//
// `State`: What the cockpit knows about its standing with the server.
// `AccessToken`: The credential to present, or `null` when there is none to present.
// `SignInStage`:
// How far a sign-in got on the way to this answer (AC-457). A stage and nothing else: an exception would carry
// detail the operator must not be shown, while a stage is enough to stop the UI naming a window that never opened.
// `Reason`:
// What is wrong (AC-524), so the caller can say what the operator should do about it rather than guess.
// `McpOAuthAttentionReason.None` when nothing is.
public readonly record struct McpOAuthAccess(
    McpAuthState State,
    string? AccessToken,
    McpSignInStage SignInStage = McpSignInStage.NoBrowserLaunched,
    McpOAuthAttentionReason Reason = McpOAuthAttentionReason.None)
{
    // The server needs nothing from the OAuth machinery.
    public static McpOAuthAccess NotRequired { get; } = new(McpAuthState.NotRequired, null);

    // Nobody has signed in, or the sign-in can no longer be renewed without the operator.
    public static McpOAuthAccess AuthorizationRequired { get; } = new(McpAuthState.AuthorizationRequired, null);

    // A usable token is held.
    public static McpOAuthAccess Authorized(string accessToken) => new(McpAuthState.Authorized, accessToken);

    // Overrides the generated `ToString()`, which would print the token in full anywhere this lands in a log
    // line or an exception message (Iron Law #8).
    public override string ToString() =>
        $"{nameof(McpOAuthAccess)} {{ {nameof(State)} = {State}, "
        + $"{nameof(AccessToken)} = {(string.IsNullOrEmpty(AccessToken) ? "null" : "***")}, "
        + $"{nameof(SignInStage)} = {SignInStage}, {nameof(Reason)} = {Reason} }}";
}
