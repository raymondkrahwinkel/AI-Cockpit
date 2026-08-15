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
    private readonly NodeSharedSecret _liveSecret = new();

    private NodeEndpointSettingsStore _Store() => new(_configPath);

    private NodePairingBroker _Broker(NodeEndpointSettingsStore? store = null) =>
        new(store ?? _Store(), new NodeSelfSignedCertificate(_certificatePath), _liveSecret, [], _time);

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

    [Fact]
    public async Task Claim_AfterConfirming_IsNotDefeatedByTheWindowClosing()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // The two minutes are on the operator answering, not on the controller collecting. Expiring here would
        // leave the node paired with a controller that can never hold the credential it was granted.
        _time.Advance(NodePairingBroker.Lifetime + TimeSpan.FromSeconds(1));

        var grant = await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        Assert.NotEqual("", grant.SharedSecret);
    }

    [Fact]
    public async Task Request_WhileAlreadyPaired_IsRefusedAndNamesTheIncumbent()
    {
        var store = _Store();
        var broker = _Broker(store);
        var first = await broker.RequestAsync("Raymond's desktop", "192.168.1.5");
        await broker.ConfirmAsync(first.PairingId);
        await broker.ClaimAsync(first.PairingId, first.ClaimToken);

        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => broker.RequestAsync("a stranger", "192.168.1.9"));

        // Criterion 3, and open point 3's answer: refuse, but say who has it — otherwise this is indistinguishable
        // from a credential problem and the caller retries the wrong thing forever.
        Assert.Equal(NodePairingError.AlreadyPaired, refusal.Error);
        Assert.Contains("Raymond's desktop", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("192.168.1.5", refusal.Message, StringComparison.Ordinal);
    }

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
    public async Task Request_AfterTheOperatorRefuses_IsAcceptedAgain()
    {
        var broker = _Broker();
        var first = await broker.RequestAsync("desk", "192.168.1.5");
        broker.Refuse(first.PairingId);

        // Refusing must not wedge the node for two minutes — the operator who pressed Refuse is often about to
        // retry with the right machine.
        var second = await broker.RequestAsync("desk", "192.168.1.5");
        Assert.NotEqual(first.PairingId, second.PairingId);
    }

    [Fact]
    public async Task Unpair_InvalidatesTheSharedSecretTheControllerWasGranted()
    {
        var store = _Store();
        var broker = _Broker(store);
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        var grant = await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        await broker.UnpairAsync();

        // Criterion 5: this is the value `McpAuthMiddleware` accepts on the node listener, so clearing it is the
        // revocation. Merely forgetting the pairing would leave the controller able to call in.
        var settings = await store.LoadAsync();
        Assert.Equal("", settings.SharedSecret);
        Assert.NotEqual(grant.SharedSecret, settings.SharedSecret);
        Assert.Null(settings.Pairing);
        Assert.Null(broker.Pairing);
    }

    [Fact]
    public async Task Confirm_MakesTheRunningListenerAcceptTheNewKeyWithoutARestart()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");

        Assert.Null(_liveSecret.Value);

        await broker.ConfirmAsync(offer.PairingId);
        var grant = await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        // `McpAuthMiddleware` reads this holder per request. Persisting the new secret without setting it here
        // would leave the freshly paired controller turned away until the node next started — while the screen
        // said the pairing had succeeded.
        Assert.Equal(grant.SharedSecret, _liveSecret.Value);
    }

    [Fact]
    public async Task Unpair_StopsTheRunningListenerAcceptingTheKeyWithoutARestart()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        await broker.UnpairAsync();

        // The serious half of the same defect: revocation that waits for a restart is not revocation. Until this
        // is null the unpaired controller still has full node access.
        Assert.Null(_liveSecret.Value);
    }

    [Fact]
    public async Task Confirm_TakesTheRequestOffTheOperatorsScreen_SoRefuseCannotUndoIt()
    {
        var store = _Store();
        var broker = _Broker(store);
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // Nothing left for the operator to press — the screen keys on this, so a Refuse button that lingered here
        // could mark a pairing refused whose secret is already minted and rotated into place.
        Assert.Null(broker.Pending);

        broker.Refuse(offer.PairingId);

        // And if it is pressed anyway, it changes nothing: the pairing stands and the controller can still claim.
        Assert.NotNull(broker.Pairing);
        var grant = await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        Assert.Equal((await store.LoadAsync()).SharedSecret, grant.SharedSecret);
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

    [Fact]
    public async Task Confirm_RecordsThePairingSoARestartStillKnowsWhoItIs()
    {
        var store = _Store();
        var broker = _Broker(store);
        var offer = await broker.RequestAsync("Raymond's desktop", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // A second broker over the same config is the restart: the pending pairing is gone (it lives in memory on
        // purpose) but the coupling it created is not.
        var afterRestart = _Broker(new NodeEndpointSettingsStore(_configPath));
        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => afterRestart.RequestAsync("a stranger", "192.168.1.9"));

        Assert.Equal(NodePairingError.AlreadyPaired, refusal.Error);
        Assert.Null(afterRestart.Pending);
    }

    [Fact]
    public async Task Confirm_MintsAFreshSecretRatherThanReusingTheHandCopiedOne()
    {
        var store = _Store();
        await store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "the-hand-copied-one" });

        var broker = _Broker(store);
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);

        // Pairing is a new coupling, so it gets a new credential — otherwise unpairing later would leave whoever
        // had read the old key off the Security tab still holding a working one.
        var settings = await store.LoadAsync();
        Assert.NotEqual("the-hand-copied-one", settings.SharedSecret);
        Assert.True(settings.Enabled);
    }

    // ── AC-794, scope ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsProfileAllowed_WhileUnpaired_IsFalse_ThereIsNothingToCheckAgainst()
    {
        var broker = _Broker();

        Assert.False(broker.IsProfileAllowed("default"));
        Assert.False(broker.IsProjectAllowed("proj-1"));
    }

    [Fact]
    public async Task IsProfileAllowed_OnAFreshPairing_IsFalseUntilTheOperatorGrantsSomething()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        // Criterion 2: a coupling grants nothing until the operator ticks something, even though it is now a real,
        // authenticated pairing that could reach a node-facing tool.
        Assert.False(broker.IsProfileAllowed("default"));
        Assert.False(broker.IsProjectAllowed("proj-1"));
    }

    [Fact]
    public async Task SetScope_GrantsExactlyWhatWasListed_NothingElse()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        await broker.SetScopeAsync(["default"], ["proj-1"]);

        Assert.True(broker.IsProfileAllowed("default"));
        Assert.True(broker.IsProjectAllowed("proj-1"));
        // Criterion 5's sibling: a grant is a named list, not "everything past this point" — a second profile that
        // was never ticked stays refused.
        Assert.False(broker.IsProfileAllowed("work"));
        Assert.False(broker.IsProjectAllowed("proj-2"));
    }

    [Fact]
    public async Task SetScope_Persists_SoARestartRemembersTheGrant()
    {
        var store = _Store();
        var broker = _Broker(store);
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        await broker.SetScopeAsync(["default"], ["proj-1"]);

        var afterRestart = _Broker(new NodeEndpointSettingsStore(_configPath));
        await afterRestart.EnsureLoadedAsync();

        Assert.True(afterRestart.IsProfileAllowed("default"));
        Assert.True(afterRestart.IsProjectAllowed("proj-1"));
    }

    [Fact]
    public async Task SetScope_WhileUnpaired_IsANoOp_ThereIsNoPairingToAttachAGrantTo()
    {
        var store = _Store();
        var broker = _Broker(store);

        await broker.SetScopeAsync(["default"], ["proj-1"]);

        Assert.False(broker.IsProfileAllowed("default"));
        Assert.Null((await store.LoadAsync()).Pairing);
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
        await broker.SetScopeAsync(["default", "work"], ["proj-1"]);
        Assert.True(broker.IsProfileAllowed("work"));

        await broker.SetScopeAsync(["default"], ["proj-1"]);

        Assert.False(broker.IsProfileAllowed("work"));
        Assert.True(broker.IsProfileAllowed("default"));
        Assert.NotNull(broker.Pairing);
    }

    [Fact]
    public async Task SetScope_TwoCallsInFlightAtOnce_TheSecondOneStartsFromWhatTheFirstActuallyWrote()
    {
        // Pins the contract _scopeWriteGate exists for: two calls in flight at once (the checklist's
        // fire-and-forget toggle can produce exactly this) must not interleave their read-modify-write-disk
        // sequences — the second one only starts once the first has fully finished, so the result is always
        // `second`'s target. A fast local disk makes the unguarded version of this pass too, most of the time (the
        // two calls rarely genuinely overlap without an artificial stall on the store) — the write-gate's own
        // comment carries the reasoning for why the sequencing still matters.
        var store = _Store();
        var broker = _Broker(store);
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);

        var first = broker.SetScopeAsync(["default"], []);
        var second = broker.SetScopeAsync(["default", "work"], ["proj-1"]);
        await Task.WhenAll(first, second);

        Assert.True(broker.IsProfileAllowed("work"));
        Assert.True(broker.IsProjectAllowed("proj-1"));
        var onDisk = (await store.LoadAsync()).Pairing!;
        Assert.Contains("work", onDisk.AllowedProfileLabels);
        Assert.Contains("proj-1", onDisk.AllowedProjectIds);
    }

    [Fact]
    public async Task Unpair_ClearsTheScopeGrant_SoARePairingStartsAtNothingAgain()
    {
        var broker = _Broker();
        var offer = await broker.RequestAsync("desk", "192.168.1.5");
        await broker.ConfirmAsync(offer.PairingId);
        await broker.ClaimAsync(offer.PairingId, offer.ClaimToken);
        await broker.SetScopeAsync(["default"], ["proj-1"]);
        Assert.True(broker.IsProfileAllowed("default"));

        await broker.UnpairAsync();

        Assert.False(broker.IsProfileAllowed("default"));
        Assert.False(broker.IsProjectAllowed("proj-1"));
    }

    // xunit's own FakeTimeProvider lives in a package this project does not take; a settable clock is four lines.
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
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
