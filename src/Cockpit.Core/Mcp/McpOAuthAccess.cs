namespace Cockpit.Core.Mcp;

// AC-353: the answer to "can this session use that server, and with what credential". Both halves travel together
// because reading only the token cannot tell "needs no auth" from "needs a sign-in nobody has done". Also carries
// `SignInStage` (AC-457, no exception detail shown to the operator) and `Reason` (AC-524) for what to do about it.
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
