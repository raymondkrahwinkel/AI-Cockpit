using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// The whole handshake between two Cockpit instances, over a real TLS listener (AC-792, criterion 1). One instance
/// is a live <see cref="NodePairingHost"/> with its own certificate and config; the other is the ordinary
/// <see cref="NodePairingClient"/> the Security tab drives, given nothing but an address string. Nothing here is
/// faked below the socket, which is the only way to prove that the code both screens show is arrived at
/// independently and that the pin refuses a certificate it did not agree to.
/// </summary>
/// <remarks>
/// Two machines would be better and were not available; this runs both ends in one process over loopback, which
/// exercises every line the cross-machine case would except the network between them.
/// </remarks>
public class NodePairingHandshakeTests : IAsyncLifetime
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"node-handshake-{Guid.NewGuid():N}.json");
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"node-handshake-{Guid.NewGuid():N}.pfx");

    private readonly NodeSharedSecret _liveSecret = new();

    private NodeEndpointSettingsStore _store = null!;
    private NodeSelfSignedCertificate _certificate = null!;
    private NodeVisibilityPolicy _visibility = null!;
    private NodePairingBroker _broker = null!;
    private NodePairingHost _host = null!;
    private string _address = "";

    public async Task InitializeAsync()
    {
        _store = new NodeEndpointSettingsStore(_configPath);

        // The node's master switch is on — the host binds nothing while it is off, which is itself the AC-791
        // posture and is covered by `Start_WhileTheSwitchIsOff_BindsNothing` below.
        await _store.SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = "" });

        _certificate = new NodeSelfSignedCertificate(_certificatePath);
        // The default policy's own-range set includes loopback (AC-793) — exactly how every test here reaches
        // the host — so this needs no whitelist entry to keep passing.
        _visibility = new NodeVisibilityPolicy(_store);
        _broker = new NodePairingBroker(_store, _certificate, _liveSecret, []);
        _host = new NodePairingHost(_store, _broker, _certificate, _visibility, NullLoggerFactory.Instance);

        await _host.StartAsync(CancellationToken.None);
        Assert.NotNull(_host.BoundPort);
        _address = $"127.0.0.1:{_host.BoundPort}";
    }

    [Fact]
    public async Task BothSidesDeriveTheSameCode_WithoutEitherSendingIt()
    {
        var client = new NodePairingClient();

        var handshake = await client.BeginAsync(_address, "the controller");

        // The node computed its number from its own certificate; the client computed one from the certificate it
        // met on the wire. Equality here is the evidence that a machine in the middle — which cannot present this
        // certificate — would have produced a different number for the operator to notice.
        Assert.Equal(_broker.Pending!.Code, handshake.Code);
        Assert.Equal(NodePairingCode.Digits, handshake.Code.Length);
        Assert.Equal(_certificate.Fingerprint, handshake.Fingerprint);
    }

    [Fact]
    public async Task Pairing_GoesLiveOnlyAfterBothSidesConfirm()
    {
        var client = new NodePairingClient();
        var handshake = await client.BeginAsync(_address, "the controller");

        // The controller's operator has compared the codes; the node's has not answered yet. Nothing exists.
        Assert.Equal("", (await _store.LoadAsync()).SharedSecret);

        var claiming = client.CompleteAsync(handshake);
        await _broker.ConfirmAsync(_broker.Pending!.PairingId);
        var grant = await claiming;

        // Criterion 1: live, and live with exactly the credential the node will now accept.
        var settings = await _store.LoadAsync();
        Assert.NotEqual("", grant.SharedSecret);
        Assert.Equal(settings.SharedSecret, grant.SharedSecret);
        Assert.Equal("the controller", settings.Pairing!.ControllerName);
        Assert.Equal("127.0.0.1", settings.Pairing.ControllerAddress);
    }

    [Fact]
    public async Task Claim_WithACertificateOtherThanThePinnedOne_IsRefusedWithThatReason()
    {
        var client = new NodePairingClient();
        var handshake = await client.BeginAsync(_address, "the controller");

        // Criterion 8, in the failure direction: the same live node, the same valid claim token, but the caller is
        // pinned to a different certificate. It has to refuse — and refuse in a way that is not just "TLS failed",
        // because "that is not the machine you paired with" is not a network problem the operator should retry.
        var elsewhere = handshake with { Fingerprint = new string('A', 64) };
        await _broker.ConfirmAsync(_broker.Pending!.PairingId);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.CompleteAsync(elsewhere));

        var mismatch = Assert.IsType<NodeCertificatePinMismatchException>(failure.InnerException);
        Assert.Equal(_certificate.Fingerprint, mismatch.PresentedFingerprint);
        Assert.Equal(new string('A', 64), mismatch.ExpectedFingerprint);
    }

    [Fact]
    public async Task Request_FromASecondControllerWhileAlreadyPaired_ReachesTheClientAsARefusalItCanClassify()
    {
        var client = new NodePairingClient();
        var handshake = await client.BeginAsync(_address, "Raymond's desktop");
        var claiming = client.CompleteAsync(handshake);
        await _broker.ConfirmAsync(_broker.Pending!.PairingId);
        await claiming;

        // The refusal has to survive the wire as a code, not just a status: criterion 3 is about the far end being
        // able to tell this from a credential problem.
        var refusal = await Assert.ThrowsAsync<NodePairingException>(() => client.BeginAsync(_address, "a stranger"));

        Assert.Equal(NodePairingError.AlreadyPaired, refusal.Error);
        Assert.Contains("Raymond's desktop", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unpair_FromTheControllerSide_InvalidatesTheKeyOnTheNode()
    {
        var client = new NodePairingClient();
        var handshake = await client.BeginAsync(_address, "the controller");
        var claiming = client.CompleteAsync(handshake);
        await _broker.ConfirmAsync(_broker.Pending!.PairingId);
        var grant = await claiming;

        await client.UnpairAsync(_address, grant.SharedSecret, handshake.Fingerprint);

        // Criterion 5, the far half: unpairing reaches the same broker call the node's own screen does, so the
        // key is gone rather than merely unlisted.
        var settings = await _store.LoadAsync();
        Assert.Equal("", settings.SharedSecret);
        Assert.Null(settings.Pairing);
    }

    [Fact]
    public async Task Unpair_WithoutTheSharedSecret_IsRefused()
    {
        var client = new NodePairingClient();
        var handshake = await client.BeginAsync(_address, "the controller");
        var claiming = client.CompleteAsync(handshake);
        await _broker.ConfirmAsync(_broker.Pending!.PairingId);
        await claiming;

        // The one route here that takes a credential must actually take it: ending somebody else's coupling is not
        // something an unauthenticated caller may do.
        var refusal = await Assert.ThrowsAsync<NodePairingException>(
            () => client.UnpairAsync(_address, "not-the-secret", handshake.Fingerprint));

        Assert.Equal(NodePairingError.InvalidToken, refusal.Error);
        Assert.NotNull((await _store.LoadAsync()).Pairing);
    }

    [Fact]
    public async Task AClientWithoutThePin_CannotReachTheNodeAtAll()
    {
        // Measured, not assumed, because it settles what the pin is *for* and what it does not cover. A node signs
        // its own certificate, so a client that only knows the machine's trust store refuses the connection outright
        // — which is why pinning is the thing that makes the node reachable, not an extra guard on top of something
        // that already worked.
        //
        // The same measurement is this feature's honest limit: an agent process the cockpit spawns (the Claude CLI
        // with its own `--mcp-config`) brings its own HTTP client and cannot be handed this pin, so a paired node's
        // MCP endpoints are reachable from the cockpit's in-process tool loop and not from a spawned CLI session.
        // Closing that needs the controller to front the node itself, which is the epic's bus sub, not this one.
        using var unpinned = new HttpClient { BaseAddress = new Uri($"https://{_address}/") };

        await Assert.ThrowsAsync<HttpRequestException>(() => unpinned.PostAsync("pair/request", content: null));
    }

    [Fact]
    public async Task Start_WhileTheSwitchIsOff_BindsNothing()
    {
        var offPath = Path.Combine(Path.GetTempPath(), $"node-handshake-off-{Guid.NewGuid():N}.json");
        var offStore = new NodeEndpointSettingsStore(offPath);
        var host = new NodePairingHost(offStore, _broker, _certificate, _visibility, NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(CancellationToken.None);

            // A cockpit nobody meant as a node has no pairing port to find — the same "absent rather than refused"
            // posture AC-791 chose for internal endpoints.
            Assert.Null(host.BoundPort);
            Assert.Null(host.Address);
        }
        finally
        {
            await host.DisposeAsync();
            File.Delete(offPath);
        }
    }

    // Criterion 6: discovery (AC-793) will hand this client an address it found rather than one that was typed.
    // Both are strings arriving at the same method, and both have to normalise onto the same base address — which
    // is what makes "one handshake, two entrances" a property of the code rather than a promise in a comment.
    [Theory]
    [InlineData("192.168.1.20:7331")]
    [InlineData("https://192.168.1.20:7331")]
    [InlineData("  https://192.168.1.20:7331/  ")]
    public void AnAddressTypedOrDiscovered_NormalisesOntoTheSameEndpoint(string address) =>
        Assert.Equal(new Uri("https://192.168.1.20:7331/"), NodePairingClient.NormalizeAddress(address));

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _certificate.Dispose();

        foreach (var path in new[] { _configPath, _certificatePath })
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}
