namespace Cockpit.Plugins.Abstractions.Vault;

/// <summary>
/// Whether the vault can be used: only while the host's credential encryption is on and unlocked.
/// </summary>
public enum VaultAvailability
{
    Available,
    EncryptionOff,
    Locked,
}

public static class VaultAvailabilityText
{
    /// <summary>
    /// What to tell the operator about <paramref name="availability"/>.
    /// </summary>
    public static string Describe(this VaultAvailability availability) => availability switch
    {
        VaultAvailability.Available => "The vault is available.",
        VaultAvailability.Locked => "The vault is locked until Cockpit is unlocked.",
        _ => "The vault needs credential encryption. Turn it on in Options › Security › Encryption.",
    };
}
