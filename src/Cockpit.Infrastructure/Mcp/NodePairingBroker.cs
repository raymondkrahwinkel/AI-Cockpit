using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// The node half of the pairing handshake (AC-792): every rule about whether a pairing may happen, in one place
// with no socket in sight. `NodePairingHost` in front of it only maps these outcomes onto status codes, and the
// Security tab only shows what is here — so criteria 2, 3 and 4 are all testable without a network.
//
// Three properties this arrangement is built to hold:
//
// *An overheard code grants nothing.* The six digits are for the human; the machine's authority is `ClaimToken`,
// 256 bits handed back only in the response to the request that created the pairing. Whoever reads the code off a
// screen still cannot claim, because they were never on that connection (criterion 4).
//
// *Confirming is the operator's, and it happens before there is anything to steal.* No shared secret exists until
// `ConfirmAsync`. A request that is never confirmed leaves the node exactly as it found it.
//
// *One controller, and the refusal says so.* The epic fixes a node to one controller (AC-742), and AC-791's
// authorization model leans on it — one identity, no per-controller grants. So a second request is refused rather
// than offered as a choice, and the refusal names the incumbent, because "you already have a controller" and "your
// token is wrong" are different problems and a caller that cannot tell them apart will retry the wrong one
// forever (open point 3).
//
// A pending pairing lives in memory only, deliberately: a two-minute window that survived a restart would be a
// two-minute window that quietly became indefinite.
internal sealed class NodePairingBroker : INodePairingBroker, ISingletonService
{
    // Open point 1: two minutes, and single-use once claimed. The token guards a handshake the operator is
    // performing at both machines right now, not a session — a longer life only widens the window in which a
    // leaked offer is still worth something, and nobody standing at two screens needs more.
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly INodeEndpointSettingsStore _settings;
    private readonly NodeSelfSignedCertificate _certificate;
    private readonly NodeSharedSecret _liveSecret;
    private readonly IEnumerable<ICockpitInternalMcpProvider> _endpointHosts;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    // AC-794: serializes SetScopeAsync's disk round-trip. `_gate` cannot do this — it is a sync lock and the
    // round-trip awaits — so without a second gate here, two toggles in quick succession (the checklist calls this
    // once per row flipped, fire-and-forget) could run their read-modify-write-disk sequences interleaved, and
    // whichever call's write lands last on disk would win regardless of which call the operator made last: an
    // earlier, already-superseded grant silently overwriting a newer one. A `SemaphoreSlim` makes the two calls
    // run one at a time instead, so the second one always starts from what the first one actually wrote.
    private readonly SemaphoreSlim _scopeWriteGate = new(1, 1);

    private PendingPairing? _pending;
    private NodePairing? _pairing;
    private bool _loaded;

    public NodePairingBroker(
        INodeEndpointSettingsStore settings,
        NodeSelfSignedCertificate certificate,
        NodeSharedSecret liveSecret,
        IEnumerable<ICockpitInternalMcpProvider> endpointHosts)
        : this(settings, certificate, liveSecret, endpointHosts, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so expiry is provable without waiting two minutes for it.
    internal NodePairingBroker(
        INodeEndpointSettingsStore settings,
        NodeSelfSignedCertificate certificate,
        NodeSharedSecret liveSecret,
        IEnumerable<ICockpitInternalMcpProvider> endpointHosts,
        TimeProvider time)
    {
        _settings = settings;
        _certificate = certificate;
        _liveSecret = liveSecret;
        _endpointHosts = endpointHosts;
        _time = time;
    }

    public event EventHandler? Changed;

    public NodePairing? Pairing
    {
        get
        {
            lock (_gate)
            {
                return _pairing;
            }
        }
    }

    public NodePairingPending? Pending
    {
        get
        {
            lock (_gate)
            {
                // Reads as absent the moment it expires rather than lingering until something sweeps it: the
                // Security tab and the claim path must agree on whether there is anything there, and a screen
                // still offering Confirm for a pairing the claim would refuse is worse than an empty one.
                return _LivePending() is { } pending ? pending.ToDomain() : null;
            }
        }
    }

    public async Task<NodePairingOffer> RequestAsync(string controllerName, string controllerAddress, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        // Outside the lock: the fingerprint may have to read (or mint) the certificate file, and that is not work
        // to do while holding a lock every property getter takes.
        var fingerprint = _certificate.Fingerprint;
        var name = string.IsNullOrWhiteSpace(controllerName) ? "an unnamed cockpit" : controllerName.Trim();

        lock (_gate)
        {
            if (_pairing is { } existing)
            {
                throw NodePairingException.For(
                    NodePairingError.AlreadyPaired,
                    $"This cockpit is already paired with \"{existing.ControllerName}\" ({existing.ControllerAddress}). Unpair there or here first.");
            }

            if (_LivePending() is { } inFlight)
            {
                throw NodePairingException.For(
                    NodePairingError.PairingInProgress,
                    $"Another pairing request from {inFlight.ControllerAddress} is waiting for this cockpit's operator. Try again once it is answered.");
            }

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pending = new PendingPairing
            {
                PairingId = Guid.NewGuid().ToString("N"),
                ClaimToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Nonce = nonce,
                ControllerName = name,
                ControllerAddress = controllerAddress,
                Code = NodePairingCode.Derive(nonce, fingerprint),
                ExpiresAtUtc = _time.GetUtcNow() + Lifetime,
            };

            _pending = pending;
            _RaiseChanged();

            return new NodePairingOffer(pending.PairingId, pending.ClaimToken, pending.Nonce, Environment.MachineName, pending.ExpiresAtUtc);
        }
    }

    public async Task ConfirmAsync(string pairingId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        NodePairing pairing;
        PendingPairing confirmed;
        lock (_gate)
        {
            if (_LivePending() is not { } pending || !string.Equals(pending.PairingId, pairingId, StringComparison.Ordinal))
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "That pairing is no longer waiting for an answer.");
            }

            pending.Confirmed = true;
            confirmed = pending;
            pairing = new NodePairing
            {
                ControllerName = pending.ControllerName,
                ControllerAddress = pending.ControllerAddress,
                PairedAtUtc = _time.GetUtcNow(),
            };
        }

        // A fresh secret per pairing, minted here rather than at claim time so that confirming is the single
        // moment the coupling comes into being — and so a claim that never arrives still leaves a node whose old
        // credential is gone, which is the safer of the two failures.
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var current = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _settings.SaveAsync(current with { SharedSecret = secret, Pairing = pairing }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _pairing = pairing;

            // The exact object confirmed, not "whatever is pending now": the store write above released the lock,
            // and handing this secret to a pairing that replaced it in that window would be a grant nobody
            // confirmed. `_pairing` being set makes a replacement impossible today — this keeps it impossible if
            // that ever stops being true.
            if (ReferenceEquals(_pending, confirmed))
            {
                confirmed.GrantedSecret = secret;
            }
        }

        // The listener starts accepting the new credential now, not at the next launch.
        _liveSecret.Set(secret);

        _RaiseChanged();
    }

    public void Refuse(string pairingId)
    {
        lock (_gate)
        {
            // `_LivePending`, not `_pending`: a pairing the operator already confirmed is past refusing — see
            // that method for what refusing one after the fact would leave behind.
            if (_LivePending() is not { } pending || !string.Equals(pending.PairingId, pairingId, StringComparison.Ordinal))
            {
                return;
            }

            pending.Refused = true;
        }

        _RaiseChanged();
    }

    public async Task<NodePairingGrant> ClaimAsync(string pairingId, string claimToken, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        string secret;
        lock (_gate)
        {
            var pending = _pending;
            if (pending is null || !string.Equals(pending.PairingId, pairingId, StringComparison.Ordinal) || !_TokenMatches(pending.ClaimToken, claimToken))
            {
                // One answer for "no such pairing" and "wrong token", so this never becomes a way to learn which
                // pairing ids exist — the same reasoning `McpAuthMiddleware` gives for its generic 401.
                throw NodePairingException.For(NodePairingError.InvalidToken, "That pairing token is not valid for this cockpit.");
            }

            if (pending.Claimed)
            {
                // Distinct from expiry (criterion 2): being too slow is a nuisance, but a token being presented a
                // second time means somebody else has it, and that is worth reading differently.
                throw NodePairingException.For(NodePairingError.AlreadyUsed, "That pairing token has already been used. Start a new pairing.");
            }

            if (pending.Refused)
            {
                throw NodePairingException.For(NodePairingError.Refused, "This cockpit's operator refused the pairing.");
            }

            if (pending.GrantedSecret is null)
            {
                // The two-minute window is on the operator answering, not on the controller collecting: once
                // Confirm is pressed the secret exists and the pairing is recorded, so refusing the claim that
                // follows would leave this node holding a coupling its controller can never use.
                if (_IsExpired(pending))
                {
                    throw NodePairingException.For(NodePairingError.Expired, "That pairing token has expired. Start a new pairing.");
                }

                // Not an error: the controller is meant to keep asking. Raised as one so every non-grant leaves by
                // the same door and the host has a single place to map codes onto statuses.
                throw NodePairingException.For(NodePairingError.Pending, "Waiting for this cockpit's operator to confirm the code.");
            }

            secret = pending.GrantedSecret;
            pending.Claimed = true;
        }

        _RaiseChanged();

        return new NodePairingGrant(secret, [.. _endpointHosts.SelectMany(host => host.GetNodeAddresses())]);
    }

    public async Task UnpairAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var current = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The secret goes with the pairing, which is what makes this a revocation rather than a bookkeeping edit:
        // `McpAuthMiddleware` accepts exactly this value on the node listener, so clearing it is the moment the
        // controller stops being able to call in (criterion 5). Nothing is rotated *into* its place — an unpaired
        // node has no credential to hand anybody.
        await _settings.SaveAsync(current with { SharedSecret = "", Pairing = null }, cancellationToken).ConfigureAwait(false);

        // And the running listener stops accepting it in the same act. Clearing only the stored copy would leave
        // the controller with full access until this node next started, which is not what "unpair" says.
        _liveSecret.Set(null);

        lock (_gate)
        {
            _pairing = null;
            _pending = null;
        }

        _RaiseChanged();
    }

    // AC-794: read straight off `_pairing`, not the store — it is already the live, always-current copy every
    // mutation below updates in the same act it writes to disk (see `SetScopeAsync`, `UnpairAsync`), so there is
    // nothing a second holder like `NodeSharedSecret` would buy here. No pairing means no scope, not "everything":
    // an unpaired node has nothing to check this against, the same posture `NodeSharedSecret.Value` being null takes.
    public bool IsProfileAllowed(string profileLabel)
    {
        lock (_gate)
        {
            return _pairing is { } pairing && pairing.AllowedProfileLabels.Contains(profileLabel, StringComparer.Ordinal);
        }
    }

    public bool IsProjectAllowed(string projectId)
    {
        lock (_gate)
        {
            return _pairing is { } pairing && pairing.AllowedProjectIds.Contains(projectId, StringComparer.Ordinal);
        }
    }

    public async Task SetScopeAsync(IReadOnlyList<string> allowedProfileLabels, IReadOnlyList<string> allowedProjectIds, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _scopeWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NodePairing updated;
            lock (_gate)
            {
                // Nothing to attach a grant to — the Security tab only shows this control while paired, so
                // reaching here unpaired would be a caller bug rather than a real toggle; a silent no-op is
                // simpler than an exception neither caller needs to handle.
                if (_pairing is not { } pairing)
                {
                    return;
                }

                updated = pairing with { AllowedProfileLabels = allowedProfileLabels, AllowedProjectIds = allowedProjectIds };
            }

            // Disk before memory, the same order `ConfirmAsync`/`UnpairAsync` write in: a crash between the two
            // leaves `IsProfileAllowed` answering off what is actually on disk after a restart, never ahead of it.
            var current = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            await _settings.SaveAsync(current with { Pairing = updated }, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _pairing = updated;
            }
        }
        finally
        {
            _scopeWriteGate.Release();
        }

        _RaiseChanged();
    }

    // Reads the persisted pairing into memory. Public because `Pairing` and `Pending` are synchronous properties a
    // view binds to and loading is not: without a call to this, a node that was paired before it restarted would
    // show as unpaired on its own Security tab and offer no way to unpair. Idempotent, so every entry point can
    // simply call it.
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
        }

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_loaded)
            {
                _pairing = settings.Pairing;
                _loaded = true;
            }
        }
    }

    // A pairing still waiting for this cockpit's operator — nothing more. Callers hold `_gate`.
    //
    // A confirmed one is deliberately not "live": the operator has already answered, so there is nothing left for
    // them to press. Leaving it live would keep the Refuse button on screen after Confirm, and pressing it would
    // mark a pairing refused whose secret is already minted, recorded and rotated into place — a node that reads
    // as paired while its controller can never claim, with the previous credential already destroyed. Refusing
    // after the fact is not a refusal; it is an unpair, and that is a different button.
    private PendingPairing? _LivePending() =>
        _pending is { } pending && !pending.Claimed && !pending.Refused && !pending.Confirmed && !_IsExpired(pending) ? pending : null;

    private bool _IsExpired(PendingPairing pending) => _time.GetUtcNow() >= pending.ExpiresAtUtc;

    // Constant-time for the same reason `McpAuthMiddleware` compares its bearer that way: this one is presented
    // by an unauthenticated caller over a real network, and it is the only thing standing between an overheard
    // code and the secret.
    private static bool _TokenMatches(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(presented));

    private void _RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class PendingPairing
    {
        public required string PairingId { get; init; }
        public required string ClaimToken { get; init; }
        public required string Nonce { get; init; }
        public required string ControllerName { get; init; }
        public required string ControllerAddress { get; init; }
        public required string Code { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public bool Confirmed { get; set; }
        public bool Refused { get; set; }
        public bool Claimed { get; set; }
        public string? GrantedSecret { get; set; }

        public NodePairingPending ToDomain() => new()
        {
            PairingId = PairingId,
            ControllerName = ControllerName,
            ControllerAddress = ControllerAddress,
            Code = Code,
            ExpiresAtUtc = ExpiresAtUtc,
        };
    }
}
