using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.App.Services;

internal interface IVaultOverviewService
{
    const string SessionScope = "session";
    const string ProjectScope = "project";
    const string BlockedScope = "blocked";
    const string WipedMessage = "Vault data was removed because credential encryption was turned off.";

    event EventHandler? AvailabilityChanged;
    event EventHandler? Changed;

    void UseEncryptionSettings(Func<Task> open);
    Task CheckAtStartAsync(CancellationToken cancellationToken = default);
    Task<VaultAvailability> EvaluateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultProviderInfo>> ReadProvidersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultGrantInfo>> ListGrantsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultAuditEvent>> ReadAuditAsync(CancellationToken cancellationToken = default);
    Task RevokeProjectAsync(string projectId, string reference, CancellationToken cancellationToken = default);
    Task RevokeSessionAsync(string paneId, string reference, CancellationToken cancellationToken = default);
    Task RevokeEverywhereAsync(string reference, CancellationToken cancellationToken = default);
}

internal sealed record VaultProviderInfo(string Id, string DisplayName, string ReferenceScheme, VaultProviderStatus Status, IReadOnlyList<VaultItem> Items);
internal sealed record VaultGrantInfo(string Scope, string Owner, string Reference);
internal sealed record VaultAuditEvent(DateTimeOffset At, string Kind, string? Session, string? Item, int? Count = null);
