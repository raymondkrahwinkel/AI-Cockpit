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

    // AC-792 made the node's TLS identity a file that outlives the process. These tests must never touch the real
    // one, so each gets a path of its own under the temp directory — and since the certificate is lazy, a test that
    // leaves node binding off never creates the file at all.
    private static NodeSelfSignedCertificate _ThrowawayNodeCertificate() =>
        new(Path.Combine(Path.GetTempPath(), $"cockpit-test-node-{Guid.NewGuid():N}.pfx"));

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
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);

        var mounted = Assert.Single(host.GetServers());
        Assert.StartsWith("http://127.0.0.1:", mounted.Url);
        Assert.Empty(host.GetNodeAddresses());
    }

    // AC-790: with the switch on, each mounted endpoint gets a second, HTTPS, off-loopback listener whose address
    // GetNodeAddresses reports — one entry per mounted endpoint.
    [Fact]
    public async Task EndpointHost_NodeBindingOn_ReportsAnHttpsNodeAddressPerMountedEndpoint()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = "test-secret-value" }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);

        var nodeAddress = Assert.Single(host.GetNodeAddresses());
        Assert.Equal("cockpit-probe", nodeAddress.ServerName);
        Assert.StartsWith("https://", nodeAddress.Url);
        Assert.EndsWith("/mcp", nodeAddress.Url);

        // The loopback listener is still there too, unaffected by the node listener beside it.
        Assert.StartsWith("http://127.0.0.1:", Assert.Single(host.GetServers()).Url);
    }

    // AC-791, criterion 2: an internal endpoint (AC-204) stays loopback-only even with the master switch on, so a
    // caller on another machine has no socket to reach it on. A public endpoint is mounted in the same host and
    // with the same settings, so the difference can only come from the internal flag — without that, this would
    // also pass on a machine where no LAN address resolves at all, which would prove nothing.
    [Fact]
    public async Task EndpointHost_NodeBindingOn_BindsNoNodeListenerForAnInternalEndpoint()
    {
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = "test-secret-value" }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-public", new ProbeTools(), isEnabled: () => true);
        await host.MountAsync("cockpit-private", new ProbeTools(), isEnabled: () => true, isInternal: true);

        var nodeAddress = Assert.Single(host.GetNodeAddresses());
        Assert.Equal("cockpit-public", nodeAddress.ServerName);

        // Loopback is untouched for both: internal means "not off this machine", not "not hosted".
        Assert.All(host.GetServers(), server => Assert.StartsWith("http://127.0.0.1:", server.Url));
    }

    // AC-790: the node listener is guarded by the persistent shared secret, and — same as the other two credential
    // paths McpAuthMiddleware already had — a wrong or missing one gets the same generic 401 either way.
    [Fact]
    public async Task NodeListener_GuardsWithTheSharedSecret_SameGenericRejectionEitherWayItFails()
    {
        const string sharedSecret = "test-secret-value";

        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = sharedSecret }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);
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
        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = "test-secret-value" }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);
        var nodeUrl = Assert.Single(host.GetNodeAddresses()).Url;
        var plainHttpUrl = "http://" + nodeUrl["https://".Length..];

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(plainHttpUrl));
    }

    // AC-790 review finding: the node secret and the loopback-scoped credentials must not cross — a request on the
    // loopback listener presenting the persistent node secret as its bearer token must be rejected exactly like any
    // other unrecognised credential, even though the same secret is valid on the off-loopback listener beside it.
    [Fact]
    public async Task LoopbackListener_RejectsTheNodeSecret_ItIsScopedToTheOffLoopbackListenerOnly()
    {
        const string sharedSecret = "test-secret-value";

        await using var host = new CockpitMcpEndpointHost(
            endpoints: [],
            services: new ServiceCollection().BuildServiceProvider(),
            authKey: new McpAuthKey(),
            keyring: new SessionMcpKeyring(),
            nodeEndpointSettings: new FakeNodeEndpointSettingsStore(
                new NodeEndpointSettings { Enabled = true, SharedSecret = sharedSecret }),
            nodeCertificate: _ThrowawayNodeCertificate(),
            nodeSharedSecret: new NodeSharedSecret(),
            loggerFactory: NullLoggerFactory.Instance);

        await host.MountAsync("cockpit-probe", new ProbeTools(), isEnabled: () => true);
        var loopbackUrl = Assert.Single(host.GetServers()).Url;

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, loopbackUrl) { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sharedSecret);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
