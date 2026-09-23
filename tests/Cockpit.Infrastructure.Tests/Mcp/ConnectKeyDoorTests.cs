using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

// AC-1351's acceptance on the real door: the endpoint host mounting cockpit-node over HTTPS behind McpAuthMiddleware
// and ConnectKeyVerifier, reached with a pinned certificate. No pairing unless a test gives one — a headless node
// has none, and that is where a valid key used to meet a 403.
public sealed class ConnectKeyDoorTests
{
    private const string Bootstrap = "ck_bootstrapKeyForTheNodeDoorTests0123456789ab";

    private const string PairingSecret = "the-pairing-secret";

    private const string UnknownKey = "ck_unknownKeyThatNoNodeEverIssued0123456789abc";

    private const string SessionProfile = "Laptop Sonnet";

    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);

    // Criterion 1: a bootstrap key from a file, or from the variable as fallback, opens the door with no pairing and
    // nothing done on the node; with neither, the same call is the one refusal every failure gets.
    [Theory]
    [InlineData(ConnectKeyVerifier.BootstrapFileVariable, HttpStatusCode.OK)]
    [InlineData(ConnectKeyVerifier.BootstrapVariable, HttpStatusCode.OK)]
    [InlineData("NO_BOOTSTRAP_KEY_SET", HttpStatusCode.Unauthorized)]
    public async Task BootstrapKey_OpensTheDoorWithoutAPairing_OnlyWhenASecretSuppliesIt(string suppliedBy, HttpStatusCode expected)
    {
        await using var door = new _Door();
        var keyFile = Path.Combine(door.Directory, "connect-key");
        await File.WriteAllTextAsync(keyFile, Bootstrap + "\n");
        var environment = new Dictionary<string, string>
        {
            [ConnectKeyVerifier.BootstrapFileVariable] = keyFile,
            [ConnectKeyVerifier.BootstrapVariable] = Bootstrap,
        };
        await door.StartAsync(name => name == suppliedBy ? environment.GetValueOrDefault(name) : null);

        using var answer = await door.InitializeAsync(Bootstrap);

        Assert.Equal(expected, answer.StatusCode);
    }

    // Criterion 2: every way a credential can fail answers byte for byte like a key nobody ever issued. The revoked
    // bootstrap row runs after a restart with the same secret, so it only passes if the revocation was persisted.
    [Theory]
    [InlineData("unknown key")]
    [InlineData("wrong key with a known prefix")]
    [InlineData("expired key")]
    [InlineData("revoked key")]
    [InlineData("revoked bootstrap key after a restart")]
    [InlineData("wrong pairing secret")]
    public async Task EveryFailedCredential_GetsTheSameAnswerAsAnUnknownKey(string credential)
    {
        await using var door = new _Door();
        var beforeRestart = door.Verifier(_BootstrapFromVariable);
        var live = await beforeRestart.IssueAsync("live", ConnectKeyCapability.Operate, 30, Operator);
        var expiring = await beforeRestart.IssueAsync("expiring", ConnectKeyCapability.Operate, 1, Operator);
        var revoked = await beforeRestart.IssueAsync("revoked", ConnectKeyCapability.Operate, 30, Operator);
        await beforeRestart.RevokeAsync(revoked.Key.Prefix, Operator);
        await beforeRestart.RevokeAsync("bootstra", Operator);
        door.Clock.Advance(TimeSpan.FromDays(2));
        await door.StartAsync(_BootstrapFromVariable);
        var tokens = new Dictionary<string, string>
        {
            ["unknown key"] = UnknownKey,
            ["wrong key with a known prefix"] = $"ck_{live.Key.Prefix}{new string('x', 35)}",
            ["expired key"] = expiring.Secret,
            ["revoked key"] = revoked.Secret,
            ["revoked bootstrap key after a restart"] = Bootstrap,
            ["wrong pairing secret"] = "not-the-pairing-secret",
        };

        var baseline = await door.AnswerAsync(UnknownKey);
        var answer = await door.AnswerAsync(tokens[credential]);

        Assert.Equal(HttpStatusCode.Unauthorized, baseline.Status);
        Assert.Equal(baseline, answer);
    }

    // Criterion 3: revocation reaches a controller that is already connected. The node's MCP transport is stateless
    // (SDK 2.0), so what is open at that moment is a call in flight: it is cut off within a second, and the open
    // client's next call is refused. A test that reconnected after revoking would pass on any door.
    [Fact]
    public async Task RevokingAKey_CutsOffItsCallInFlight_AndFailsTheOpenClientsNextCall()
    {
        await using var door = new _Door();
        var reached = new TaskCompletionSource();
        var heldProfiles = new TaskCompletionSource<IReadOnlyList<SessionProfile>>();
        door.Profiles = Substitute.For<ISessionProfileStore>();
        door.Profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            reached.TrySetResult();
            return heldProfiles.Task;
        });
        door.Read.Sessions.Add(new AssistantSessionRow("pane-a", "the sweep", SessionProfile, "running", null, null));
        await door.StartAsync(_BootstrapFromVariable);
        await using var admin = await door.ClientAsync(Bootstrap);
        var issued = await _CallAsync(admin, "issue_connect_key", new() { ["label"] = "laptop", ["capability"] = "operate", ["expiresInDays"] = 7 });
        var key = issued["key"]?.GetValue<string>() ?? "";
        await using var controller = await door.ClientAsync(key);
        var before = await _CallAsync(controller, "list_node_sessions", new());
        var inFlight = controller.CallToolAsync("list_node_profiles").AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _CallAsync(admin, "revoke_connect_key", new() { ["prefix"] = issued["prefix"]?.GetValue<string>() });
        var cutOff = await Record.ExceptionAsync(() => inFlight.WaitAsync(TimeSpan.FromSeconds(1)));
        var nextCall = await Record.ExceptionAsync(() => controller.CallToolAsync("list_node_sessions").AsTask());
        heldProfiles.SetResult([]);

        Assert.Equal("pane-a", Assert.Single(before["sessions"]?.AsArray() ?? new JsonArray())?["paneId"]?.GetValue<string>());
        Assert.NotNull(cutOff);
        Assert.IsNotType<TimeoutException>(cutOff);
        Assert.NotNull(nextCall);
    }

    // Criterion 4: managing keys is admin's alone. An operate key and the pairing secret get the admin refusal on
    // each of the three key tools; an admin key gets no error on any of them.
    [Theory]
    [InlineData("operate key", "issue_connect_key", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("operate key", "revoke_connect_key", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("operate key", "list_connect_keys", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("pairing secret", "issue_connect_key", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("pairing secret", "revoke_connect_key", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("pairing secret", "list_connect_keys", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("admin key", "issue_connect_key", null)]
    [InlineData("admin key", "revoke_connect_key", null)]
    [InlineData("admin key", "list_connect_keys", null)]
    public async Task KeyTools_AreForAnAdminKeyOnly(string credential, string tool, string? refusal)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync(_BootstrapFromVariable, new NodePairing { ControllerName = "laptop", ControllerAddress = "10.0.0.2", PairedAtUtc = DateTimeOffset.UnixEpoch, AllowAllProfiles = true, AllowAllProjects = true });
        var operate = await verifier.IssueAsync("operate", ConnectKeyCapability.Operate, 30, Operator);
        var spare = await verifier.IssueAsync("spare", ConnectKeyCapability.Operate, 30, Operator);
        var tokens = new Dictionary<string, string>
        {
            ["operate key"] = operate.Secret,
            ["pairing secret"] = PairingSecret,
            ["admin key"] = Bootstrap,
        };
        var arguments = new Dictionary<string, Dictionary<string, object?>>
        {
            ["issue_connect_key"] = new() { ["label"] = "another", ["capability"] = "admin" },
            ["revoke_connect_key"] = new() { ["prefix"] = spare.Key.Prefix },
            ["list_connect_keys"] = new(),
        };
        await using var client = await door.ClientAsync(tokens[credential]);

        var answer = await _CallAsync(client, tool, arguments[tool]);

        Assert.Equal(refusal, answer["error"]?.GetValue<string>());
    }

    // Criterion 5: after issuing, using and revoking a key, the key itself is in none of cockpit.json, the log and
    // the audit trail. Finding the prefix in each proves the search looked where the key would have been.
    [Fact]
    public async Task AnIssuedUsedAndRevokedKey_LeavesItsPrefixButNeverItselfInConfigLogOrAudit()
    {
        await using var door = new _Door();
        await door.StartAsync(_BootstrapFromVariable);
        await using var admin = await door.ClientAsync(Bootstrap);
        var issued = await _CallAsync(admin, "issue_connect_key", new() { ["label"] = "laptop", ["capability"] = "operate" });
        var key = issued["key"]?.GetValue<string>() ?? "";
        var prefix = issued["prefix"]?.GetValue<string>() ?? "";
        await using (var controller = await door.ClientAsync(key))
        {
            await _CallAsync(controller, "read_node_inbox", new());
        }

        await _CallAsync(admin, "revoke_connect_key", new() { ["prefix"] = prefix });
        var config = await File.ReadAllTextAsync(door.ConfigPath);
        var log = door.LogText();
        var audit = await File.ReadAllTextAsync(door.AuditPath);

        Assert.StartsWith("ck_", key);
        Assert.DoesNotContain(key, config, StringComparison.Ordinal);
        Assert.Contains(prefix, config, StringComparison.Ordinal);
        Assert.DoesNotContain(key, log, StringComparison.Ordinal);
        Assert.Contains(prefix, log, StringComparison.Ordinal);
        Assert.DoesNotContain(key, audit, StringComparison.Ordinal);
        Assert.Contains(prefix, audit, StringComparison.Ordinal);
    }

    // Criterion 6: ten failures lock the address out, so the eleventh attempt is refused even with the right key and
    // with the same answer; once the lockout has run out on the clock, the right key works again. The next ten
    // failures lock it out twice as long: still refused after one lockout's time, let in after two.
    [Fact]
    public async Task TenFailures_LockTheAddressOutEvenForTheRightKey_UntilTheLockoutRunsOut()
    {
        await using var door = new _Door();
        await door.StartAsync(_BootstrapFromVariable);
        var failures = ConnectKeyPolicy.Default.FailuresBeforeLockout;
        var refusal = await door.AnswerAsync(UnknownKey);
        await Task.WhenAll(Enumerable.Range(0, failures - 1).Select(_ => door.AnswerAsync(UnknownKey)));

        var lockedOut = await door.AnswerAsync(Bootstrap);
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var afterLockout = await door.AnswerAsync(Bootstrap);
        await Task.WhenAll(Enumerable.Range(0, failures).Select(_ => door.AnswerAsync(UnknownKey)));
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var stillLockedOut = await door.AnswerAsync(Bootstrap);
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var afterDoubleLockout = await door.AnswerAsync(Bootstrap);

        Assert.Equal(refusal, lockedOut);
        Assert.Equal(HttpStatusCode.OK, afterLockout.Status);
        Assert.Equal(refusal, stillLockedOut);
        Assert.Equal(HttpStatusCode.OK, afterDoubleLockout.Status);
    }

    private static string? _BootstrapFromVariable(string name) => name == ConnectKeyVerifier.BootstrapVariable ? Bootstrap : null;

    private static async Task<JsonNode> _CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        return JsonNode.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text))) ?? new JsonObject();
    }

    // What a refusal is made of on the wire — status, challenge, content type and body — so two can be compared whole.
    private sealed record _Answer(HttpStatusCode Status, string Challenge, string ContentType, string Body);

    // One node: a temp state directory with its cockpit.json and audit trail, a settable clock, a log that keeps
    // every line, and — once started — the real endpoint host with cockpit-node on an HTTPS port of its own.
    private sealed class _Door : IAsyncDisposable
    {
        private readonly List<string> _logLines = [];
        private readonly ILoggerFactory _loggerFactory;
        private readonly NodeSelfSignedCertificate _certificate;
        private readonly HttpClient _http;
        private CockpitMcpEndpointHost? _host;

        public _Door()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"connect-key-door-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new _CollectingLoggerProvider(_logLines)));
            _certificate = new NodeSelfSignedCertificate(Path.Combine(Directory, "node-certificate.pfx"));
            Audit = new NodeAccessAuditLog(AuditPath, _loggerFactory.CreateLogger<NodeAccessAuditLog>());
            _http = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public string Directory { get; }

        public string ConfigPath => Path.Combine(Directory, "cockpit.json");

        public string AuditPath => Path.Combine(Directory, "node-access-audit.jsonl");

        public _Clock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

        public NodeSessionMcpToolsTests.RecordingReadGateway Read { get; } = new();

        public ISessionProfileStore Profiles { get; set; } = new NodeSessionMcpToolsTests.StubProfileStore();

        public NodeAccessAuditLog Audit { get; }

        public string NodeUrl { get; private set; } = "";

        public ConnectKeyVerifier Verifier(Func<string, string?> environment) =>
            new(ConfigPath, environment, Clock, Audit, _loggerFactory.CreateLogger<ConnectKeyVerifier>());

        // The verifier the started host answers with, for a test that needs a key issued before a client connects.
        public async Task<ConnectKeyVerifier> StartAsync(Func<string, string?> environment, NodePairing? pairing = null)
        {
            await new NodeEndpointSettingsStore(ConfigPath).SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = PairingSecret, Port = 0 });
            var verifier = Verifier(environment);

            var broker = Substitute.For<INodePairingBroker>();
            broker.Pairing.Returns(pairing);
            broker.IsProfileAllowed(Arg.Any<string>()).Returns(pairing is not null);
            broker.IsProjectAllowed(Arg.Any<string>()).Returns(pairing is not null);

            var services = new ServiceCollection();
            services.AddSingleton<IAssistantReadGateway>(Read);
            services.AddSingleton<IAssistantAgentGateway>(new NodeSessionMcpToolsTests.RecordingAgentGateway());
            services.AddSingleton(broker);
            services.AddSingleton(Profiles);
            services.AddSingleton(new NodeDiscoveryId(Path.Combine(Directory, "node-discovery-id.txt")));
            services.AddSingleton<IAgentMessageInbox>(new AgentMessageInbox());
            services.AddSingleton<IAssistantMemory>(new NodeSessionMcpToolsTests.StubMemory());
            services.AddSingleton(Audit);
            services.AddSingleton(verifier);

            _host = new CockpitMcpEndpointHost(
                [new CockpitMcpEndpoint("cockpit-node", typeof(NodeSessionMcpTools), NodeOnly: true)],
                services.BuildServiceProvider(),
                new McpAuthKey(),
                new SessionMcpKeyring(),
                new NodeEndpointSettingsStore(ConfigPath),
                _certificate,
                new NodeSharedSecret(),
                new SessionMcpMounts(),
                _loggerFactory);
            await _host.StartAsync(CancellationToken.None);
            NodeUrl = Assert.Single(_host.GetNodeAddresses()).Url;
            return verifier;
        }

        public string LogText()
        {
            lock (_logLines)
            {
                return string.Join("\n", _logLines);
            }
        }

        // A controller as `NodeSessionsClient` opens one: the node's certificate pinned, the key as its bearer.
        public async Task<McpClient> ClientAsync(string bearer)
        {
            var server = new McpServerConfig { Name = "door", Transport = McpTransport.Http, Url = NodeUrl, PinnedCertificateFingerprint = _certificate.Fingerprint };
            var transport = NodeCertificatePin.TransportFor(server, new HttpClientTransportOptions
            {
                Endpoint = new Uri(NodeUrl),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
            });
            return await McpClient.CreateAsync(transport);
        }

        public Task<HttpResponseMessage> InitializeAsync(string bearer) =>
            _PostAsync(bearer, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"door-test","version":"1"}}}""");

        public async Task<_Answer> AnswerAsync(string bearer)
        {
            using var response = await InitializeAsync(bearer);
            return new _Answer(
                response.StatusCode,
                string.Join(", ", response.Headers.WwwAuthenticate),
                response.Content.Headers.ContentType?.ToString() ?? "",
                await response.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
            }

            _http.Dispose();
            _certificate.Dispose();
            _loggerFactory.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        private Task<HttpResponseMessage> _PostAsync(string bearer, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, NodeUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            return _http.SendAsync(request);
        }
    }

    internal sealed class _Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class _CollectingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new _CollectingLogger(lines);

        public void Dispose()
        {
        }
    }

    // Formatted lines only, at the level the cockpit's own log keeps: what would land in the operator's log file.
    private sealed class _CollectingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}
