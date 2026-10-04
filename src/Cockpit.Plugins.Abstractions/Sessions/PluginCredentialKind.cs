namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What kind of credential a provider's sign-in rests on, as the provider's own CLI or config reports it (AC-1483).
/// Returned by <see cref="SessionProviderRegistration.CredentialKind"/>; never derived from a credential's contents.
/// </summary>
public enum PluginCredentialKind
{
    /// <summary>
    /// The provider cannot say, or said something this build does not recognise. The host claims nothing.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A subscription login whose refresh token the CLI renews by itself.
    /// </summary>
    RenewingLogin = 1,

    /// <summary>
    /// A login that does not renew itself, such as a pasted token without a refresh token.
    /// </summary>
    Login = 2,

    /// <summary>
    /// An API key the operator entered into the profile or the CLI.
    /// </summary>
    ApiKey = 3,

    /// <summary>
    /// An API key that reaches the provider from the process environment (a container secret), not from the profile.
    /// </summary>
    ApiKeyFromSecret = 4,
}
