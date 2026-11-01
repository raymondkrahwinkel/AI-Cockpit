namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Provider-owned guidance shown in provider and profile settings, without restricting access.
/// </summary>
public sealed record TermsNotice(
    string Text,
    Uri Url,
    string? SubscriptionText = null,
    string? ApiKeyText = null,
    Uri? ApiTermsUrl = null);
