namespace Cockpit.Infrastructure.Configuration;

// AC-1407: one account a plugin connected at an OAuth provider, in `pluginOAuthAccounts`. Field names are load-bearing:
// `SecretFields` encrypts `AccessToken`/`RefreshToken` by their name, and `Endpoint` deliberately avoids "token" so the
// address the account is bound to stays readable and survives a backup without credentials.
internal sealed class PluginOAuthAccountEntry
{
    public string AccountId { get; set; } = string.Empty;

    // The plugin folder id that connected it; another plugin's host never sees this account.
    public string PluginId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    // Display only, from the id token's claims.
    public string? DisplayName { get; set; }

    // The id token's `iss` and `sub`, so signing in to the same account twice updates it rather than adding a second.
    public string? Subject { get; set; }

    public string AccessToken { get; set; } = string.Empty;

    public string? RefreshToken { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    // The scopes asked for and consented to, as the plugin named them (providers echo them back in their own spelling).
    public List<string> Scopes { get; set; } = [];

    // The client id and token endpoint the grant was issued under; a refresh token is never presented to any other.
    public string ClientId { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    // Set by `invalid_grant`: the grant is dead, and nothing is sent to the provider again until a new sign-in.
    public bool ReconnectRequired { get; set; }
}
