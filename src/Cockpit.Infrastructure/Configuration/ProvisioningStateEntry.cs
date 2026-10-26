using Cockpit.Core.Provisioning;

namespace Cockpit.Infrastructure.Configuration;

// AC-1507 (manifest-v1 §4.2): what the applier remembers between two applies, keyed by `ManagedConfigKeys` paths.
// Never in a backup: it comes back from the manifest. Values are JSON text, hashes SHA-256 of that text.
internal sealed class ProvisioningStateEntry
{
    // Scheme, host and path only; the token never lands here.
    public string? Source { get; set; }

    public string? ManifestId { get; set; }

    public long? Revision { get; set; }

    // The last value the applier wrote, so a later local change reads as the operator's own choice.
    public Dictionary<string, string> Applied { get; set; } = new(StringComparer.Ordinal);

    // The last manifest value seen, so "your choice stays" is said once per new default, not on every apply.
    public Dictionary<string, string> Offered { get; set; } = new(StringComparer.Ordinal);

    // The local value a lock pushed aside, put back when the lock goes.
    public Dictionary<string, string> Displaced { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, ManagedKeyEntry> Managed { get; set; } = new(StringComparer.Ordinal);

    // AC-1593: the policy as last applied, `By` already the organisation's name. Clamped at use, never written into the settings.
    public ProvisionPolicy? Policy { get; set; }
}

internal sealed class ManagedKeyEntry
{
    // Who manages it, as shown: "Managed by Acme".
    public string By { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
