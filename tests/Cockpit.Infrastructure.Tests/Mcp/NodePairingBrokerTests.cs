using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// What the node will and will not agree to (AC-792). Every refusal in the pairing handshake is decided here, so
/// this is where criteria 2 (expired told apart from spent), 3 (a second controller refused, with the incumbent
/// named), 4 (an overheard code is not enough) and 5 (unpairing invalidates the key) are proved — without a socket,
/// which is what makes them provable at all rather than timing-dependent.
/// </summary>
public class NodePairingBrokerTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"node-pairing-{Guid.NewGuid():N}.json");
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"node-pairing-{Guid.NewGuid():N}.pfx");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

    // The value the node listener accepts right now — the holder `McpAuthMiddleware` reads per request.

    private NodeEndpointSettingsStore _Store() => new(_configPath);

    private NodePairingBroker _Broker(NodeEndpointSettingsStore? store = null, IMcpServerStore? servers = null) =>
        new(store ?? _Store(), new NodeSelfSignedCertificate(_certificatePath), [], servers, _time);

    [Fact]
    public async Task Claim_BeforeTheOperatorConfirms_IsPendingAndGrantsNothing()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");

        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.ClaimAsync(offer.PairingId, offer.ClaimToken));

        Assert.Equal(NodePairingError.Pending, refusal.Error);
        // The whole point: a request on its own leaves the node exactly as it was. No secret exists to steal yet.
        Assert.Equal("", (await _Store().LoadAsync()).SharedSecret);
    }

    [Fact]
    public async Task Claim_WithTheWrongToken_IsRefused_SoAnOverheardCodeIsNotEnough()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // Criterion 4, in the failure direction: this caller knows the pairing id — and by extension could have
        // read the six digits off the screen — but was never on the connection the claim token came back over.
        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.ClaimAsync(offer.PairingId, "0123456789ABCDEF"));

        Assert.Equal(NodePairingError.InvalidToken, refusal.Error);

        // And the real controller is unharmed by the attempt: its own claim still works.
        var grant = await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        Assert.NotEqual("", grant.SharedSecret);
    }

    [Fact]
    public async Task Claim_Twice_SaysAlreadyUsed_NotExpired()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.ClaimAsync(offer.PairingId, offer.ClaimToken));

        // Criterion 2: "somebody already used this" is a different event from "you were too slow", and only the
        // first is worth alarm — so they must not collapse into one code.
        Assert.Equal(NodePairingError.AlreadyUsed, refusal.Error);
        Assert.NotEqual(NodePairingError.Expired, refusal.Error);
    }

    [Fact]
    public async Task Claim_AfterTheWindowClosesWithoutAConfirm_SaysExpired()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");

        _time.Advance(NodePairingBroker.Lifetime + TimeSpan.FromSeconds(1));

        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.ClaimAsync(offer.PairingId, offer.ClaimToken));
        Assert.Equal(NodePairingError.Expired, refusal.Error);
    }



    /// <summary>
    /// AC-1325: no chains. A cockpit that already controls a node of its own refuses a pairing request naming that
    /// node, and its counter-proof in the same row set — no controlled node — is today's existing behaviour: the
    /// request becomes <c>Pending</c> exactly as before this ticket.
    /// </summary>

    [Fact]
    public async Task Request_WhileAnotherIsWaitingForTheOperator_IsRefused()
    {
        var broker = _Broker();
        await broker.RequestAsync("desk", "192.168.1.5");

        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.RequestAsync("a stranger", "192.168.1.9"));

        // A second request must not silently replace the one on screen: that would let an unauthenticated caller
        // cancel somebody else's pairing.
        Assert.Equal(NodePairingError.PairingInProgress, refusal.Error);
    }




    [Fact]
    public async Task Confirm_Twice_IsRefused()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // A second confirm would mint a second secret over the first, orphaning a controller mid-claim.
        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.ConfirmAsync(offer.PairingId));
        Assert.Equal(NodePairingError.InvalidToken, refusal.Error);
    }



    // ── AC-794, scope ──────────────────────────────────────────────────────────────────────────────────────────


    /// <summary>
    /// AC-1292 criteria 1 and 2, and a deliberate reversal of AC-794's own criterion 2: confirming is still where
    /// the access comes from, but what it covers is now everything — including the profile and the project that do
    /// not exist yet, which a literal id list could never reach.
    /// </summary>


    /// <summary>
    /// AC-1292 criterion 3: the narrow stand still does exactly what it did, and the two kinds are separate
    /// questions — every project with a two-name profile list is an answer the operator is allowed to give.
    /// </summary>
    [Fact]
    public async Task SetScope_AllProjectsBesideANarrowProfileList_KeepsTheTwoKindsApart()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        await broker.SetScopeAsync(["default", "work"], [], allowAllProfiles: false, allowAllProjects: true);

        Assert.True(broker.IsProfileAllowed("default"));
        Assert.False(broker.IsProfileAllowed("olaf"));
        Assert.True(broker.IsProjectAllowed("a-project-made-tomorrow"));
    }



    [Fact]
    public async Task SetScope_NarrowedWhileTheListenerIsLive_TakesEffectAtOnce_NotOnTheNextClaim()
    {
        // Criterion 6: revoking a scope grant is not the same act as unpairing, and must not need one — the
        // coupling itself stays intact while the check that gates a call reads the narrower list immediately.
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        await broker.SetScopeAsync(["default", "work"], ["proj-1"], false, false);
        Assert.True(broker.IsProfileAllowed("work"));

        await broker.SetScopeAsync(["default"], ["proj-1"], false, false);

        Assert.False(broker.IsProfileAllowed("work"));
        Assert.True(broker.IsProfileAllowed("default"));
        Assert.NotNull(broker.Pairing);
    }



    private sealed class _FakeMcpServerStore(IReadOnlyList<McpServerConfig> servers) : IMcpServerStore
    {
        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(servers);

        public Task SaveAsync(IReadOnlyList<McpServerConfig> value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test double is read-only — the guard under test only lists nodes.");
    }

    // xunit's own FakeTimeProvider lives in a package this project does not take; a settable clock is four lines.
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TimerCallback Callback, object? State)> _alarms = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        // The base class's timers run on the real clock, which would leave the expiry event (AC-1291) only
        // provable by waiting two minutes.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _alarms.Add((_now + dueTime, callback, state));

            return new _InertTimer();
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var alarm in _alarms.Where(alarm => alarm.Due <= _now).ToList())
            {
                _alarms.Remove(alarm);
                alarm.Callback(alarm.State);
            }
        }

        private sealed class _InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var path in new[] { _configPath, _certificatePath })
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}
