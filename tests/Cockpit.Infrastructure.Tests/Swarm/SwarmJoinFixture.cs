using System.Net;
using System.Net.Http.Headers;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Swarm;

internal sealed class SwarmJoinFixture : IAsyncDisposable
{
    public Clock Time { get; } = new();
    public Machine A { get; private set; } = null!;
    public Machine B { get; private set; } = null!;

    public static async Task<SwarmJoinFixture> CreateAsync(SwarmHostKind kind = SwarmHostKind.Desktop)
    {
        var pair = new SwarmJoinFixture();
        pair.A = await Machine.CreateAsync(pair.Time, SwarmHostKind.Desktop);
        pair.B = await Machine.CreateAsync(pair.Time, kind);
        return pair;
    }

    public async Task AssertBothDirectionsAsync()
    {
        foreach (var (source, target) in new[] { (A, B), (B, A) })
        {
            var server = Assert.Single(await source.Servers.LoadAsync(), server => server.PinnedCertificateFingerprint == target.Certificate.Fingerprint);
            Assert.Equal(target.Certificate.Fingerprint, server.PinnedCertificateFingerprint);
            using var client = new HttpClient(NodeCertificatePin.Require(server.PinnedCertificateFingerprint!));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
            using var response = await client.GetAsync(server.Url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    public async ValueTask DisposeAsync() { await A.DisposeAsync(); await B.DisposeAsync(); }

    internal sealed class Machine : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("COCKPIT_SCRATCH") ?? Path.GetTempPath(), "swarm-join-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        public NodeEndpointSettingsStore Settings { get; private set; } = null!;
        public NodeSelfSignedCertificate Certificate { get; private set; } = null!;
        public SwarmIdentity Identity { get; private set; } = null!;
        public SwarmMembershipStore Membership { get; private set; } = null!;
        public ConnectKeyVerifier Keys { get; private set; } = null!;
        public MemoryServers Servers { get; } = new();
        public SwarmJoinHost Join { get; private set; } = null!;
        public SwarmJoinClient Client { get; private set; } = null!;
        public string Address { get; private set; } = "";
        public Toast Toast { get; } = new();
        public NodeControllerPresence Presence { get; private set; } = null!;

        public static async Task<Machine> CreateAsync(Clock time, SwarmHostKind kind)
        {
            var machine = new Machine();
            Directory.CreateDirectory(machine._root);
            machine.Settings = new(Path.Combine(machine._root, "cockpit.json"));
            await machine.Settings.SaveAsync(new NodeEndpointSettings { Enabled = true, Port = 7443 });
            machine.Certificate = new(Path.Combine(machine._root, "node.pfx"));
            machine.Identity = new(machine.Certificate);
            machine.Membership = new(Path.Combine(machine._root, "members.json"));
            var audit = new NodeAccessAuditLog(Path.Combine(machine._root, "audit.jsonl"), NullLogger<NodeAccessAuditLog>.Instance);
            machine.Keys = new(Path.Combine(machine._root, "cockpit.json"), _ => null, _ => { }, time, audit, NullLogger.Instance);
            var policy = new SwarmNetworkPolicy(new SwarmSettingsStore(Path.Combine(machine._root, "cockpit.json")), audit, NullLogger<SwarmNetworkPolicy>.Instance);
            var endpoint = new Endpoint();
            var host = new SwarmHost(kind);
            machine.Presence = new(time);
            machine.Join = new(host, machine.Certificate, machine.Identity, machine.Membership, machine.Keys, machine.Settings, machine.Servers, [endpoint], machine.Toast, machine.Presence) { Time = time, Name = Guid.NewGuid().ToString("N") };
            machine.Client = new(host, machine.Certificate, machine.Identity, machine.Membership, policy, machine.Join, machine.Settings, new NodeDiscoveryClient());
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(policy);
            builder.Services.AddSingleton(machine.Client);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = machine.Certificate.Value;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            })));
            machine._app = builder.Build();
            machine.Join.MapRoutes(machine._app, machine._app.Services);
            machine._app.MapGet("/remote", async (HttpContext context) =>
            {
                var token = context.Request.Headers.Authorization.ToString();
                var caller = await machine.Keys.AuthenticateAsync(token.StartsWith("Bearer ", StringComparison.Ordinal) ? token[7..] : token, null, "127.0.0.1");
                return caller is { Capability: ConnectKeyCapability.Admin, MayStartBypass: true }
                    ? Results.Ok() : Results.Unauthorized();
            });
            await machine._app.StartAsync();
            var bound = machine._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var port = new Uri(bound).Port;
            machine.Address = $"https://127.0.0.1:{port}";
            await machine.Settings.SaveAsync(new NodeEndpointSettings { Enabled = true, Port = port });
            endpoint.Address = machine.Address + "/remote";
            return machine;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Certificate.Dispose();
            Directory.Delete(_root, true);
        }

        public Task StopAsync() => _app.StopAsync();
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    internal sealed class MemoryServers : IMcpServerStore
    {
        private IReadOnlyList<McpServerConfig> _servers = [];
        public Task<IReadOnlyList<McpServerConfig>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_servers);
        public Task SaveAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default) { _servers = servers; return Task.CompletedTask; }
    }

    internal sealed class Toast : IToastNotifier
    {
        public List<AttentionNotification> Notifications { get; } = [];
        public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) { Notifications.Add(notification); return Task.CompletedTask; }
    }

    private sealed class Endpoint : ICockpitInternalMcpProvider
    {
        public string Address { get; set; } = "";
        public IReadOnlyList<McpServerConfig> GetServers() => [];
        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() => [new("cockpit-node", Address)];
    }
}
