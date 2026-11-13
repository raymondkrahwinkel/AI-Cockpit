using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Vault;

// AC-2347: what the status bar and Options › Vault read. Names and references only, never a value.
internal sealed partial class VaultHub
{
    // Raised when a grant or the audit changed; the UI reads again.
    public event EventHandler? Changed;

    // One row per (pane or project, item); empty while the vault is closed.
    public async Task<IReadOnlyList<VaultGrantInfo>> ListGrantsAsync(CancellationToken cancellationToken = default)
    {
        if (await EvaluateAsync(cancellationToken).ConfigureAwait(false) != VaultAvailability.Available)
        {
            return [];
        }

        var grants = new List<VaultGrantInfo>();
        lock (_gate)
        {
            foreach (var (paneId, keys) in _sessionGrants)
            {
                grants.AddRange(keys.Select(ReferenceOf).Distinct(StringComparer.Ordinal).Select(reference => new VaultGrantInfo(IVaultHub.SessionScope, paneId, reference)));
            }
        }

        var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (projectId, keys) in state?.ProjectGrants ?? [])
        {
            grants.AddRange(keys.Select(ReferenceOf).Distinct(StringComparer.Ordinal).Select(reference => new VaultGrantInfo(IVaultHub.ProjectScope, projectId, reference)));
        }

        return grants;
    }

    // Newest last, as stored; empty while the vault is closed or the ring does not decrypt.
    public async Task<IReadOnlyList<VaultAuditEvent>> ReadAuditAsync(CancellationToken cancellationToken = default)
    {
        if (await EvaluateAsync(cancellationToken).ConfigureAwait(false) != VaultAvailability.Available)
        {
            return [];
        }

        try
        {
            var file = await _config.ReadAsync(cancellationToken).ConfigureAwait(false);
            return _Parse<List<VaultAuditEvent>>(file?.Vault?.AuditSecret) ?? [];
        }
        catch (Exception exception) when (exception is Cockpit.Core.Secrets.SecretProtectionException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "The vault's audit could not be read; it is shown empty.");
            return [];
        }
    }

    // One project's grants for this item end; sessions that already started keep their copy.
    public async Task RevokeProjectAsync(string projectId, string reference, CancellationToken cancellationToken = default)
    {
        await _TryUpdateAsync(section => _WithState(section, state =>
        {
            state.ProjectGrants.GetValueOrDefault(projectId)?.RemoveAll(key => ReferenceOf(key) == reference);
        }), cancellationToken).ConfigureAwait(false);
        _RaiseChanged();
        await AuditAsync("Revoked", null, reference, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void _RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
