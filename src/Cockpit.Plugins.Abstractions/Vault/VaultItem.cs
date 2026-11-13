namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// One secret a provider offers, by reference and name; it never carries the value.
/// </summary>
public sealed record VaultItem(string Reference, string DisplayName);
