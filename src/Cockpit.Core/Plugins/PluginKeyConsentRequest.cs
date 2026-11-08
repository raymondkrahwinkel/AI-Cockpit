namespace Cockpit.Core.Plugins;

public sealed record PluginKeyConsentRequest(
    string Id, string Version, PluginTrustDecision Decision,
    string? Organization, string? DepotHost, string? Fingerprint, string? KeyName,
    string? Repository, string? Tag, string? PreviousFingerprint, string? PreviousSource);
