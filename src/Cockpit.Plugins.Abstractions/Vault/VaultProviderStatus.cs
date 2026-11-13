namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// Whether a provider is connected, and to which account and scope (e.g. "reads one vault: Agents").
/// </summary>
public sealed record VaultProviderStatus(VaultProviderState State, string? Account = null, string? Scope = null, string? Error = null);

public enum VaultProviderState
{
    NotConfigured,
    Connected,
    Error,
}
