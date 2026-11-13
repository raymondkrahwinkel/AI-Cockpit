namespace Cockpit.Plugins.Abstractions.Vault;

public interface IVaultHub
{
    const string SessionScope = "session";
    const string ProjectScope = "project";
    const string WipedMessage = "Vault data was removed because credential encryption was turned off.";

    event EventHandler? AvailabilityChanged;
    event EventHandler? Changed;

    IReadOnlyList<IVaultProvider> Providers { get; }

    void UseEncryptionSettings(Func<Task> open);
    Task CheckAtStartAsync(CancellationToken cancellationToken = default);
    Task<VaultAvailability> EvaluateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultGrantInfo>> ListGrantsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultAuditEvent>> ReadAuditAsync(CancellationToken cancellationToken = default);
    IVaultProvider? ProviderFor(string reference);
    Task RevokeProjectAsync(string projectId, string reference, CancellationToken cancellationToken = default);
    Task RevokeSessionAsync(string paneId, string reference, CancellationToken cancellationToken = default);
    Task RevokeEverywhereAsync(string reference, CancellationToken cancellationToken = default);
}

public sealed record VaultGrantInfo(string Scope, string Owner, string Reference);

public sealed record VaultAuditEvent(DateTimeOffset At, string Kind, string? Session, string? Item, int? Count = null);
