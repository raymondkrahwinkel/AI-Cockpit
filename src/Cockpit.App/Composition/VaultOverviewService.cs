using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Vault;
using Cockpit.Plugins.Abstractions.Vault;
using OverviewAuditEvent = Cockpit.App.Services.VaultAuditEvent;
using OverviewGrantInfo = Cockpit.App.Services.VaultGrantInfo;

namespace Cockpit.App.Composition;

internal sealed class VaultOverviewService(VaultHub hub) : IVaultOverviewService, ISingletonService
{
    public event EventHandler? AvailabilityChanged { add => hub.AvailabilityChanged += value; remove => hub.AvailabilityChanged -= value; }
    public event EventHandler? Changed { add => hub.Changed += value; remove => hub.Changed -= value; }
    public void UseEncryptionSettings(Func<Task> open) => hub.UseEncryptionSettings(open);
    public Task CheckAtStartAsync(CancellationToken cancellationToken = default) => hub.CheckAtStartAsync(cancellationToken);
    public Task<VaultAvailability> EvaluateAsync(CancellationToken cancellationToken = default) => hub.EvaluateAsync(cancellationToken);
    public Task<IReadOnlyList<OverviewGrantInfo>> ListGrantsAsync(CancellationToken cancellationToken = default) => _MapGrants(cancellationToken);
    public Task<IReadOnlyList<OverviewAuditEvent>> ReadAuditAsync(CancellationToken cancellationToken = default) => _MapAudit(cancellationToken);
    public Task RevokeProjectAsync(string projectId, string reference, CancellationToken cancellationToken = default) => hub.RevokeProjectAsync(projectId, reference, cancellationToken);
    public Task RevokeSessionAsync(string paneId, string reference, CancellationToken cancellationToken = default) => hub.RevokeSessionAsync(paneId, reference, cancellationToken);
    public Task RevokeEverywhereAsync(string reference, CancellationToken cancellationToken = default) => hub.RevokeEverywhereAsync(reference, cancellationToken);

    public async Task<IReadOnlyList<VaultProviderInfo>> ReadProvidersAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<VaultProviderInfo>();
        foreach (var provider in hub.Providers)
        {
            try
            {
                var status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                var items = status.State == VaultProviderState.Connected ? await provider.ListItemsAsync(cancellationToken).ConfigureAwait(false) : [];
                rows.Add(new VaultProviderInfo(provider.Id, provider.DisplayName, provider.ReferenceScheme, status, items));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                rows.Add(new VaultProviderInfo(provider.Id, provider.DisplayName, provider.ReferenceScheme, new VaultProviderStatus(VaultProviderState.Error, Error: exception.GetType().Name), []));
            }
        }
        return rows;
    }

    private async Task<IReadOnlyList<OverviewGrantInfo>> _MapGrants(CancellationToken cancellationToken) =>
        (await hub.ListGrantsAsync(cancellationToken).ConfigureAwait(false)).Select(grant => new OverviewGrantInfo(grant.Scope, grant.Owner, grant.Reference)).ToList();
    private async Task<IReadOnlyList<OverviewAuditEvent>> _MapAudit(CancellationToken cancellationToken) =>
        (await hub.ReadAuditAsync(cancellationToken).ConfigureAwait(false)).Select(entry => new OverviewAuditEvent(entry.At, entry.Kind, entry.Session, entry.Item, entry.Count)).ToList();
}
