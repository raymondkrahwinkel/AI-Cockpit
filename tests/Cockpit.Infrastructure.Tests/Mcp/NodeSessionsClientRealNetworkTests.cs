using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// <see cref="NodeSessionsClient"/> against a real <c>NodeSessionMcpTools</c> listener (AC-796, criterion 4): "the
/// failure the network makes", not a mock that hands back a tidy error code. A real HTTPS Kestrel host, with the
/// same certificate pin, bearer secret and <see cref="McpAuthMiddleware"/> production wires it through, serves the
/// tools; the client talks to it exactly as it would talk to a paired node. This is also the harness
/// <c>NodeSessionsClient</c> itself never had — <c>NodePairingHandshakeTests</c> covers the pairing handshake, not
/// session listing over the same kind of connection.
/// </summary>
public sealed class NodeSessionsClientRealNetworkTests
{
    private const string NodeName = "laptop";

    [Fact]
    public async Task ReadAsync_ANodeAnsweringWithAnUnpinnedCertificate_ReportsCertificateNotTrusted()
    {
        var certificatePath = _TempCertificatePath();
        try
        {
            using var certificate = new NodeSelfSignedCertificate(certificatePath);
            var sharedSecret = new NodeSharedSecret();
            sharedSecret.Set("the-shared-secret");
            var pairing = new NodeSessionMcpToolsTests.StubPairing { AllowEverything = true };

            await using var host = await _StartNodeHostAsync(certificate, sharedSecret, new NodeSessionMcpToolsTests.RecordingReadGateway(), pairing);

            // Pinned to a fingerprint that is not this host's real one — exactly what a re-installed node, or
            // something else answering at the paired address, would look like on the wire.
            var client = _ClientFor(host.Url, new string('A', 64), sharedSecret.Value!);

            var snapshot = await client.ReadAsync(NodeName);

            Assert.NotNull(snapshot.Error);
            Assert.Contains("did not pin", snapshot.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    /// <summary>
    /// The same move, on a row with no certificate pin — a node paired by hand rather than through the handshake.
    /// Without a pin there is nothing that says which machine answered, so the client declines to rewrite the row
    /// and reports the original failure instead of adopting whichever cockpit on the segment replied first.
    /// </summary>
    [Fact]
    public async Task ReadAsync_AMovedNodeOnARowWithoutAPin_IsNotAdopted()
    {
        var certificatePath = _TempCertificatePath();
        try
        {
            using var certificate = new NodeSelfSignedCertificate(certificatePath);
            var sharedSecret = new NodeSharedSecret();
            sharedSecret.Set("the-shared-secret");
            var pairing = new NodeSessionMcpToolsTests.StubPairing { AllowEverything = true };

            await using var host = await _StartNodeHostAsync(certificate, sharedSecret, new NodeSessionMcpToolsTests.RecordingReadGateway(), pairing);

            var store = new _MutableStore(_RowFor("https://127.0.0.1:1/mcp", pinnedFingerprint: "", sharedSecret.Value!));
            var discovery = new _FoundAt($"127.0.0.1:{new Uri(host.Url).Port - NodeEndpointSettings.McpPortOffset}");
            var client = new NodeSessionsClient(store, discovery, NullLogger<NodeSessionsClient>.Instance);

            var snapshot = await client.ReadAsync(NodeName);

            Assert.NotNull(snapshot.Error);
            Assert.Equal("https://127.0.0.1:1/mcp", Assert.Single(store.Servers).Url);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    /// <summary>
    /// AC-1458 criterion 1: a fingerprint that does not match stops a connect before the key leaves this machine.
    /// The server sees no request with an Authorization header; the matching probe after it proves the recorder sees one.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_AFingerprintThatDoesNotMatch_FailsBeforeAnyRequestCarriesTheKey()
    {
        var certificatePath = _TempCertificatePath();
        try
        {
            using var certificate = new NodeSelfSignedCertificate(certificatePath);
            var sharedSecret = new NodeSharedSecret();
            sharedSecret.Set("the-shared-secret");
            var withKey = new ConcurrentQueue<string>();

            await using var host = await _StartNodeHostAsync(
                certificate,
                sharedSecret,
                new NodeSessionMcpToolsTests.RecordingReadGateway(),
                new NodeSessionMcpToolsTests.StubPairing { AllowEverything = true },
                request =>
                {
                    if (request.Headers.Authorization.Count > 0)
                    {
                        withKey.Enqueue(request.Path);
                    }
                });

            var wrong = _RowFor(host.Url, new string('A', 64), "the-shared-secret");
            var client = new NodeSessionsClient(new _SingleServerStore(wrong), new _NoNodesFound(), NullLogger<NodeSessionsClient>.Instance);

            var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.ProbeAsync(wrong));
            var mismatch = NodeConnectionFailure.Find<NodeCertificatePinMismatchException>(refused);
            var keysBeforeTheMatch = withKey.Count;
            var matched = await client.ProbeAsync(_RowFor(host.Url, certificate.Fingerprint, "the-shared-secret"));

            Assert.NotNull(mismatch);
            Assert.Equal(0, keysBeforeTheMatch);
            Assert.Equal(new string('A', 64), mismatch.ExpectedFingerprint);
            Assert.Equal(NodePairingCode.Normalize(certificate.Fingerprint), mismatch.PresentedFingerprint);
            Assert.NotEmpty(withKey);
            Assert.Equal(NodePairingCode.Normalize(certificate.Fingerprint), matched.PresentedFingerprint);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    private static McpServerConfig _RowFor(string url, string pinnedFingerprint, string sharedSecret) => new()
    {
        Name = NodeServerName.For(NodeName, NodeServerName.SessionsServerName),
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.ApiKey,
        ApiKey = sharedSecret,
        PinnedCertificateFingerprint = pinnedFingerprint,
    };

    private sealed class _FoundAt(string address) : INodeDiscoveryClient
    {
        public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NodeDiscoveryFound>>([new NodeDiscoveryFound(address, "the-node")]);
    }

    private sealed class _MutableStore(McpServerConfig server) : IMcpServerStore
    {
        public IReadOnlyList<McpServerConfig> Servers { get; private set; } = [server];

        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Servers);

        public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default)
        {
            Servers = servers;
            return Task.CompletedTask;
        }
    }

    private static string _TempCertificatePath() =>
        Path.Combine(Path.GetTempPath(), $"node-sessions-real-{Guid.NewGuid():N}.pfx");

    private static NodeSessionsClient _ClientFor(string url, string pinnedFingerprint, string sharedSecret) =>
        new(
            new _SingleServerStore(new McpServerConfig
            {
                Name = NodeServerName.For(NodeName, NodeServerName.SessionsServerName),
                Transport = McpTransport.Http,
                Url = url,
                Auth = McpServerAuth.ApiKey,
                ApiKey = sharedSecret,
                PinnedCertificateFingerprint = pinnedFingerprint,
            }),
            // AC-1284's re-resolve asks discovery where a node that stopped answering went. Nothing here has moved,
            // so a finder that reports an empty segment keeps these tests about the transport and not about UDP.
            new _NoNodesFound(),
            NullLogger<NodeSessionsClient>.Instance);

    private sealed class _NoNodesFound : INodeDiscoveryClient
    {
        public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NodeDiscoveryFound>>([]);
    }

    // The real Kestrel wiring `CockpitMcpEndpointHost`/`NodePairingHost` use for their node listener: an HTTPS
    // loopback port with the node's self-signed certificate, `NodeSessionMcpTools` behind `McpAuthMiddleware`
    // gated on the shared secret — nothing here is a stand-in for the transport, only the four gateways
    // `NodeSessionMcpTools` reads from are fakes (the same ones `NodeSessionMcpToolsTests` already uses).
    private static async Task<_NodeHost> _StartNodeHostAsync(
        NodeSelfSignedCertificate certificate,
        NodeSharedSecret sharedSecret,
        NodeSessionMcpToolsTests.RecordingReadGateway read,
        NodeSessionMcpToolsTests.StubPairing pairing,
        Action<HttpRequest>? seen = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IAssistantReadGateway>(read);
        builder.Services.AddSingleton<IAssistantAgentGateway>(new NodeSessionMcpToolsTests.RecordingAgentGateway());
        builder.Services.AddSingleton<INodePairingBroker>(pairing);
        builder.Services.AddSingleton<ISessionProfileStore>(new NodeSessionMcpToolsTests.StubProfileStore());
        builder.Services.AddSingleton(new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")));
        builder.Services.AddSingleton<IAgentMessageInbox>(new AgentMessageInbox());
        builder.Services.AddSingleton<IAssistantMemory>(new NodeSessionMcpToolsTests.StubMemory());
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<NodeSessionMcpTools>();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(certificate.Value)));

        var app = builder.Build();

        // Ahead of the auth middleware, so a request it refuses is seen as well (AC-1458).
        if (seen is not null)
        {
            app.Use((context, next) =>
            {
                seen(context.Request);
                return next(context);
            });
        }

        // AC-1148: this stands in for the endpoint host's own policy — what is under test here is the client over a
        // real socket, and the policy itself is covered where it lives (McpAuthMiddlewareTests).
        McpAuthMiddleware.Require(app, new McpAuthKey(), new SessionMcpKeyring(), static _ => new ValueTask<bool>(true), sharedSecret);
        app.MapMcp("/mcp");

        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.First(address => address.StartsWith("https://", StringComparison.Ordinal));

        return new _NodeHost(app, $"{boundUrl.TrimEnd('/')}/mcp");
    }

    private sealed class _NodeHost(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class _SingleServerStore(McpServerConfig server) : IMcpServerStore
    {
        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpServerConfig>>([server]);

        public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
