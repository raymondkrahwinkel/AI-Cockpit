using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Tests.Mcp;

namespace Cockpit.Infrastructure.Tests.BackendApi;

// AC-1383's acceptance on the real door: cockpit-node on loopback and HTTPS, the middleware and verifier in front.
// The node is paired with every scope and a session is granted cockpit-node, so the middleware lets the pairing
// secret and that session's token through: a refusal of either can only come from the API's own door.
public sealed class BackendApiDoorTests
{
    private const string Bootstrap = "ck_bootstrapKeyForTheBackendApiDoorTests0123456";

    private const string PairingSecret = "the-pairing-secret";

    private const string UnknownKey = "ck_unknownKeyThatNoNodeEverIssued0123456789abc";

    private const string SessionPane = "pane-a";

    private const string ForbiddenBody = """{"error":"forbidden","error_description":"This cockpit endpoint is not available to this caller."}""";

    private const string InvalidTokenBody = """{"error":"invalid_token","error_description":"The cockpit did not accept this bearer token."}""";

    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);

    // Criterion 1: an operate key over HTTPS is told who it is.
    [Fact]
    public async Task Whoami_WithAnOperateKeyOverHttps_AnswersWhoTheKeyIs()
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var operate = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator);

        var answer = await door.GetAsync(door.NodeBase, "/api/v1/whoami", operate.Secret);
        var body = JsonNode.Parse(answer.Body);

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(operate.Key.Prefix, body?["keyPrefix"]?.GetValue<string>());
        Assert.Equal("laptop", body?["label"]?.GetValue<string>());
        Assert.Equal("operate", body?["capability"]?.GetValue<string>());
        Assert.Equal(Environment.MachineName, body?["node"]?.GetValue<string>());
        Assert.Equal(1, body?["apiVersion"]?.GetValue<int>());
    }

    // Criteria 1 and 2, the counter-proof: the pairing secret over HTTPS, and the app key or a session token over
    // loopback, all pass the middleware and meet the API's forbidden; a key nobody issued meets the MCP door's 401.
    [Theory]
    [InlineData("pairing secret over HTTPS", HttpStatusCode.Forbidden, ForbiddenBody)]
    [InlineData("app key over loopback", HttpStatusCode.Forbidden, ForbiddenBody)]
    [InlineData("session token over loopback", HttpStatusCode.Forbidden, ForbiddenBody)]
    [InlineData("unknown key over HTTPS", HttpStatusCode.Unauthorized, InvalidTokenBody)]
    public async Task Whoami_RefusesEveryCallerButAConnectKeyOverHttps(string caller, HttpStatusCode expected, string expectedBody)
    {
        await using var door = new _Door();
        await door.StartAsync();
        var callers = new Dictionary<string, (string Base, string Bearer)>
        {
            ["pairing secret over HTTPS"] = (door.NodeBase, PairingSecret),
            ["app key over loopback"] = (door.LoopbackBase, door.AppKey.Value),
            ["session token over loopback"] = (door.LoopbackBase, door.Keyring.TokenFor(SessionPane)),
            ["unknown key over HTTPS"] = (door.NodeBase, UnknownKey),
        };

        var answer = await door.GetAsync(callers[caller].Base, "/api/v1/whoami", callers[caller].Bearer);

        Assert.Equal(new _Answer(expected, expectedBody), answer);
    }

    // Criterion 3: the key list is admin's; an operate key is refused with the same forbidden.
    [Theory]
    [InlineData("operate key", HttpStatusCode.Forbidden)]
    [InlineData("admin key", HttpStatusCode.OK)]
    public async Task Keys_AreForAnAdminKeyOnly(string credential, HttpStatusCode expected)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var operate = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator);
        var tokens = new Dictionary<string, string>
        {
            ["operate key"] = operate.Secret,
            ["admin key"] = Bootstrap,
        };

        var answer = await door.GetAsync(door.NodeBase, "/api/v1/keys", tokens[credential]);

        Assert.Equal(expected, answer.Status);
    }

    // Scope 5 and 6: the list carries list_connect_keys' fields and never a key, and reading it is audited.
    [Fact]
    public async Task Keys_ListWhatListConnectKeysLists_AndAreAudited()
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var operate = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator);

        var answer = await door.GetAsync(door.NodeBase, "/api/v1/keys", Bootstrap);
        var keys = JsonNode.Parse(answer.Body)?["keys"]?.AsArray() ?? [];
        var issued = keys.Single(key => key?["prefix"]?.GetValue<string>() == operate.Key.Prefix);
        var audit = await File.ReadAllTextAsync(door.AuditPath);

        Assert.Equal(
            new[] { "prefix", "label", "capability", "isBootstrap", "createdAt", "expiresAt", "revokedAt", "lastUsedAt" },
            issued?.AsObject().Select(property => property.Key) ?? []);
        Assert.Equal(2, keys.Count);
        Assert.DoesNotContain(operate.Secret, answer.Body, StringComparison.Ordinal);
        Assert.Contains("api:list_keys", audit, StringComparison.Ordinal);
    }

    // Criterion 4: an API call leaves the assistant free; the same key on the MCP door does take the line.
    [Fact]
    public async Task AnApiCall_NeverHoldsTheAssistant_WhereTheSameKeyOnTheMcpDoorDoes()
    {
        await using var door = new _Door();
        await door.StartAsync();

        var api = await door.GetAsync(door.NodeBase, "/api/v1/whoami", Bootstrap);
        var afterApi = door.Presence.Current;
        using var mcp = await door.InitializeMcpAsync(Bootstrap);
        var afterMcp = door.Presence.Current;

        Assert.Equal(HttpStatusCode.OK, api.Status);
        Assert.Null(afterApi);
        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        Assert.Equal("bootstrap", afterMcp?.Name);
    }

    // Criterion 5: after a revoke the key's next API call is the door's 401.
    [Fact]
    public async Task RevokingAKey_FailsItsNextApiCall()
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var operate = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator);

        var before = await door.GetAsync(door.NodeBase, "/api/v1/whoami", operate.Secret);
        await verifier.RevokeAsync(operate.Key.Prefix, Operator);
        var after = await door.GetAsync(door.NodeBase, "/api/v1/whoami", operate.Secret);

        Assert.Equal(HttpStatusCode.OK, before.Status);
        Assert.Equal(new _Answer(HttpStatusCode.Unauthorized, InvalidTokenBody), after);
    }

    private sealed record _Answer(HttpStatusCode Status, string Body);

    // One node in a temp directory: cockpit.json, the audit trail and the certificate, and once started the real
    // endpoint host with cockpit-node on loopback and on an HTTPS port of its own.
    private sealed class _Door : IAsyncDisposable
    {
        private readonly NodeSelfSignedCertificate _certificate;
        private readonly HttpClient _http;
        private readonly NodeAccessAuditLog _audit;
        private CockpitMcpEndpointHost? _host;

        public _Door()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"backend-api-door-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            _certificate = new NodeSelfSignedCertificate(Path.Combine(Directory, "node-certificate.pfx"));
            _audit = new NodeAccessAuditLog(AuditPath, NullLogger<NodeAccessAuditLog>.Instance);
            _http = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public string Directory { get; }

        public string ConfigPath => Path.Combine(Directory, "state", "cockpit.json");

        public string AuditPath => Path.Combine(Directory, "node-access-audit.jsonl");

        public McpAuthKey AppKey { get; } = new();

        public SessionMcpKeyring Keyring { get; } = new();

        public NodeControllerPresence Presence { get; } = new();

        public string NodeBase { get; private set; } = "";

        public string LoopbackBase { get; private set; } = "";

        public async Task<ConnectKeyVerifier> StartAsync()
        {
            await new NodeEndpointSettingsStore(ConfigPath).SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = PairingSecret, Port = 0 });
            var environment = new Dictionary<string, string> { [ConnectKeyVerifier.BootstrapVariable] = Bootstrap };
            var verifier = new ConnectKeyVerifier(ConfigPath, name => environment.GetValueOrDefault(name), name => environment.Remove(name), TimeProvider.System, _audit, NullLogger.Instance);

            var pairing = new NodePairing { ControllerName = "laptop", ControllerAddress = "10.0.0.2", PairedAtUtc = DateTimeOffset.UnixEpoch, AllowAllProfiles = true, AllowAllProjects = true };
            var broker = Substitute.For<INodePairingBroker>();
            broker.Pairing.Returns(pairing);
            broker.IsProfileAllowed(Arg.Any<string>()).Returns(true);
            broker.IsProjectAllowed(Arg.Any<string>()).Returns(true);
            var mounts = new SessionMcpMounts();
            mounts.Grant(SessionPane, ["cockpit-node"]);

            var services = new ServiceCollection();
            services.AddSingleton<IAssistantReadGateway>(new NodeSessionMcpToolsTests.RecordingReadGateway());
            services.AddSingleton<IAssistantAgentGateway>(new NodeSessionMcpToolsTests.RecordingAgentGateway());
            services.AddSingleton(broker);
            services.AddSingleton<ISessionProfileStore>(new NodeSessionMcpToolsTests.StubProfileStore());
            services.AddSingleton(new NodeDiscoveryId(Path.Combine(Directory, "node-discovery-id.txt")));
            services.AddSingleton<IAgentMessageInbox>(new AgentMessageInbox());
            services.AddSingleton<IAssistantMemory>(new NodeSessionMcpToolsTests.StubMemory());
            services.AddSingleton(_audit);
            services.AddSingleton(verifier);
            services.AddSingleton(Presence);

            _host = new CockpitMcpEndpointHost(
                [new CockpitMcpEndpoint("cockpit-node", typeof(NodeSessionMcpTools), NodeOnly: true)],
                services.BuildServiceProvider(),
                AppKey,
                Keyring,
                new NodeEndpointSettingsStore(ConfigPath),
                _certificate,
                new NodeSharedSecret(),
                mounts,
                NullLoggerFactory.Instance);
            await _host.StartAsync(CancellationToken.None);
            NodeBase = _BaseOf(Assert.Single(_host.GetNodeAddresses()).Url);
            LoopbackBase = _BaseOf(Assert.Single(_host.GetServers()).Url ?? "");
            return verifier;
        }

        public async Task<_Answer> GetAsync(string baseUrl, string path, string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await _http.SendAsync(request);
            return new _Answer(response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // An MCP initialize over the node listener: an authorized call on the MCP door, as a controller's first one.
        public Task<HttpResponseMessage> InitializeMcpAsync(string bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, NodeBase + "/mcp")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"api-door-test","version":"1"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            return _http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
            }

            _http.Dispose();
            _certificate.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static string _BaseOf(string mcpUrl) => mcpUrl[..^"/mcp".Length];
    }
}
