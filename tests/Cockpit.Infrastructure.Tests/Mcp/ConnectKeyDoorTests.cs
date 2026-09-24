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

    private const string ShortBootstrap = "ck_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string MonotonousBootstrap = "ck_abababababababababababababababababababababab";

    private const string SessionProfile = "Laptop Sonnet";

    private static readonly NodeCaller Operator = new("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "laptop")]
    public async Task ConnectKey_HoldsAssistantOnlyWhenRequested(bool holdsAssistant, string? expectedController)
    {
        await using var door = new _Door();
        var issued = await door.Verifier(_Environment()).IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator, holdsAssistant);
        await door.StartAsync(_Environment());
        await using var client = await door.ClientAsync(issued.Secret);

        await _CallAsync(client, "list_node_sessions", new());

        Assert.Equal(expectedController, door.Presence.Current?.Name);
    }

    [Fact]
    public async Task PairingSecret_StillHoldsAssistant()
    {
        await using var door = new _Door();
        await door.StartAsync(_Environment(), new NodePairing { ControllerName = "laptop", ControllerAddress = "10.0.0.2", PairedAtUtc = DateTimeOffset.UnixEpoch, AllowAllProfiles = true });
        await using var client = await door.ClientAsync(PairingSecret);

        await _CallAsync(client, "list_node_sessions", new());

        Assert.Equal("laptop", door.Presence.Current?.Name);
    }

    [Fact]
    public async Task LegacyKeyDefaultsToNotHolding_IssuedKeyAndBootstrapAppearInList()
    {
        await using var door = new _Door();
        var legacy = await door.Verifier(_Environment()).IssueAsync("legacy", ConnectKeyCapability.Operate, 30, Operator);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(door.ConfigPath)) ?? new JsonObject();
        Assert.True(config["NodeConnectKeys"]?["Keys"]?.AsArray()[0]?.AsObject().Remove("HoldsAssistant"));
        await File.WriteAllTextAsync(door.ConfigPath, config.ToJsonString());
        await door.StartAsync(_Environment());
        await using var legacyClient = await door.ClientAsync(legacy.Secret);
        await _CallAsync(legacyClient, "list_node_sessions", new());
        Assert.Null(door.Presence.Current);
        await using var admin = await door.ClientAsync(Bootstrap);

        var issued = await _CallAsync(admin, "issue_connect_key", new() { ["label"] = "holding", ["capability"] = "operate", ["holdsAssistant"] = true });
        var listed = await _CallAsync(admin, "list_connect_keys", new());
        var keys = listed["keys"]?.AsArray() ?? new JsonArray();

        Assert.True(keys.Single(key => key?["prefix"]?.GetValue<string>() == issued["prefix"]?.GetValue<string>())?["holdsAssistant"]?.GetValue<bool>());
        Assert.False(keys.Single(key => key?["label"]?.GetValue<string>() == "bootstrap")?["holdsAssistant"]?.GetValue<bool>());
        Assert.False(keys.Single(key => key?["label"]?.GetValue<string>() == "legacy")?["holdsAssistant"]?.GetValue<bool>());
    }

    // Criterion 1: a bootstrap key from a file, or from the variable as fallback, opens the door with no pairing and
    // nothing done on the node; with neither, or a key too short or too monotonous to be random, the same call is the
    // one refusal every failure gets. Either way the node lets go of both variables once it has read them.
    [Theory]
    [InlineData(new[] { ConnectKeyVerifier.BootstrapFileVariable }, Bootstrap, HttpStatusCode.OK)]
    [InlineData(new[] { ConnectKeyVerifier.BootstrapVariable }, Bootstrap, HttpStatusCode.OK)]
    [InlineData(new string[0], Bootstrap, HttpStatusCode.Unauthorized)]
    [InlineData(new[] { ConnectKeyVerifier.BootstrapVariable }, ShortBootstrap, HttpStatusCode.Unauthorized)]
    [InlineData(new[] { ConnectKeyVerifier.BootstrapVariable }, MonotonousBootstrap, HttpStatusCode.Unauthorized)]
    public async Task BootstrapKey_OpensTheDoorWithoutAPairing_OnlyWhenASecretSuppliesAStrongOne(string[] suppliedBy, string key, HttpStatusCode expected)
    {
        await using var door = new _Door();
        var keyFile = Path.Combine(door.Directory, "connect-key");
        await File.WriteAllTextAsync(keyFile, key + "\n");
        var values = new Dictionary<string, string>
        {
            [ConnectKeyVerifier.BootstrapFileVariable] = keyFile,
            [ConnectKeyVerifier.BootstrapVariable] = key,
        };
        var environment = suppliedBy.ToDictionary(name => name, name => values[name]);
        await door.StartAsync(environment);

        using var answer = await door.InitializeAsync(key);

        Assert.Equal(expected, answer.StatusCode);
        Assert.DoesNotContain(ConnectKeyVerifier.BootstrapFileVariable, environment.Keys);
        Assert.DoesNotContain(ConnectKeyVerifier.BootstrapVariable, environment.Keys);
    }

    // Criterion 2: every way a credential can fail answers byte for byte like a key nobody ever issued. The revoked
    // bootstrap row runs after a restart with the same secret, so it only passes if the revocation was persisted. The
    // unsaved-revocation row is a key whose revoke failed to save: loud then, and still revoked after a restart.
    [Theory]
    [InlineData("unknown key")]
    [InlineData("wrong key with a known prefix")]
    [InlineData("expired key")]
    [InlineData("revoked key")]
    [InlineData("revoked bootstrap key after a restart")]
    [InlineData("key whose revocation could not be saved, after a restart")]
    [InlineData("wrong pairing secret")]
    public async Task EveryFailedCredential_GetsTheSameAnswerAsAnUnknownKey(string credential)
    {
        await using var door = new _Door();
        var beforeRestart = door.Verifier(_Environment());
        var live = await beforeRestart.IssueAsync("live", ConnectKeyCapability.Operate, 30, Operator);
        var expiring = await beforeRestart.IssueAsync("expiring", ConnectKeyCapability.Operate, 1, Operator);
        var revoked = await beforeRestart.IssueAsync("revoked", ConnectKeyCapability.Operate, 30, Operator);
        await beforeRestart.RevokeAsync(revoked.Key.Prefix, Operator);
        await beforeRestart.RevokeAsync("bootstra", Operator);
        var unsaved = await beforeRestart.IssueAsync("unsaved", ConnectKeyCapability.Operate, 30, Operator);
        var failedRevoke = await door.WithConfigUnwritableAsync(() => beforeRestart.RevokeAsync(unsaved.Key.Prefix, Operator));
        await beforeRestart.IssueAsync("after", ConnectKeyCapability.Operate, 30, Operator);
        door.Clock.Advance(TimeSpan.FromDays(2));
        await door.StartAsync(_Environment());
        var tokens = new Dictionary<string, string>
        {
            ["unknown key"] = UnknownKey,
            ["wrong key with a known prefix"] = $"ck_{live.Key.Prefix}{new string('x', 35)}",
            ["expired key"] = expiring.Secret,
            ["revoked key"] = revoked.Secret,
            ["revoked bootstrap key after a restart"] = Bootstrap,
            ["key whose revocation could not be saved, after a restart"] = unsaved.Secret,
            ["wrong pairing secret"] = "not-the-pairing-secret",
        };

        var baseline = await door.AnswerAsync(UnknownKey);
        var answer = await door.AnswerAsync(tokens[credential]);

        Assert.IsType<InvalidOperationException>(failedRevoke);
        Assert.Contains("WARNING revoked for this run only", await File.ReadAllTextAsync(door.AuditPath), StringComparison.Ordinal);
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
        await door.StartAsync(_Environment());
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

    // AC-1367 criterion 4: a scope change reaches a controller that is already connected, from its next call —
    // the same open client, never a reconnect. The call before the change still saw the project.
    [Fact]
    public async Task SettingAKeysScope_HoldsItsOpenClientToItFromTheNextCall()
    {
        await using var door = new _Door();
        await door.StartAsync(_Environment());
        await using var admin = await door.ClientAsync(Bootstrap);
        var issued = await _CallAsync(admin, "issue_connect_key", new() { ["label"] = "laptop", ["capability"] = "operate" });
        await using var controller = await door.ClientAsync(issued["key"]?.GetValue<string>() ?? "");
        var before = await _CallAsync(controller, "list_node_projects", new());

        var set = await _CallAsync(admin, "set_connect_key_scope", new() { ["prefix"] = issued["prefix"]?.GetValue<string>(), ["projects"] = new[] { "another-project" } });
        var after = await _CallAsync(controller, "list_node_projects", new());

        Assert.True(set["ok"]?.GetValue<bool>(), set.ToJsonString());
        Assert.Equal("project-allowed", Assert.Single(before["projects"]?.AsArray() ?? new JsonArray())?["id"]?.GetValue<string>());
        Assert.Empty(after["projects"]?.AsArray() ?? new JsonArray());
    }

    // AC-1367: the bootstrap key keeps its full scope — it is admin and meant to be revoked right after setup.
    [Fact]
    public async Task SetConnectKeyScope_RefusesTheBootstrapKey_AndLeavesItsScope()
    {
        await using var door = new _Door();
        await door.StartAsync(_Environment());
        await using var admin = await door.ClientAsync(Bootstrap);

        var refused = await _CallAsync(admin, "set_connect_key_scope", new() { ["prefix"] = "bootstra", ["projects"] = new[] { "one-project" } });
        var listed = await _CallAsync(admin, "list_connect_keys", new());

        Assert.Contains("bootstrap key keeps its full scope", refused["error"]?.GetValue<string>() ?? "", StringComparison.Ordinal);
        Assert.Equal(
            """{"profiles":null,"projects":null,"mayStartBypassProfiles":true,"mayAnswerPermissions":true}""",
            Assert.Single(listed["keys"]?.AsArray() ?? new JsonArray())?["scope"]?.ToJsonString());
    }

    // AC-1367 criterion 5: a key from a cockpit.json written before scopes existed reads as every profile and
    // project, permissions on and bypass off.
    [Fact]
    public async Task AKeyStoredWithoutAScope_ReadsAsEverythingWithPermissionsButWithoutBypass()
    {
        await using var door = new _Door();
        await door.Verifier(_Environment()).IssueAsync("legacy", ConnectKeyCapability.Operate, 30, Operator);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(door.ConfigPath)) ?? new JsonObject();
        Assert.True(config["NodeConnectKeys"]?["Keys"]?.AsArray()[0]?.AsObject().Remove("Scope"));
        await File.WriteAllTextAsync(door.ConfigPath, config.ToJsonString());
        await door.StartAsync(_Environment());
        await using var admin = await door.ClientAsync(Bootstrap);

        var listed = await _CallAsync(admin, "list_connect_keys", new());

        Assert.Equal(
            """{"profiles":null,"projects":null,"mayStartBypassProfiles":false,"mayAnswerPermissions":true}""",
            (listed["keys"]?.AsArray() ?? new JsonArray()).Single(key => key?["label"]?.GetValue<string>() == "legacy")?["scope"]?.ToJsonString());
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
    [InlineData("operate key", "set_connect_key_scope", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("pairing secret", "set_connect_key_scope", NodeSessionMcpTools.AdminRefusal)]
    [InlineData("admin key", "set_connect_key_scope", null)]
    public async Task KeyTools_AreForAnAdminKeyOnly(string credential, string tool, string? refusal)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync(_Environment(), new NodePairing { ControllerName = "laptop", ControllerAddress = "10.0.0.2", PairedAtUtc = DateTimeOffset.UnixEpoch, AllowAllProfiles = true, AllowAllProjects = true });
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
            ["set_connect_key_scope"] = new() { ["prefix"] = spare.Key.Prefix, ["projects"] = new[] { "one-project" } },
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
        await door.StartAsync(_Environment());
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

    // Criterion 6: ten failed attempts — empty requests, as from a NAT neighbour — lock the address out, so the right
    // pairing secret is refused with the same answer; a valid connect key is not, since only failures are locked out.
    // Once the lockout has run out the pairing secret works again, and the next ten lock it out twice as long.
    [Fact]
    public async Task TenFailures_LockOutThePairingSecretButNotAValidKey_UntilTheLockoutRunsOut()
    {
        await using var door = new _Door();
        await door.StartAsync(_Environment(), new NodePairing { ControllerName = "laptop", ControllerAddress = "10.0.0.2", PairedAtUtc = DateTimeOffset.UnixEpoch, AllowAllProfiles = true });
        var failures = ConnectKeyPolicy.Default.FailuresBeforeLockout;
        var refusal = await door.AnswerAsync(null);
        await Task.WhenAll(Enumerable.Range(0, failures - 1).Select(_ => door.AnswerAsync(null)));

        var lockedOut = await door.AnswerAsync(PairingSecret);
        var keyDuringLockout = await door.AnswerAsync(Bootstrap);
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var afterLockout = await door.AnswerAsync(PairingSecret);
        await Task.WhenAll(Enumerable.Range(0, failures).Select(_ => door.AnswerAsync(null)));
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var stillLockedOut = await door.AnswerAsync(PairingSecret);
        door.Clock.Advance(ConnectKeyPolicy.Default.FirstLockout);
        var afterDoubleLockout = await door.AnswerAsync(PairingSecret);

        Assert.Equal(refusal, lockedOut);
        Assert.Equal(HttpStatusCode.OK, keyDuringLockout.Status);
        Assert.Equal(HttpStatusCode.OK, afterLockout.Status);
        Assert.Equal(refusal, stillLockedOut);
        Assert.Equal(HttpStatusCode.OK, afterDoubleLockout.Status);
    }

    private static Dictionary<string, string> _Environment() => new() { [ConnectKeyVerifier.BootstrapVariable] = Bootstrap };

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

        public string ConfigPath => Path.Combine(Directory, "state", "cockpit.json");

        public string AuditPath => Path.Combine(Directory, "node-access-audit.jsonl");

        public _Clock Clock { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

        public NodeSessionMcpToolsTests.RecordingReadGateway Read { get; } = new();

        public ISessionProfileStore Profiles { get; set; } = new NodeSessionMcpToolsTests.StubProfileStore();

        public NodeAccessAuditLog Audit { get; }

        public NodeControllerPresence Presence { get; } = new();

        public string NodeUrl { get; private set; } = "";

        // The environment as the node sees it at startup; what the verifier lets go of disappears from it.
        public ConnectKeyVerifier Verifier(Dictionary<string, string> environment) =>
            new(ConfigPath, name => environment.GetValueOrDefault(name), name => environment.Remove(name), Clock, Audit, _loggerFactory.CreateLogger<ConnectKeyVerifier>());

        // Runs `act` while cockpit.json cannot be written — a file where its directory should be — and hands back
        // what it threw. A file, not a missing directory: the writer would just create that.
        public async Task<Exception?> WithConfigUnwritableAsync(Func<Task> act)
        {
            var state = Path.GetDirectoryName(ConfigPath) ?? "";
            System.IO.Directory.Move(state, state + "-away");
            await File.WriteAllTextAsync(state, "");
            var thrown = await Record.ExceptionAsync(act);
            File.Delete(state);
            System.IO.Directory.Move(state + "-away", state);
            return thrown;
        }

        // The verifier the started host answers with, for a test that needs a key issued before a client connects.
        public async Task<ConnectKeyVerifier> StartAsync(Dictionary<string, string> environment, NodePairing? pairing = null)
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
            services.AddSingleton(Presence);
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

        public Task<HttpResponseMessage> InitializeAsync(string? bearer) =>
            _PostAsync(bearer, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"door-test","version":"1"}}}""");

        public async Task<_Answer> AnswerAsync(string? bearer)
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

        // A null bearer sends no Authorization header at all — the empty request a scanner or a NAT neighbour makes.
        private Task<HttpResponseMessage> _PostAsync(string? bearer, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, NodeUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

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
