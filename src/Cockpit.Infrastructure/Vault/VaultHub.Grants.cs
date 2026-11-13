using System.Text.Json;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Infrastructure.Vault;

// AC-2346: what vault_run may inject without a card. A grant is one (reference, variable, exact argv), held for a
// pane in memory or for a project in stateSecret; Once is never stored.
internal sealed partial class VaultHub
{
    internal const string UnavailableToast = "An agent asked for the vault, but Cockpit's credential encryption is not on and unlocked.";

    private readonly Dictionary<string, HashSet<string>> _sessionGrants = new(StringComparer.Ordinal);
    private bool _isUnavailableToastShown;

    public bool HasProviders
    {
        get
        {
            lock (_gate)
            {
                return _providers.Count > 0;
            }
        }
    }

    public IVaultProvider? ProviderFor(string reference)
    {
        var scheme = reference.Split(':', 2)[0];
        lock (_gate)
        {
            return _providers.Values.FirstOrDefault(provider => string.Equals(provider.ReferenceScheme, scheme, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static string GrantKey(string reference, string variable, IReadOnlyList<string> argv) =>
        JsonSerializer.Serialize<string[]>([reference, variable, .. argv], StateOptions);

    public static string ReferenceOf(string grantKey) => JsonSerializer.Deserialize<string[]>(grantKey, StateOptions)?[0] ?? string.Empty;

    // The pane's own grants and, when it has a project, that project's; an unreadable state grants nothing.
    public async Task<(IReadOnlySet<string> Session, IReadOnlySet<string> Project)> GrantsForAsync(
        string paneId, string? projectId, CancellationToken cancellationToken)
    {
        HashSet<string> session;
        lock (_gate)
        {
            session = _sessionGrants.TryGetValue(paneId, out var keys) ? [.. keys] : [];
        }

        if (projectId is null)
        {
            return (session, new HashSet<string>());
        }

        var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
        return (session, state?.ProjectGrants.GetValueOrDefault(projectId)?.ToHashSet(StringComparer.Ordinal) ?? []);
    }

    public void GrantSession(string paneId, IEnumerable<string> grantKeys)
    {
        lock (_gate)
        {
            if (!_sessionGrants.TryGetValue(paneId, out var keys))
            {
                _sessionGrants[paneId] = keys = new HashSet<string>(StringComparer.Ordinal);
            }

            keys.UnionWith(grantKeys);
        }

        _RaiseChanged();
    }

    public async Task<bool> TryGrantProjectAsync(string projectId, IEnumerable<string> grantKeys, CancellationToken cancellationToken)
    {
        var granted = await _TryUpdateAsync(section => _WithState(section, state =>
        {
            var keys = state.ProjectGrants.GetValueOrDefault(projectId) ?? [];
            state.ProjectGrants[projectId] = [.. keys.Union(grantKeys, StringComparer.Ordinal)];
        }), cancellationToken).ConfigureAwait(false);
        _RaiseChanged();
        return granted;
    }

    // A pane that closed takes its session grants with it.
    public void EndSessionsExcept(IReadOnlySet<string> livePaneIds)
    {
        var ended = false;
        lock (_gate)
        {
            foreach (var paneId in _sessionGrants.Keys.Where(paneId => !livePaneIds.Contains(paneId)).ToList())
            {
                ended |= _sessionGrants.Remove(paneId);
            }
        }

        if (ended)
        {
            _RaiseChanged();
        }
    }

    // For the status bar and Options › Vault: one pane's grants for this item end; a process already running keeps its copy.
    public async Task RevokeSessionAsync(string paneId, string reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sessionGrants.GetValueOrDefault(paneId)?.RemoveWhere(key => ReferenceOf(key) == reference);
        }

        _RaiseChanged();
        await AuditAsync("Revoked", paneId, reference, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Every session and project grant for this item.
    public async Task RevokeEverywhereAsync(string reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var keys in _sessionGrants.Values)
            {
                keys.RemoveWhere(key => ReferenceOf(key) == reference);
            }
        }

        await _TryUpdateAsync(section => _WithState(section, state =>
        {
            foreach (var keys in state.ProjectGrants.Values)
            {
                keys.RemoveAll(key => ReferenceOf(key) == reference);
            }
        }), cancellationToken).ConfigureAwait(false);
        _RaiseChanged();
        await AuditAsync("Revoked", null, reference, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AuditAsync(string kind, string? paneId, string? item, int? count = null, CancellationToken cancellationToken = default) =>
        TryAppendAuditAsync(new VaultAuditEvent(DateTimeOffset.UtcNow, kind, paneId, item, count), cancellationToken);

    // One toast per closed period, so repeated calls do not stack them.
    public void ShowUnavailable()
    {
        lock (_gate)
        {
            if (_isUnavailableToastShown)
            {
                return;
            }

            _isUnavailableToastShown = true;
        }

        _toasts?.Show(UnavailableToast, ToastSeverity.Warning, "Open Security", OpenEncryptionSettings);
    }
}
