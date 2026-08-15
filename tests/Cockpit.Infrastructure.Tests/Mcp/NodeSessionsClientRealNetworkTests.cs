using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
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

    private const string AllowedProfile = "Laptop Sonnet";

    [Fact]
    public async Task ReadAsync_ANodeThatAnswers_ListsWhatItReported()
    {
        var certificatePath = _TempCertificatePath();
        try
        {
            using var certificate = new NodeSelfSignedCertificate(certificatePath);
            var sharedSecret = new NodeSharedSecret();
            sharedSecret.Set("the-shared-secret");
            var read = new NodeSessionMcpToolsTests.RecordingReadGateway();
            read.Sessions.Add(new AssistantSessionRow("pane-a", "the sweep", AllowedProfile, "running", null, null));
            var pairing = new NodeSessionMcpToolsTests.StubPairing();
            pairing.Profiles.Add(AllowedProfile);

            await using var host = await _StartNodeHostAsync(certificate, sharedSecret, read, pairing);
            var client = _ClientFor(host.Url, certificate.Fingerprint, sharedSecret.Value!);

            var snapshot = await client.ReadAsync(NodeName);

            Assert.Null(snapshot.Error);
            Assert.Equal("pane-a", Assert.Single(snapshot.Sessions).PaneId);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    [Fact]
    public async Task ReadAsync_AfterTheNodesListenerStops_ReportsARealConnectionRefusal()
    {
        var certificatePath = _TempCertificatePath();
        try
        {
            using var certificate = new NodeSelfSignedCertificate(certificatePath);
            var sharedSecret = new NodeSharedSecret();
            sharedSecret.Set("the-shared-secret");
            var read = new NodeSessionMcpToolsTests.RecordingReadGateway();
            read.Sessions.Add(new AssistantSessionRow("pane-a", "the sweep", AllowedProfile, "running", null, null));
            var pairing = new NodeSessionMcpToolsTests.StubPairing();
            pairing.Profiles.Add(AllowedProfile);

            var host = await _StartNodeHostAsync(certificate, sharedSecret, read, pairing);
            var client = _ClientFor(host.Url, certificate.Fingerprint, sharedSecret.Value!);

            // Control measurement first (AC-529's own lesson): prove the harness answers before proving what it
            // does once it stops answering, so a failure below is the node dropping out and not a broken rig.
            var before = await client.ReadAsync(NodeName);
            Assert.Null(before.Error);

            // The node valt weg, midway through being usable — a hard stop of the real listener, not a fake that
            // returns an error shape. `DisposeAsync` tears the Kestrel host down immediately rather than draining
            // in-flight requests first, which is what an actual power loss or process kill looks like on the wire.
            await host.DisposeAsync();

            var after = await client.ReadAsync(NodeName);

            Assert.NotNull(after.Error);
            Assert.Contains(NodeName, after.Error, StringComparison.Ordinal);
            Assert.Contains("looks stopped", after.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

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
            NullLogger<NodeSessionsClient>.Instance);

    // The real Kestrel wiring `CockpitMcpEndpointHost`/`NodePairingHost` use for their node listener: an HTTPS
    // loopback port with the node's self-signed certificate, `NodeSessionMcpTools` behind `McpAuthMiddleware`
    // gated on the shared secret — nothing here is a stand-in for the transport, only the four gateways
    // `NodeSessionMcpTools` reads from are fakes (the same ones `NodeSessionMcpToolsTests` already uses).
    private static async Task<_NodeHost> _StartNodeHostAsync(
        NodeSelfSignedCertificate certificate,
        NodeSharedSecret sharedSecret,
        NodeSessionMcpToolsTests.RecordingReadGateway read,
        NodeSessionMcpToolsTests.StubPairing pairing)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IAssistantReadGateway>(read);
        builder.Services.AddSingleton<IAssistantAgentGateway>(new NodeSessionMcpToolsTests.RecordingAgentGateway());
        builder.Services.AddSingleton<INodePairingBroker>(pairing);
        builder.Services.AddSingleton<ISessionProfileStore>(new NodeSessionMcpToolsTests.StubProfileStore());
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<NodeSessionMcpTools>();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(certificate.Value)));

        var app = builder.Build();
        McpAuthMiddleware.Require(app, new McpAuthKey(), new SessionMcpKeyring(), sharedSecret);
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
