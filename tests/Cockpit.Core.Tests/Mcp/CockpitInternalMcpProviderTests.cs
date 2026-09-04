using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The two cockpit-hosted MCP sources answer the session fan-out live as <see cref="ICockpitInternalMcpProvider"/>s
/// (AC-40): they must project their live loopback URL, mark themselves <c>CockpitHosted</c>, and report the enabled
/// state their toggle currently gives — read each time, so a flip takes effect without a rebind. A real Kestrel
/// loopback endpoint is stood up (the same pattern as <see cref="InProcessMcpHttpServer"/>), since that is the only
/// place the bound URL comes from.
/// </summary>
public class CockpitInternalMcpProviderTests
{
    private sealed class ProbeTools
    {
        [McpServerTool, Description("A probe tool, so the endpoint mounts a non-empty tool set.")]
        public static string Ping() => "pong";
    }

    // A hand-written test double for the settings store (AC-790) — no mocking framework needed for a two-method
    // interface. Defaults to the same off/empty-secret state `NodeEndpointSettings.Default` gives a real store
    // that was never saved to.
    private sealed class FakeNodeEndpointSettingsStore(NodeEndpointSettings? settings = null) : INodeEndpointSettingsStore
    {
        private NodeEndpointSettings _settings = settings ?? NodeEndpointSettings.Default;

        public Task<NodeEndpointSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(NodeEndpointSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    // AC-1148: the services a node-reachable endpoint resolves its pairing scope from. A scoped pairing is the
    // normal state of a coupling the operator has actually ticked something for (AC-794); an empty one grants
    // nothing, which is what a fresh pairing looks like.
    private static IServiceProvider _ServicesWithPairing(bool scoped)
    {
        var broker = Substitute.For<INodePairingBroker>();
        broker.Pairing.Returns(new NodePairing
        {
            ControllerName = "controller",
            ControllerAddress = "10.0.0.2",
            PairedAtUtc = DateTimeOffset.UnixEpoch,
            AllowedProfileLabels = scoped ? ["default"] : [],
        });

        var services = new ServiceCollection();
        services.AddSingleton(broker);
        return services.BuildServiceProvider();
    }

    // AC-792 made the node's TLS identity a file that outlives the process. These tests must never touch the real
    // one, so each gets a path of its own under the temp directory — and since the certificate is lazy, a test that
    // leaves node binding off never creates the file at all.
    private static NodeSelfSignedCertificate _ThrowawayNodeCertificate() =>
        new(Path.Combine(Path.GetTempPath(), $"cockpit-test-node-{Guid.NewGuid():N}.pfx"));

    // AC-856: a node listener is earned by the NodeOnly flag, and only registration can set it — `MountAsync` is
    // the plugin door and deliberately hardcodes NodeOnly false. So a test that wants one goes in through
    // `StartAsync` with a real registration, which is also the path production takes.
    private static async Task<CockpitMcpEndpointHost> _StartedHostAsync(
        bool nodeEnabled,
        IServiceProvider? services = null,
        params CockpitMcpEndpoint[] endpoints)
    {
        var host = new CockpitMcpEndpointHost(
            endpoints: endpoints,
            services: services ?? new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                nodeEnabled ? new NodeEndpointSettings { Enabled = true, SharedSecret = NodeSecret } : null),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.StartAsync(CancellationToken.None);
        return host;
    }

    private const string NodeSecret = "test-secret-value";

    [Fact]
    public async Task EndpointHost_ReflectsTheLiveIsEnabledGate_AndMarksItselfCockpitHosted()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);

        // Nothing mounted yet: the fan-out sees no cockpit-hosted server.
        Assert.Empty(host.GetServers());

        var enabled = true;
        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => enabled);

        var mounted = Assert.Single(host.GetServers());
        Assert.Equal("cockpit-probe", mounted.Name);
        Assert.True(mounted.CockpitHosted);
        Assert.StartsWith("http://127.0.0.1:", mounted.Url);
        Assert.EndsWith("/mcp", mounted.Url);
        Assert.True(mounted.Enabled);

        // The gate is read on every call, so flipping the plugin's own setting changes the answer with no rebind.
        enabled = false;
        Assert.False(Assert.Single(host.GetServers()).Enabled);
    }

    [Fact]
    public async Task EndpointHost_ProjectsTheInternalFlag_SoTheFilterCanHideSpawnScopedEndpoints()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);

        // An ordinary endpoint is not internal; an internal-only one (AC-204, e.g. the Autopilot CEO/step tools)
        // carries the flag through to the fan-out's McpServerConfig so the user-facing selection can hide it.
        await host.MountAsync("cockpit-public", new ProbeTools(), isEnabled: () => true);
        await host.MountAsync("cockpit-private", new ProbeTools(), isEnabled: () => true, isInternal: true);

        var servers = host.GetServers();
        Assert.False(servers.Single(server => server.Name == "cockpit-public").Internal);
        Assert.True(servers.Single(server => server.Name == "cockpit-private").Internal);
    }

    // AC-790: the network-node master switch, off by default. Node binding off means only the loopback listener
    // exists — no second URL to hand anyone.
    [Fact]
    public async Task EndpointHost_NodeBindingOff_OnlyEverAdvertisesTheLoopbackListener()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);

        var mounted = Assert.Single(host.GetServers());
        Assert.StartsWith("http://127.0.0.1:", mounted.Url);
        Assert.Empty(host.GetNodeAddresses());
    }

    /// <summary>
    /// AC-856, the whole bind rule in one table: with the master switch on, a NodeOnly endpoint gets the second,
    /// HTTPS, off-loopback listener and nothing else does. Every row is mounted into the same host with the same
    /// settings, so a difference can only come from the flags — on a machine where no LAN address resolves, a
    /// one-row version of this would pass while proving nothing.
    /// </summary>
    /// <remarks>
    /// Replaces three narrower tests: AC-790's "an address per mounted endpoint" (the behaviour this ticket
    /// removes), AC-791's Internal case and AC-1148's switched-off case, both of which survive here as rows.
    /// This is the only thing standing between the shipped default and N sockets on <c>0.0.0.0</c> — the state
    /// measured on 2026-09-05, where one ticked profile answered 200 OK on every one of them.
    /// </remarks>
    [Fact]
    public async Task EndpointHost_NodeBindingOn_BindsANodeListenerForTheNodeOnlyEndpointAndNothingElse()
    {
        await using var host = await _StartedHostAsync(
            nodeEnabled: true,
            services: null,
            new CockpitMcpEndpoint("cockpit-node-probe", typeof(ProbeTools), NodeOnly: true),
            new CockpitMcpEndpoint("cockpit-ordinary", typeof(ProbeTools)),
            new CockpitMcpEndpoint("cockpit-internal", typeof(ProbeTools), Internal: true),
            new CockpitMcpEndpoint("cockpit-node-probe-off", typeof(ProbeTools), () => false, NodeOnly: true));

        var nodeAddress = Assert.Single(host.GetNodeAddresses());
        Assert.Equal("cockpit-node-probe", nodeAddress.ServerName);
        Assert.StartsWith("https://", nodeAddress.Url);
        Assert.EndsWith("/mcp", nodeAddress.Url);

        // And loopback is untouched for all four: none of these flags means "not hosted", only "not off this machine".
        Assert.Equal(4, host.GetServers().Count);
        Assert.All(host.GetServers(), server => Assert.StartsWith("http://127.0.0.1:", server.Url));
    }

    // AC-790: the node listener is guarded by the persistent shared secret, and — same as the other two credential
    // paths McpAuthMiddleware already had — a wrong or missing one gets the same generic 401 either way.
    [Fact]
    public async Task NodeListener_GuardsWithTheSharedSecret_SameGenericRejectionEitherWayItFails()
    {
        const string sharedSecret = NodeSecret;

        await using var host = await _StartedHostAsync(
            nodeEnabled: true,
            // AC-1148: a pairing the operator has scoped, so what this test measures stays the credential and not
            // the authorization beside it.
            services: _ServicesWithPairing(scoped: true),
            new CockpitMcpEndpoint("cockpit-node-probe", typeof(ProbeTools), NodeOnly: true));

        var nodeUrl = Assert.Single(host.GetNodeAddresses()).Url;

        using var client = _CreateClientTrustingTheSelfSignedCertificate();

        using (var noHeader = new HttpRequestMessage(HttpMethod.Post, nodeUrl) { Content = new StringContent("{}") })
        using (var response = await client.SendAsync(noHeader))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var wrongSecret = new HttpRequestMessage(HttpMethod.Post, nodeUrl) { Content = new StringContent("{}") })
        {
            wrongSecret.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-secret");
            using var response = await client.SendAsync(wrongSecret);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var rightSecret = new HttpRequestMessage(HttpMethod.Post, nodeUrl) { Content = new StringContent("{}") })
        {
            rightSecret.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sharedSecret);
            using var response = await client.SendAsync(rightSecret);
            // Past the auth gate: whatever MapMcp makes of a malformed JSON-RPC body is not this middleware's
            // concern — only that it is never the 401 the two cases above got.
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // AC-790: the node listener is TLS-only (Kestrel's UseHttps). A plain HTTP request against the same host:port
    // sends bytes Kestrel is waiting to read as a TLS ClientHello, so the handshake never completes and the
    // connection is dropped rather than answered — the exact exception HttpClient surfaces for that is OS-dependent
    // (a reset socket reads differently on Linux than on Windows), so this only pins "never a valid response".
    [Fact]
    public async Task NodeListener_RefusesPlainHttp_TheListenerIsTlsOnly()
    {
        await using var host = await _StartedHostAsync(
            nodeEnabled: true,
            services: null,
            new CockpitMcpEndpoint("cockpit-node-probe", typeof(ProbeTools), NodeOnly: true));

        var nodeUrl = Assert.Single(host.GetNodeAddresses()).Url;
        var plainHttpUrl = "http://" + nodeUrl["https://".Length..];

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(plainHttpUrl));
    }

    // AC-790 review finding: the node secret and the loopback-scoped credentials must not cross — a request on the
    // loopback listener presenting the persistent node secret as its bearer token must be rejected exactly like any
    // other unrecognised credential, even though the same secret is live on this cockpit's node listener. AC-856
    // moved that listener onto the one NodeOnly endpoint, which makes this rule matter more rather than less: the
    // secret is now the credential for a single endpoint, and every other one is loopback's alone.
    [Fact]
    public async Task LoopbackListener_RejectsTheNodeSecret_ItIsScopedToTheOffLoopbackListenerOnly()
    {
        const string sharedSecret = NodeSecret;

        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = sharedSecret }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: new SessionMcpMounts(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);
        var loopbackUrl = Assert.Single(host.GetServers()).Url;

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, loopbackUrl) { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sharedSecret);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // AC-1148's "a switched-off endpoint gets no node socket at all" is now the fourth row of the bind table
    // above, where it sits beside the three other reasons an endpoint does not get one.

    // AC-1148, the loopback negative control on the real host: a session's own live token, on a mounted endpoint
    // its launch never selected. Every existing test here asked only "valid credential or not"; a wrong-scope call
    // is valid, which is exactly why it went unseen.
    [Fact]
    public async Task EndpointHost_SessionTokenForAnEndpointItsLaunchNeverMounted_Is403ButThePaneThatDidGetsThrough()
    {
        var keyring = new SessionMcpKeyring();
        var mounts = new SessionMcpMounts();
        mounts.Grant("pane-allowed", ["cockpit-probe"]);
        mounts.Grant("pane-denied", ["cockpit-something-else"]);

        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: keyring,
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            mounts: mounts,
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);
        var url = Assert.IsType<string>(Assert.Single(host.GetServers()).Url);

        Assert.Equal(HttpStatusCode.Forbidden, await _PostStatusAsync(url, keyring.TokenFor("pane-denied")));

        // And the positive control beside it: the pane that did mount this endpoint is not held up by the check.
        Assert.NotEqual(HttpStatusCode.Forbidden, await _PostStatusAsync(url, keyring.TokenFor("pane-allowed")));
    }

    // AC-1148 (was AC-1161, first half): the heaviest of the three. "A session only gets these tools if started
    // with delegation enabled" was the fan-out talking — the listener took any live token, so a session whose
    // profile has no delegation could start a bypassPermissions sub-agent by finding the port.
    [Fact]
    public async Task Orchestrator_SessionThatNeverGotDelegation_Is403AndSoIsEveryoneWhileTheToggleIsOff()
    {
        var store = Substitute.For<IDelegationSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new DelegationSettings { McpEnabled = true });

        var keyring = new SessionMcpKeyring();
        var mounts = new SessionMcpMounts();
        mounts.Grant("pane-delegating", [DelegationMcp.ServerName]);
        mounts.Grant("pane-plain", ["cockpit-session"]);

        await using var server = new OrchestratorMcpServer(
            Substitute.For<IDelegationService>(),
            new McpAuthKey(),
            keyring,
            store,
            mounts,
            NullLoggerFactory.Instance);
        await server.StartAsync(CancellationToken.None);

        var url = Assert.IsType<string>(server.OrchestratorMcpUrl);
        Assert.Equal(HttpStatusCode.Forbidden, await _PostStatusAsync(url, keyring.TokenFor("pane-plain")));
        Assert.NotEqual(HttpStatusCode.Forbidden, await _PostStatusAsync(url, keyring.TokenFor("pane-delegating")));

        // And the Options toggle is a boundary now too, not just an entry the fan-out leaves out.
        await server.SetMcpEnabledAsync(false);
        Assert.Equal(HttpStatusCode.Forbidden, await _PostStatusAsync(url, keyring.TokenFor("pane-delegating")));

        await server.StopAsync(CancellationToken.None);
    }

    // The status of a bare POST at an MCP endpoint: enough to tell a refusal from anything the transport makes of
    // a body it never got to read, which is all these authorization tests ask.
    private static async Task<HttpStatusCode> _PostStatusAsync(string url, string bearer)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // The self-signed certificate (AC-790) has no CA behind it by design (see NodeSelfSignedCertificate) — a real
    // client is told out-of-band to trust this specific instance's certificate; a test stands in for that trust.
    private static HttpClient _CreateClientTrustingTheSelfSignedCertificate()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        return new HttpClient(handler);
    }

    [Fact]
    public async Task Orchestrator_LoadsTheToggleAtStartup_ThenFlipsAndPersistsOnSet()
    {
        var store = Substitute.For<IDelegationSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new DelegationSettings { McpEnabled = false });

        var server = new OrchestratorMcpServer(
            Substitute.For<IDelegationService>(),
            new McpAuthKey(),
            new SessionMcpKeyring(),
            store,
            new SessionMcpMounts(),
            NullLoggerFactory.Instance);

        // Before it has bound a port there is nothing to hand the fan-out.
        Assert.Empty(server.GetServers());

        await server.StartAsync(default);
        try
        {
            // Startup honoured the persisted off-state, and the server names itself a cockpit-hosted endpoint.
            var started = Assert.Single(server.GetServers());
            Assert.Equal(OrchestratorMcpServer.ServerName, started.Name);
            Assert.True(started.CockpitHosted);
            Assert.False(started.Enabled);

            await server.SetMcpEnabledAsync(true);

            Assert.True(Assert.Single(server.GetServers()).Enabled);
            await store.Received().SaveAsync(
                Arg.Is<DelegationSettings>(settings => settings.McpEnabled), Arg.Any<CancellationToken>());
        }
        finally
        {
            await server.StopAsync(default);
            await server.DisposeAsync();
        }
    }
}
