namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// Where the cockpit's OAuth standing is for an MCP server a plugin contributed via <see cref="ICockpitHost.AddMcpServer"/>
/// (AC-243/AC-355) — the plugin-facing mirror of the host's own internal auth state, read-only and without a
/// <c>Cockpit.Core</c> type in the signature (see the isolation note on <see cref="ICockpitHost"/>).
/// </summary>
public enum PluginMcpAuthState
{
    /// <summary>
    /// The host has never heard of a server by that name, the contribution is not OAuth, or the host predates <see cref="ICockpitHost.GetMcpServerAuthStateAsync"/>.
    /// </summary>
    Unknown,

    /// <summary>
    /// A usable access token is held for this server.
    /// </summary>
    Authorized,

    /// <summary>
    /// The server needs a sign-in that has not happened, or whose token can no longer be renewed silently.
    /// </summary>
    AuthorizationRequired,
}

/// <summary>
/// What came of the operator's own "sign in" act for an MCP server a plugin contributed (AC-243/AC-355) — a named
/// outcome rather than a token or a failure detail (Iron Law #8): the plugin never sees a credential, only whether
/// asking for one worked.
/// </summary>
public enum PluginMcpSignInOutcome
{
    // Unavailable is deliberately the zero value: default(PluginMcpSignInOutcome) — an unstubbed fake, a missed
    // switch arm, a deserialization gap — must never read as "signed in". Authorized used to be 0, which made
    // Substitute.For<ICockpitHost>() (and any other unconfigured Task<T> fake) report success for free.

    /// <summary>
    /// This host predates <see cref="ICockpitHost.SignInMcpServerAsync"/>, or the named contribution is not (or no longer) an OAuth server.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The sign-in produced a usable access token.
    /// </summary>
    Authorized,

    /// <summary>
    /// The sign-in was attempted but did not produce a usable token — the browser closed, the server refused, or the credential that came back was unusable. See the host's own MCP-servers dialog for detail.
    /// </summary>
    Declined,

    /// <summary>
    /// The sign-in could not even be attempted — a network/store failure, not the operator declining.
    /// </summary>
    Unreachable,
}
