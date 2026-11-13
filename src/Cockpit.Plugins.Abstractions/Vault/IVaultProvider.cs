namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// A secret store a plugin connects to the host's vault hub (AC-2333), registered with <see cref="ICockpitHost.AddVaultProvider"/>.
/// </summary>
public interface IVaultProvider
{
    /// <summary>
    /// The provider's id, e.g. <c>1password</c> or <c>bitwarden</c>.
    /// </summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// The scheme its references start with, e.g. <c>op</c> or <c>bws</c>.
    /// </summary>
    string ReferenceScheme { get; }

    Task<VaultProviderStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The items it offers, as names and references only — never a value.
    /// </summary>
    Task<IReadOnlyList<VaultItem>> ListItemsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The value behind <paramref name="reference"/>. Only the hub calls this.
    /// </summary>
    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken);
}
