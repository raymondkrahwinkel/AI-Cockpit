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
    private Machine? _a;
    public Machine A { get => _a ?? throw new InvalidOperationException("A is read before CreateAsync set it."); private set => _a = value; }
    private Machine? _b;
    public Machine B { get => _b ?? throw new InvalidOperationException("B is read before CreateAsync set it."); private set => _b = value; }

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
        private WebApplication? _started;
        private WebApplication _app { get => _started ?? throw new InvalidOperationException("The app is read before CreateAsync started it."); set => _started = value; }
        private NodeEndpointSettingsStore? _settings;
        public NodeEndpointSettingsStore Settings { get => _settings ?? throw new InvalidOperationException("Settings is read before CreateAsync set it."); private set => _settings = value; }
        private NodeSelfSignedCertificate? _certificate;
        public NodeSelfSignedCertificate Certificate { get => _certificate ?? throw new InvalidOperationException("Certificate is read before CreateAsync set it."); private set => _certificate = value; }
        private SwarmIdentity? _identity;
        public SwarmIdentity Identity { get => _identity ?? throw new InvalidOperationException("Identity is read before CreateAsync set it."); private set => _identity = value; }
        private SwarmMembershipStore? _membership;
        public SwarmMembershipStore Membership { get => _membership ?? throw new InvalidOperationException("Membership is read before CreateAsync set it."); private set => _membership = value; }
        private ConnectKeyVerifier? _keys;
        public ConnectKeyVerifier Keys { get => _keys ?? throw new InvalidOperationException("Keys is read before CreateAsync set it."); private set => _keys = value; }
        public MemoryServers Servers { get; } = new();
        private SwarmJoinHost? _join;
        public SwarmJoinHost Join { get => _join ?? throw new InvalidOperationException("Join is read before CreateAsync set it."); private set => _join = value; }
        private SwarmJoinClient? _client;
        public SwarmJoinClient Client { get => _client ?? throw new InvalidOperationException("Client is read before CreateAsync set it."); private set => _client = value; }
        private SwarmHeartbeat? _heartbeat;
        public SwarmHeartbeat Heartbeat { get => _heartbeat ?? throw new InvalidOperationException("Heartbeat is read before CreateAsync set it."); private set => _heartbeat = value; }
        private SwarmLeadership? _leadership;
        public SwarmLeadership Leadership { get => _leadership ?? throw new InvalidOperationException("Leadership is read before CreateAsync set it."); private set => _leadership = value; }
        private SwarmStandbyCopy? _standby;
        public SwarmStandbyCopy Standby { get => _standby ?? throw new InvalidOperationException("Standby is read before CreateAsync set it."); private set => _standby = value; }
        private SwarmNetworkPolicy? _network;
        public SwarmNetworkPolicy Network { get => _network ?? throw new InvalidOperationException("Network is read before CreateAsync set it."); private set => _network = value; }
        private SwarmSettingsStore? _swarmSettings;
        public SwarmSettingsStore SwarmSettings { get => _swarmSettings ?? throw new InvalidOperationException("SwarmSettings is read before CreateAsync set it."); private set => _swarmSettings = value; }
        private SwarmPropagation? _propagation;
        public SwarmPropagation Propagation { get => _propagation ?? throw new InvalidOperationException("Propagation is read before CreateAsync set it."); private set => _propagation = value; }
        private SwarmKick? _kick;
        public SwarmKick Kick { get => _kick ?? throw new InvalidOperationException("Kick is read before CreateAsync set it."); private set => _kick = value; }
        public bool Sleep { get; set; }
        public bool External { get; set; }
        private Cockpit.Core.Swarm.SwarmReachability? _reachability;
        public Cockpit.Core.Swarm.SwarmReachability Reachability { get => _reachability ?? throw new InvalidOperationException("Reachability is read before CreateAsync set it."); private set => _reachability = value; }
        public List<string> Notices { get; } = [];
        public bool RejectConnect { get; set; }
        public string Address { get; private set; } = "";

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
            var policy = new SwarmNetworkPolicy(new SwarmSettingsStore(Path.Combine(machine._root, "cockpit.json")),
                audit, NullLogger<SwarmNetworkPolicy>.Instance, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
            var endpoint = new Endpoint();
            machine.Network = policy;
            machine.Standby = new(Path.Combine(machine._root, "standby.json"));
            var host = new SwarmHost(kind);
            machine.Join = new(host, machine.Certificate, machine.Identity, machine.Membership, machine.Keys, machine.Settings,
                machine.Servers, [endpoint]) { Time = time, Name = Guid.NewGuid().ToString("N") };
            machine.Client = new(host, machine.Certificate, machine.Identity, machine.Membership, policy, machine.Join,
                new SwarmManualAddress(new SwarmSettingsStore(Path.Combine(machine._root, "cockpit.json")), policy), machine.Settings, new NodeDiscoveryClient());
            machine.Kick = new(machine.Membership, machine.Identity, machine.Certificate, machine.Keys, machine.Servers, policy,
                new SwarmManualAddress(new SwarmSettingsStore(Path.Combine(machine._root, "cockpit.json")), policy), NullLogger<SwarmKick>.Instance);
            machine.Propagation = new(machine.Membership, new NoticesToast(machine.Notices), machine.Kick);
            machine.Reachability = new(time);
            machine.SwarmSettings = new(Path.Combine(machine._root, "cockpit.json"));
            machine.Leadership = new(machine.Membership, machine.SwarmSettings, machine.Identity, machine.Reachability) { Time = time };
            machine.Heartbeat = new(machine.Identity, machine.Certificate, machine.Membership, machine.Propagation, policy,
                machine.Client, machine.Servers, machine.Settings, machine.Keys, machine.Reachability,
                machine.Leadership, new SwarmManualAddress(machine.SwarmSettings, policy), new HeartbeatLog(), machine.Kick) { Time = time };
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(policy);
            builder.Services.AddSingleton(machine.Client);
            builder.Services.AddSingleton(machine.Propagation);
            builder.Services.AddSingleton(machine.Membership).AddSingleton<ISwarmLeadership>(machine.Leadership).AddSingleton(machine.Standby);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = machine.Certificate.Value;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            })));
            machine._app = builder.Build();
            machine._app.Use(async (context, next) =>
            {
                if (machine.External)
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
                }
                if (machine.Sleep) { context.Response.StatusCode = 503; return; }
                if (machine.RejectConnect && context.Request.Path == "/swarm/connect")
                {
                    await Results.Json(new NodePairingProblem(NodePairingError.InvalidToken, "Sleeping"), NodePairingJson.Options, statusCode: 403).ExecuteAsync(context);
                }
                else if (context.Request.Path == "/swarm/standby")
                {
                    var token = context.Request.Headers.Authorization.ToString();
                    var caller = await machine.Keys.AuthenticateAsync(token.StartsWith("Bearer ", StringComparison.Ordinal) ? token[7..] : token, null, "127.0.0.1");
                    McpRequestContext.Set(null, caller);
                    try { await next(context); }
                    finally { McpRequestContext.Set(null); }
                }
                else
                {
                    await next(context);
                }
            });
            SwarmAssistantRelay.Map(machine._app, machine._app.Services);
            machine.Join.MapRoutes(machine._app, machine._app.Services);
            machine.Heartbeat.MapRoutes(machine._app, machine._app.Services);
            machine._app.MapGet("/remote", async (HttpContext context) =>
            {
                var token = context.Request.Headers.Authorization.ToString();
                var caller = await machine.Keys.AuthenticateAsync(token.StartsWith("Bearer ", StringComparison.Ordinal) ? token[7..] : token, null, "127.0.0.1");
                return caller is { Capability: ConnectKeyCapability.Admin, MayStartBypass: true }
                    ? Results.Ok() : Results.Unauthorized();
            });
            await machine._app.StartAsync();
            var bound = machine._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single() ?? throw new InvalidOperationException("The app bound no address.");
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
            if (Directory.Exists(_root))
            {
                try
                {
                    Directory.Delete(_root, true);
                }
                catch (DirectoryNotFoundException)
                {
                    // The folder disappeared between the check and the delete: it is already cleaned up.
                }
            }
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

    private sealed class Endpoint : ICockpitInternalMcpProvider
    {
        public string Address { get; set; } = "";
        public IReadOnlyList<McpServerConfig> GetServers() => [];
        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() => [new("cockpit-node", Address)];
    }

    private sealed class NoticesToast(List<string> notices) : Cockpit.Core.Abstractions.Toasts.IToastService
    {
        public void Show(string message, Cockpit.Core.Toasts.ToastSeverity severity, string? actionLabel = null, Action? onAction = null) => notices.Add(message);
    }

    private sealed class HeartbeatLog : Microsoft.Extensions.Logging.ILogger<SwarmHeartbeat>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Console.WriteLine(formatter(state, exception) + " " + exception);
    }
}
