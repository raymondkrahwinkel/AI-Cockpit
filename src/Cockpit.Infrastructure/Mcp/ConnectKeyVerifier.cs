using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// AC-1351: the node endpoint's door for everyone who is not a local session — connect keys (DEP-208's model) and
// the pairing secret, behind one per-address lockout and one audit trail. A caller that fails learns nothing about
// why: unknown, wrong, expired, revoked and locked out are all a null here, and the reason goes to the audit only.
internal sealed class ConnectKeyVerifier : ISingletonService
{
    public const string KeyPrefix = "ck_";

    public const string BootstrapFileVariable = "COCKPIT_CONNECT_KEY_FILE";

    public const string BootstrapVariable = "COCKPIT_CONNECT_KEY";

    // Characters after `ck_` kept as the key's public handle: enough to tell keys apart in a list, far too few to
    // matter for guessing the rest (48 of 256 bits).
    private const int PrefixLength = 8;

    // A bootstrap key is chosen by whoever sets up the container, so it gets the length of an issued one (256 bits
    // in base64url) and a floor on distinct characters, which a random key clears by far and `aaaa…` does not.
    private const int MinimumBootstrapLength = 43;

    private const int MinimumBootstrapDistinctCharacters = 16;

    private readonly CockpitConfigFileAccess _configFile;
    private readonly Func<string, string?> _environment;
    private readonly Action<string> _forgetEnvironment;
    private readonly TimeProvider _time;
    private readonly NodeAccessAuditLog _audit;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // Per address, an IPv6 /64 as one. ponytail: in memory, so a restart forgets every lockout, and past
    // `MaxTrackedAddresses` an attacker with that many /64s can push a locked one out. Persist it past LAN/Tailscale (B2).
    private readonly Dictionary<string, _AddressState> _addresses = new(StringComparer.Ordinal);

    // Per key, what its in-flight requests are aborted by. The MCP transport is stateless, so that is all it has open.
    // ponytail: expiry does not fire it, so an expired key's call already in flight finishes (new requests are
    // refused). Cancel on ExpiresAt too if node calls ever run long.
    private readonly Dictionary<string, CancellationTokenSource> _revocations = new(StringComparer.Ordinal);

    // Not persisted: the controller polls every 20 s, and that would be a cockpit.json write every 20 s.
    private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);

    private List<ConnectKey> _persisted = [];
    private ConnectKey? _bootstrap;
    private ConnectKeyPolicy _policy = ConnectKeyPolicy.Default;
    private bool _loaded;

    public ConnectKeyVerifier(NodeAccessAuditLog audit, ILogger<ConnectKeyVerifier> logger)
        : this(CockpitConfigPath.Default, ConnectKeyBootstrapEnvironment.Get, ConnectKeyBootstrapEnvironment.Forget, TimeProvider.System, audit, logger)
    {
    }

    // Test seam: another config file, environment and clock.
    internal ConnectKeyVerifier(string configFilePath, Func<string, string?> environment, Action<string> forgetEnvironment, TimeProvider time, NodeAccessAuditLog audit, ILogger logger)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _environment = environment;
        _forgetEnvironment = forgetEnvironment;
        _time = time;
        _audit = audit;
        _logger = logger;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var section = (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.NodeConnectKeys;
            var persisted = (section?.Keys ?? []).Where(_IsWellFormed).ToList();
            var bootstrap = await _LoadBootstrapAsync(persisted, cancellationToken).ConfigureAwait(false);

            // Read once and let go, whether it was usable or not: the secret has no business outliving this.
            _forgetEnvironment(BootstrapFileVariable);
            _forgetEnvironment(BootstrapVariable);

            lock (_gate)
            {
                _persisted = persisted;
                _bootstrap = bootstrap;
                _policy = section?.Policy ?? ConnectKeyPolicy.Default;
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Who this bearer token is, or null. `pairingSecret` is the live pairing secret, checked alongside so both
    // credentials share one lockout and one refusal.
    public async Task<NodeCaller?> AuthenticateAsync(string token, string? pairingSecret, string remoteAddress, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var bucket = _BucketOf(remoteAddress);
        NodeCaller? caller = null;
        List<NodeAccessAuditEntry> audit = [];
        lock (_gate)
        {
            var found = token.StartsWith(KeyPrefix, StringComparison.Ordinal) ? _Find(token) : null;
            _addresses.TryGetValue(bucket, out var state);

            // A usable key passes a lockout: its 256 bits are not what a lockout protects, and a neighbour behind the
            // same NAT sending garbage must not shut it out. Only failures are locked out, the pairing secret among them.
            if (found is { } key && key.IsUsableAt(now))
            {
                caller = new NodeCaller(key.Prefix, key.Label, key.Capability, remoteAddress, _RevocationOf(key.Prefix), key.HoldsAssistant, key.EffectiveScope());
                _lastUsed[key.Prefix] = now;
            }
            else if (state is not null && state.LockedUntil > now)
            {
                // Counted, not audited one by one: a flood during a lockout would otherwise grow the trail unbounded.
                state.RefusedWhileLockedOut++;
            }
            else
            {
                if (state is { RefusedWhileLockedOut: > 0 })
                {
                    audit.Add(new NodeAccessAuditEntry(now, "not checked", null, remoteAddress, null, $"lockout ended: {state.RefusedWhileLockedOut} attempts refused during it"));
                    state.RefusedWhileLockedOut = 0;
                }

                if (pairingSecret is { Length: > 0 } secret && ConstantTimeEquals(token, secret))
                {
                    caller = NodeCaller.ForPairing(remoteAddress);
                }
                else
                {
                    var credential = found is not null ? "connect key" : token.Length == 0 ? "none" : "unknown";
                    var refusal = found switch
                    {
                        { RevokedAt: not null } => "refused: revoked key",
                        { } => "refused: expired key",
                        null => "refused: unknown credential",
                    };
                    audit.Add(new NodeAccessAuditEntry(now, credential, found?.Prefix, remoteAddress, null, refusal));

                    // A success leaves the failures standing: a controller polling from behind the same NAT must not
                    // wipe an attacker's count every 20 s.
                    if (_RecordFailure(bucket, now) is { } lockedUntil)
                    {
                        audit.Add(new NodeAccessAuditEntry(now, credential, found?.Prefix, remoteAddress, null, $"lockout started until {lockedUntil:O}"));
                    }
                }
            }
        }

        foreach (var entry in audit)
        {
            await _audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return caller;
    }

    // The raw key is in the return value and nowhere else — not in the log, the audit or cockpit.json.
    public async Task<(ConnectKey Key, string Secret)> IssueAsync(string label, ConnectKeyCapability capability, int? expiresInDays, NodeCaller issuedBy, bool holdsAssistant = false, ConnectKeyScope? scope = null, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var days = expiresInDays ?? _policy.DefaultExpiryDays;
        if (days < 1 || days > _policy.MaxExpiryDays)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresInDays), $"expiresInDays must be between 1 and {_policy.MaxExpiryDays}.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            string secret;
            ConnectKey key;
            List<ConnectKey> next;
            lock (_gate)
            {
                do
                {
                    secret = KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
                }
                while (_AllKeys().Any(existing => string.Equals(existing.Prefix, _PrefixOf(secret), StringComparison.Ordinal)));

                key = new ConnectKey(_PrefixOf(secret), _Hash(secret), capability, label.Trim(), now, now.AddDays(days), HoldsAssistant: holdsAssistant, Scope: scope ?? ConnectKeyScope.Default);
                next = [.. _persisted, key];
            }

            // Saved before it counts: a key that works now but is gone after a restart is worse than a failed issue.
            await _SaveAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _persisted = next;
            }

            _logger.LogInformation("Issued connect key {Prefix} ({Capability}), expiring {ExpiresAt}.", key.Prefix, key.Capability, key.ExpiresAt);
            await _audit.RecordAsync(new NodeAccessAuditEntry(now, issuedBy.Credential, issuedBy.KeyPrefix, issuedBy.RemoteAddress, "issue_connect_key", "issued", key.Prefix), cancellationToken).ConfigureAwait(false);
            return (key, secret);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // False when no live key carries this prefix. The key stops working in memory before the save; a failed save
    // is never silent — logged, audited and thrown — and the next save that does succeed carries the revocation.
    public async Task<bool> RevokeAsync(string prefix, NodeCaller revokedBy, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            List<ConnectKey> next;
            CancellationTokenSource? inFlight;
            lock (_gate)
            {
                if (_AllKeys().FirstOrDefault(key => key.RevokedAt is null && string.Equals(key.Prefix, prefix, StringComparison.Ordinal)) is not { } target)
                {
                    return false;
                }

                var revoked = target with { RevokedAt = now };
                next = target.IsBootstrap ? [.. _persisted, revoked] : [.. _persisted.Select(key => ReferenceEquals(key, target) ? revoked : key)];
                _persisted = next;
                if (target.IsBootstrap)
                {
                    _bootstrap = null;
                }

                _revocations.Remove(prefix, out inFlight);
            }

            // Not disposed: a request that authenticated just before this still registers on its token.
            inFlight?.Cancel();

            try
            {
                await _SaveAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Revoked connect key {Prefix} for this run only: the revocation could not be saved.", prefix);
                await _audit.RecordAsync(new NodeAccessAuditEntry(now, revokedBy.Credential, revokedBy.KeyPrefix, revokedBy.RemoteAddress, "revoke_connect_key", "WARNING revoked for this run only: not saved, the key returns after a restart unless revoked again", prefix), CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The key is refused from now on, but the revocation could not be saved to cockpit.json, so it would work again after a restart. Revoke it again once the configuration can be written.",
                    exception);
            }

            _logger.LogInformation("Revoked connect key {Prefix}.", prefix);
            await _audit.RecordAsync(new NodeAccessAuditEntry(now, revokedBy.Credential, revokedBy.KeyPrefix, revokedBy.RemoteAddress, "revoke_connect_key", "revoked", prefix), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // AC-1367: false when no live key carries this prefix. Saved before it counts, like an issue; the next request
    // is authenticated against the replaced record, so an open client is held to the new scope from its next call.
    public async Task<bool> UpdateScopeAsync(string prefix, ConnectKeyScope scope, NodeCaller updatedBy, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ConnectKey> next;
            lock (_gate)
            {
                if (_AllKeys().FirstOrDefault(key => key.RevokedAt is null && string.Equals(key.Prefix, prefix, StringComparison.Ordinal)) is not { } target)
                {
                    return false;
                }

                if (target.IsBootstrap)
                {
                    throw new InvalidOperationException("The bootstrap key keeps its full scope: it is for first setup only. Issue your own key with the scope you want and revoke the bootstrap key.");
                }

                next = [.. _persisted.Select(key => ReferenceEquals(key, target) ? key with { Scope = scope } : key)];
            }

            await _SaveAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _persisted = next;
            }

            _logger.LogInformation("Changed the scope of connect key {Prefix}.", prefix);
            await _audit.RecordAsync(new NodeAccessAuditEntry(_time.GetUtcNow(), updatedBy.Credential, updatedBy.KeyPrefix, updatedBy.RemoteAddress, "set_connect_key_scope", "scope changed", prefix), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<(ConnectKey Key, DateTimeOffset? LastUsedAt)>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            return [.. _AllKeys().Select(key => (key, _lastUsed.TryGetValue(key.Prefix, out var at) ? at : (DateTimeOffset?)null))];
        }
    }

    // Every stored hash is compared, match or not: an early exit would time where in the list a key sits.
    private ConnectKey? _Find(string token)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        ConnectKey? found = null;
        foreach (var key in _AllKeys())
        {
            var matches = CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(key.Hash));
            found = matches ? key : found;
        }

        return found;
    }

    private IEnumerable<ConnectKey> _AllKeys() => _bootstrap is { } bootstrap ? [bootstrap, .. _persisted] : _persisted;

    private CancellationToken _RevocationOf(string prefix)
    {
        if (!_revocations.TryGetValue(prefix, out var source))
        {
            source = new CancellationTokenSource();
            _revocations[prefix] = source;
        }

        return source.Token;
    }

    // When this failure starts a lockout, until when; otherwise null.
    private DateTimeOffset? _RecordFailure(string bucket, DateTimeOffset now)
    {
        if (!_addresses.TryGetValue(bucket, out var state))
        {
            _MakeRoom(now);
            state = new _AddressState();
            _addresses[bucket] = state;
        }

        state.Failures.RemoveAll(at => at <= now - _policy.FailureWindow);
        state.Failures.Add(now);
        if (state.Failures.Count < _policy.FailuresBeforeLockout)
        {
            return null;
        }

        state.Lockouts++;
        var ticks = Math.Min(_policy.FirstLockout.Ticks * Math.Pow(2, state.Lockouts - 1), _policy.MaxLockout.Ticks);
        state.LockedUntil = now + TimeSpan.FromTicks((long)ticks);
        state.Failures.Clear();
        _logger.LogWarning("Locked {RemoteAddress} out of the node endpoint until {LockedUntil} after repeated failed attempts.", bucket, state.LockedUntil);
        return state.LockedUntil;
    }

    // Only when a new address would pass the cap, so an ordinary failure costs no scan. Quiet addresses go first —
    // no failure in the window, no lockout for a whole max lockout, so the doubling survives between lockouts — and
    // then the least recently active, until there is room.
    private void _MakeRoom(DateTimeOffset now)
    {
        if (_addresses.Count < _policy.MaxTrackedAddresses)
        {
            return;
        }

        foreach (var quiet in _addresses.Where(pair => pair.Value.LockedUntil <= now - _policy.MaxLockout && pair.Value.Failures.All(at => at <= now - _policy.FailureWindow)).Select(pair => pair.Key).ToList())
        {
            _addresses.Remove(quiet);
        }

        while (_addresses.Count >= _policy.MaxTrackedAddresses && _addresses.Count > 0)
        {
            _addresses.Remove(_addresses.MinBy(pair => pair.Value.LastActive).Key);
        }
    }

    // IPv6 hands one holder a whole /64, so the lockout counts the /64 and not each address in it.
    private static string _BucketOf(string remoteAddress)
    {
        if (!IPAddress.TryParse(remoteAddress, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return remoteAddress;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private async Task<ConnectKey?> _LoadBootstrapAsync(List<ConnectKey> persisted, CancellationToken cancellationToken)
    {
        string? raw = null;
        if (_environment(BootstrapFileVariable) is { Length: > 0 } path)
        {
            try
            {
                raw = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            }
            // Anything, a malformed path included: a bootstrap key that cannot be read is no key, not a door that
            // throws on every request.
            catch (Exception exception)
            {
                _logger.LogError(exception, "The bootstrap connect key file named by {Variable} could not be read.", BootstrapFileVariable);
                return null;
            }
        }
        else if (_environment(BootstrapVariable) is { Length: > 0 } value)
        {
            raw = value.Trim();
            _logger.LogWarning("The bootstrap connect key came from {Variable}; prefer {FileVariable}, which keeps it out of the process environment.", BootstrapVariable, BootstrapFileVariable);
        }

        if (raw is null)
        {
            return null;
        }

        if (!raw.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || raw.Length < KeyPrefix.Length + MinimumBootstrapLength
            || raw[KeyPrefix.Length..].Distinct().Count() < MinimumBootstrapDistinctCharacters)
        {
            _logger.LogError(
                "The bootstrap connect key was ignored: it must start with {KeyPrefix} followed by at least {MinimumLength} random characters, at least {MinimumDistinct} of them different — 32 random bytes in base64url qualify.",
                KeyPrefix,
                MinimumBootstrapLength,
                MinimumBootstrapDistinctCharacters);
            return null;
        }

        var hash = _Hash(raw);
        var prefix = _PrefixOf(raw);
        if (persisted.Any(key => string.Equals(key.Hash, hash, StringComparison.Ordinal)))
        {
            _logger.LogWarning("The bootstrap connect key {Prefix} was revoked earlier and stays revoked; supply a new one to connect with a bootstrap key.", prefix);
            return null;
        }

        // A prefix is how a key is revoked, so two keys sharing one would make revoking either ambiguous.
        if (persisted.Any(key => string.Equals(key.Prefix, prefix, StringComparison.Ordinal)))
        {
            _logger.LogError("The bootstrap connect key was ignored: its prefix {Prefix} already belongs to a stored key. Supply a different one.", prefix);
            return null;
        }

        _logger.LogInformation("Bootstrap connect key {Prefix} is active.", prefix);
        return new ConnectKey(prefix, hash, ConnectKeyCapability.Admin, "bootstrap", _time.GetUtcNow(), ExpiresAt: null, IsBootstrap: true, Scope: ConnectKeyScope.Everything);
    }

    private Task _SaveAsync(List<ConnectKey> keys, CancellationToken cancellationToken) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.NodeConnectKeys ??= new NodeConnectKeysEntry();
                file.NodeConnectKeys.Keys = [.. keys];
            },
            cancellationToken);

    private bool _IsWellFormed(ConnectKey key)
    {
        if (key.Hash.Length == 64 && key.Hash.All(char.IsAsciiHexDigit))
        {
            return true;
        }

        _logger.LogWarning("Ignored stored connect key {Prefix}: its hash is not a SHA-256.", key.Prefix);
        return false;
    }

    private static string _PrefixOf(string key) => key.Substring(KeyPrefix.Length, PrefixLength);

    private static string _Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // Over the SHA-256 of each side, so the comparison runs on 32 bytes whatever either length is — comparing the raw
    // strings returns early on a length mismatch and tells a caller how long the secret is.
    internal static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private sealed class _AddressState
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public int Lockouts { get; set; }

        public DateTimeOffset LockedUntil { get; set; }

        public int RefusedWhileLockedOut { get; set; }

        public DateTimeOffset LastActive => Failures.Count > 0 && Failures[^1] > LockedUntil ? Failures[^1] : LockedUntil;
    }
}

// AC-1351: who came in over the node listener — a connect key (`KeyPrefix` set) or the pairing secret (null).
// Stamped next to `NodeCallerIdentity.PaneId`, which stays the one identity the node tools check.
// AC-1367: `Scope` is the key's, snapshotted with the request; a pairing has none and answers from its broker.
internal sealed record NodeCaller(string? KeyPrefix, string Label, ConnectKeyCapability Capability, string RemoteAddress, CancellationToken Revoked, bool HoldsAssistant = false, ConnectKeyScope? Scope = null)
{
    public bool ByConnectKey => KeyPrefix is not null;

    // AC-1367: the one scope check for every node tool and API route. A pairing keeps the bypass and permission
    // grants it always had (AC-1318/AC-1323); its profiles and projects are the broker's live grant.
    public bool AllowsProfile(string profileLabel, INodePairingBroker pairing) =>
        ByConnectKey ? _KeyScope.AllowsProfile(profileLabel) : pairing.IsProfileAllowed(profileLabel);

    public bool AllowsProject(string projectId, INodePairingBroker pairing) =>
        ByConnectKey ? _KeyScope.AllowsProject(projectId) : pairing.IsProjectAllowed(projectId);

    // Seeing a session is reaching it (stop, prompt, transcript, permission). A key sees it only inside its projects,
    // and a session without a project only with every project; a pairing's reach stays profile-only (AC-795).
    public bool AllowsSession(string profileLabel, string? projectId, INodePairingBroker pairing) =>
        AllowsProfile(profileLabel, pairing)
        && (!ByConnectKey || (projectId is null ? _KeyScope.AllowAllProjects : _KeyScope.AllowsProject(projectId)));

    public bool MayStartBypass => !ByConnectKey || _KeyScope.MayStartBypassProfiles;

    public bool MayAnswerPermissions => !ByConnectKey || _KeyScope.MayAnswerPermissions;

    private ConnectKeyScope _KeyScope => Scope ?? ConnectKeyScope.Default;

    public string Credential => ByConnectKey ? "connect key" : "pairing";

    public static NodeCaller ForPairing(string remoteAddress) =>
        new(null, "", ConnectKeyCapability.Operate, remoteAddress, CancellationToken.None, HoldsAssistant: true);
}
