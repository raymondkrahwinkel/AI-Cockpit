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

    // AC-2333 S4: references a pane revoked for itself; its project's grants no longer cover them there until it approves again.
    private readonly Dictionary<string, HashSet<string>> _sessionBlocks = new(StringComparer.Ordinal);
    private bool _isUnavailableToastShown;

    // AC-2333 S3: raised by every revoke and wipe once it took effect. A run reads it before it looks at its grants
    // and may only start while it is unchanged.
    // ponytail: one counter for every grant, so an unrelated revoke also refuses a run in flight; per key if that bites.
    private long _generation;

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

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
        HashSet<string> blocked;
        lock (_gate)
        {
            session = _sessionGrants.TryGetValue(paneId, out var keys) ? [.. keys] : [];
            blocked = _sessionBlocks.TryGetValue(paneId, out var references) ? [.. references] : [];
        }

        if (projectId is null)
        {
            return (session, new HashSet<string>());
        }

        var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
        return (session, state?.ProjectGrants.GetValueOrDefault(projectId)?.Where(key => !blocked.Contains(ReferenceOf(key))).ToHashSet(StringComparer.Ordinal) ?? []);
    }

    // Starts the run's process only if nothing was revoked or wiped since `generation`, the key is still in hand and
    // `stillWanted` holds. Serialised with every revoke, so none can land between this check and the start.
    public bool TryStart(long generation, Func<bool> stillWanted, Action start)
    {
        lock (_gate)
        {
            if (generation != _generation || _keyHolder.Protector is null || !stillWanted())
            {
                return false;
            }

            start();
            return true;
        }
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
            _Unblock(paneId, keys);
        }

        _RaiseChanged();
    }

    // An approval in a pane that revoked an item for itself lifts that pane's block again.
    public async Task<bool> TryGrantProjectAsync(string paneId, string projectId, IReadOnlyCollection<string> grantKeys, CancellationToken cancellationToken)
    {
        var granted = await _TryUpdateAsync(section => _WithState(section, state =>
        {
            var keys = state.ProjectGrants.GetValueOrDefault(projectId) ?? [];
            state.ProjectGrants[projectId] = [.. keys.Union(grantKeys, StringComparer.Ordinal)];
        }), cancellationToken).ConfigureAwait(false);
        if (granted)
        {
            lock (_gate)
            {
                _Unblock(paneId, grantKeys);
            }
        }

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

            foreach (var paneId in _sessionBlocks.Keys.Where(paneId => !livePaneIds.Contains(paneId)).ToList())
            {
                _sessionBlocks.Remove(paneId);
            }
        }

        if (ended)
        {
            _RaiseChanged();
        }
    }

    // For the status bar and Options › Vault: this item is no longer covered in this pane, by its own grants nor by its
    // project's, until a card approves it again; a process already running keeps its copy.
    public async Task RevokeSessionAsync(string paneId, string reference, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sessionGrants.GetValueOrDefault(paneId)?.RemoveWhere(key => ReferenceOf(key) == reference);
            if (!_sessionBlocks.TryGetValue(paneId, out var blocked))
            {
                _sessionBlocks[paneId] = blocked = new HashSet<string>(StringComparer.Ordinal);
            }

            blocked.Add(reference);
            _generation++;
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
        _Invalidate();
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

    private void _Invalidate()
    {
        lock (_gate)
        {
            _generation++;
        }
    }

    private void _Unblock(string paneId, IEnumerable<string> grantKeys)
    {
        if (_sessionBlocks.GetValueOrDefault(paneId) is { } blocked)
        {
            blocked.ExceptWith(grantKeys.Select(ReferenceOf));
        }
    }
}
