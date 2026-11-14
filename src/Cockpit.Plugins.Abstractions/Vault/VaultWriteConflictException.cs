namespace Cockpit.Plugins.Abstractions.Vault;

public sealed class VaultWriteConflictException() : InvalidOperationException("The vault item changed; the staged value was retained.");
