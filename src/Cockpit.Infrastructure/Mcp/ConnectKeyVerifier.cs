using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
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

    // A bootstrap key is chosen by whoever sets up the container, so it gets a floor; an issued one is always 43.
    private const int MinimumBootstrapLength = 32;

    private readonly CockpitConfigFileAccess _configFile;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _time;
    private readonly NodeAccessAuditLog _audit;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    // ponytail: in memory and per address, so a restart forgets every lockout and an IPv6 /64 gets a budget per
    // address. Persist it, or bucket by /64, once the node faces more than LAN/Tailscale (B2).
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
        : this(CockpitConfigPath.Default, Environment.GetEnvironmentVariable, TimeProvider.System, audit, logger)
    {
    }

    // Test seam: another config file, environment and clock.
    internal ConnectKeyVerifier(string configFilePath, Func<string, string?> environment, TimeProvider time, NodeAccessAuditLog audit, ILogger logger)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _environment = environment;
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
        NodeCaller? caller = null;
        string credential;
        string? keyPrefix = null;
        string refusal;
        lock (_gate)
        {
            // Checked before the credential, so a locked-out address cannot use the answer as an oracle.
            if (_addresses.TryGetValue(remoteAddress, out var state) && state.LockedUntil > now)
            {
                credential = "not checked";
                refusal = "refused: locked out";
            }
            else
            {
                var found = token.StartsWith(KeyPrefix, StringComparison.Ordinal) ? _Find(token) : null;
                var pairing = pairingSecret is { Length: > 0 } secret && _ConstantTimeEquals(token, secret);
                if (found is { } key && key.IsUsableAt(now))
                {
                    caller = new NodeCaller(key.Prefix, key.Label, key.Capability, remoteAddress, _RevocationOf(key.Prefix));
                    _lastUsed[key.Prefix] = now;
                }
                else if (pairing)
                {
                    caller = NodeCaller.ForPairing(remoteAddress);
                }

                keyPrefix = found?.Prefix;
                credential = found is not null ? "connect key" : token.Length == 0 ? "none" : "unknown";
                refusal = found switch
                {
                    { RevokedAt: not null } => "refused: revoked key",
                    { } => "refused: expired key",
                    null => "refused: unknown credential",
                };

                // A success leaves the address's failures standing: a controller polling from behind the same NAT
                // must not wipe an attacker's count every 20 s.
                if (caller is null)
                {
                    _RecordFailure(remoteAddress, now);
                }
            }
        }

        if (caller is null)
        {
            await _audit.RecordAsync(new NodeAccessAuditEntry(now, credential, keyPrefix, remoteAddress, null, refusal), cancellationToken).ConfigureAwait(false);
        }

        return caller;
    }

    // The raw key is in the return value and nowhere else — not in the log, the audit or cockpit.json.
    public async Task<(ConnectKey Key, string Secret)> IssueAsync(string label, ConnectKeyCapability capability, int? expiresInDays, NodeCaller issuedBy, CancellationToken cancellationToken = default)
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

                key = new ConnectKey(_PrefixOf(secret), _Hash(secret), capability, label.Trim(), now, now.AddDays(days));
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

    // False when no live key carries this prefix. The key stops working in memory before the save, so a failed
    // write still revokes it for this run — the exception tells the caller it will not survive a restart.
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

            await _SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Revoked connect key {Prefix}.", prefix);
            await _audit.RecordAsync(new NodeAccessAuditEntry(now, revokedBy.Credential, revokedBy.KeyPrefix, revokedBy.RemoteAddress, "revoke_connect_key", "revoked", prefix), cancellationToken).ConfigureAwait(false);
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

    private void _RecordFailure(string remoteAddress, DateTimeOffset now)
    {
        _SweepQuietAddresses(now);

        if (!_addresses.TryGetValue(remoteAddress, out var state))
        {
            state = new _AddressState();
            _addresses[remoteAddress] = state;
        }

        state.Failures.RemoveAll(at => at <= now - _policy.FailureWindow);
        state.Failures.Add(now);
        if (state.Failures.Count < _policy.FailuresBeforeLockout)
        {
            return;
        }

        state.Lockouts++;
        var ticks = Math.Min(_policy.FirstLockout.Ticks * Math.Pow(2, state.Lockouts - 1), _policy.MaxLockout.Ticks);
        state.LockedUntil = now + TimeSpan.FromTicks((long)ticks);
        state.Failures.Clear();
        _logger.LogWarning("Locked {RemoteAddress} out of the node endpoint until {LockedUntil} after repeated failed attempts.", remoteAddress, state.LockedUntil);
    }

    // An address with no failure in the window and no lockout for a whole max lockout is forgotten, escalation
    // included — not sooner, or the doubling would reset the moment each lockout ran out.
    private void _SweepQuietAddresses(DateTimeOffset now)
    {
        foreach (var quiet in _addresses.Where(pair => pair.Value.LockedUntil <= now - _policy.MaxLockout && pair.Value.Failures.All(at => at <= now - _policy.FailureWindow)).Select(pair => pair.Key).ToList())
        {
            _addresses.Remove(quiet);
        }
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

        if (!raw.StartsWith(KeyPrefix, StringComparison.Ordinal) || raw.Length < KeyPrefix.Length + MinimumBootstrapLength)
        {
            _logger.LogError("The bootstrap connect key was ignored: it must start with {KeyPrefix} and carry at least {MinimumLength} characters after it.", KeyPrefix, MinimumBootstrapLength);
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
        return new ConnectKey(prefix, hash, ConnectKeyCapability.Admin, "bootstrap", _time.GetUtcNow(), ExpiresAt: null, IsBootstrap: true);
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

    private static bool _ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private sealed class _AddressState
    {
        public List<DateTimeOffset> Failures { get; } = [];

        public int Lockouts { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}

// AC-1351: who came in over the node listener — a connect key (`KeyPrefix` set) or the pairing secret (null).
// Stamped next to `NodeCallerIdentity.PaneId`, which stays the one identity the node tools check.
internal sealed record NodeCaller(string? KeyPrefix, string Label, ConnectKeyCapability Capability, string RemoteAddress, CancellationToken Revoked)
{
    public bool ByConnectKey => KeyPrefix is not null;

    public string Credential => ByConnectKey ? "connect key" : "pairing";

    public static NodeCaller ForPairing(string remoteAddress) =>
        new(null, "", ConnectKeyCapability.Operate, remoteAddress, CancellationToken.None);
}
