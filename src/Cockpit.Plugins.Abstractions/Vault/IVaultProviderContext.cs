namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// What the hub hands a provider: its own credential slot, gated on the host's credential encryption.
/// </summary>
/// <remarks>
/// A provider keeps its token here and nowhere else (not in <see cref="IPluginStorage"/>), so it is encrypted and wiped with the vault.
/// </remarks>
public interface IVaultProviderContext
{
    VaultAvailability Availability { get; }

    event EventHandler? AvailabilityChanged;

    /// <summary>
    /// Stores the provider's credential; false while the vault is not <see cref="VaultAvailability.Available"/>.
    /// </summary>
    bool TryStoreCredential(string value);

    /// <summary>
    /// The stored credential; null while the vault is not available, or when none is stored or it cannot be read.
    /// </summary>
    string? ReadCredential();

    void ClearCredential();

    /// <summary>
    /// Opens Options › Security, where the operator turns encryption on.
    /// </summary>
    void OpenEncryptionSettings();
}

/// <summary>
/// The context of a host without a vault hub: never available, stores nothing.
/// </summary>
public sealed class UnavailableVaultProviderContext : IVaultProviderContext
{
    public static UnavailableVaultProviderContext Instance { get; } = new();

    private UnavailableVaultProviderContext()
    {
    }

    public VaultAvailability Availability => VaultAvailability.EncryptionOff;

    public event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public bool TryStoreCredential(string value) => false;

    public string? ReadCredential() => null;

    public void ClearCredential()
    {
    }

    public void OpenEncryptionSettings()
    {
    }
}
