namespace Cockpit.Plugins.Abstractions.OAuth;

/// <summary>
/// An OAuth 2.0 provider a plugin signs its operator in to (AC-1407), read from the plugin's own settings on every call.
/// The host keeps none of it except the client id and token endpoint an account was issued under.
/// </summary>
/// <param name="Id">The plugin's own name for the provider, such as <c>microsoft</c>; accounts are listed per id.</param>
/// <param name="AuthorizationEndpoint">Where the browser is sent to sign in; https only.</param>
/// <param name="TokenEndpoint">Where codes and refresh tokens are redeemed; https only.</param>
/// <param name="ClientId">The app registration's client id. Blank means <see cref="PluginOAuthState.NotConfigured"/>.</param>
/// <param name="ClientSecret">A non-confidential secret some desktop clients carry (Google); keep it in the plugin's secret keys.</param>
/// <param name="AuthorizationParameters">Extra query parameters for the sign-in link, such as <c>prompt=select_account</c>.</param>
public sealed record PluginOAuthProvider(
    string Id,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    string? ClientId,
    string? ClientSecret = null,
    IReadOnlyDictionary<string, string>? AuthorizationParameters = null)
{
    /// <summary>
    /// Whether an app registration is filled in; without one, sign-in is shown disabled with the reason, never hidden.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(PluginOAuthProvider)} {{ {nameof(Id)} = {Id}, {nameof(AuthorizationEndpoint)} = {AuthorizationEndpoint}, "
        + $"{nameof(TokenEndpoint)} = {TokenEndpoint}, {nameof(ClientId)} = {ClientId}, "
        + $"{nameof(ClientSecret)} = {(string.IsNullOrEmpty(ClientSecret) ? "null" : "***")} }}";
}

/// <summary>
/// Where an account stands, for the UI's "connected as …" line.
/// </summary>
public enum PluginOAuthState
{
    /// <summary>
    /// The host does not know the account, or predates host OAuth.
    /// </summary>
    Unknown,

    /// <summary>
    /// The provider has no client id, so nothing can be signed in to.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// A token is held, or can be renewed without the operator.
    /// </summary>
    Connected,

    /// <summary>
    /// The grant was revoked or no longer fits the provider's settings; only a new sign-in helps.
    /// </summary>
    ReconnectRequired,
}

/// <summary>
/// A connected account. It never carries a token.
/// </summary>
/// <param name="AccountId">The host's id for the account, stable across restarts.</param>
/// <param name="ProviderId">The <see cref="PluginOAuthProvider.Id"/> it was connected through.</param>
/// <param name="DisplayName">Who signed in, from the id token when <c>openid email profile</c> was asked for.</param>
/// <param name="State">Where the account stands.</param>
/// <param name="Scopes">The scopes the last sign-in asked for.</param>
public sealed record PluginOAuthAccount(
    string AccountId,
    string ProviderId,
    string? DisplayName,
    PluginOAuthState State,
    IReadOnlyList<string> Scopes);

/// <summary>
/// What came of a connect or a token request. <see cref="Unavailable"/> is the zero value, so a default never reads as success.
/// </summary>
public enum PluginOAuthOutcome
{
    /// <summary>
    /// This host offers no OAuth, the account is unknown, or no browser could be opened.
    /// </summary>
    Unavailable,

    /// <summary>
    /// It worked.
    /// </summary>
    Ok,

    /// <summary>
    /// The provider has no client id.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// The operator declined, closed the tab or let the sign-in time out.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The grant is dead (<c>invalid_grant</c>) or the provider's settings changed; connect the account again.
    /// </summary>
    ReconnectRequired,

    /// <summary>
    /// The scopes asked for were never consented to; connect the account again with them.
    /// </summary>
    ConsentRequired,

    /// <summary>
    /// The provider refused the request itself, such as an unknown client id.
    /// </summary>
    Rejected,

    /// <summary>
    /// The provider could not be reached or failed; trying again later may work.
    /// </summary>
    Unreachable,
}

/// <summary>
/// The answer to <see cref="ICockpitHost.ConnectOAuthAccountAsync"/>: the account when <see cref="Outcome"/> is <see cref="PluginOAuthOutcome.Ok"/>.
/// </summary>
public sealed record PluginOAuthConnectResult(PluginOAuthOutcome Outcome, PluginOAuthAccount? Account = null);

/// <summary>
/// The answer to <see cref="ICockpitHost.GetOAuthAccessTokenAsync"/>. Use the token in the backend only; never pass it to a UI, session or log.
/// </summary>
public sealed record PluginOAuthTokenResult(PluginOAuthOutcome Outcome, string? AccessToken = null)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(PluginOAuthTokenResult)} {{ {nameof(Outcome)} = {Outcome}, {nameof(AccessToken)} = {(AccessToken is null ? "null" : "***")} }}";
}
