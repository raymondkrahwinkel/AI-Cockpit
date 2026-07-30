namespace Cockpit.Core.Mcp;

/// <summary>
/// The answer to "can this session use that server, and with what credential" (AC-353). Both halves travel together
/// because a caller that reads only the token cannot tell "needs no auth" from "needs a sign-in nobody has done" —
/// and those two lead to opposite things being said to the operator.
/// </summary>
/// <param name="State">What the cockpit knows about its standing with the server.</param>
/// <param name="AccessToken">The credential to present, or <see langword="null"/> when there is none to present.</param>
/// <param name="SignInStage">
/// How far a sign-in got on the way to this answer (AC-457). A stage and nothing else: an exception would carry
/// detail the operator must not be shown, while a stage is enough to stop the UI naming a window that never opened.
/// </param>
/// <param name="Reason">
/// Why no credential is being handed over (AC-524), so the caller can say what the operator should do about it
/// rather than guess. <see cref="McpOAuthAttentionReason.None"/> whenever there is a credential.
/// </param>
public readonly record struct McpOAuthAccess(
    McpAuthState State,
    string? AccessToken,
    McpSignInStage SignInStage = McpSignInStage.NoBrowserLaunched,
    McpOAuthAttentionReason Reason = McpOAuthAttentionReason.None)
{
    /// <summary>The server needs nothing from the OAuth machinery.</summary>
    public static McpOAuthAccess NotRequired { get; } = new(McpAuthState.NotRequired, null);

    /// <summary>Nobody has signed in, or the sign-in can no longer be renewed without the operator.</summary>
    public static McpOAuthAccess AuthorizationRequired { get; } = new(McpAuthState.AuthorizationRequired, null);

    /// <summary>A usable token is held.</summary>
    public static McpOAuthAccess Authorized(string accessToken) => new(McpAuthState.Authorized, accessToken);

    /// <summary>
    /// Overrides the generated <c>ToString()</c>, which would print the token in full anywhere this lands in a log
    /// line or an exception message (Iron Law #8).
    /// </summary>
    public override string ToString() =>
        $"{nameof(McpOAuthAccess)} {{ {nameof(State)} = {State}, "
        + $"{nameof(AccessToken)} = {(string.IsNullOrEmpty(AccessToken) ? "null" : "***")}, "
        + $"{nameof(SignInStage)} = {SignInStage}, {nameof(Reason)} = {Reason} }}";
}
