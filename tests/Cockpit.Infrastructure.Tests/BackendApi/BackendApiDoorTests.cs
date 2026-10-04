using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Events;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Tests.Mcp;
using Cockpit.Plugins.Abstractions.Health;

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

    // AC-1446 criteria 1 and 4: every admin route turns an operate key away with the one forbidden, and no admin may
    // narrow or revoke the bootstrap key; either way nothing changed. F5.6b2–b4 add their routes as rows here.
    public static TheoryData<string, string, string?, string, HttpStatusCode, string> AdminRoutes => new()
    {
        { "GET", "/api/v1/plugins", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "GET", "/api/v1/plugins/store", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "POST", "/api/v1/plugins", """{"storeId":"store","pluginId":"plugin","version":"1.0.0"}""", "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "PUT", "/api/v1/plugins/plugin/enabled", """{"enabled":true}""", "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "DELETE", "/api/v1/plugins/plugin", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "POST", "/api/v1/plugins", """{"store":{"kind":"remote","location":"https://user:password@store.example/index.json?access_token=query-token","token":"token"},"storeId":"store","pluginId":"plugin","version":"1.0.0"}""", "admin", HttpStatusCode.BadRequest, "invalid_request" },
        { "GET", "/api/v1/keys", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "POST", "/api/v1/keys", """{"label":"more","capability":"admin"}""", "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "DELETE", "/api/v1/keys/{issued}", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "PUT", "/api/v1/keys/{issued}/scope", """{"allowAllProjects":false}""", "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "POST", "/api/v1/lockouts/10.0.0.9/lift", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "GET", "/api/v1/audit", null, "operate", HttpStatusCode.Forbidden, "forbidden" },
        { "DELETE", "/api/v1/keys/bootstra", null, "admin", HttpStatusCode.Conflict, "bootstrap_key" },
        { "PUT", "/api/v1/keys/bootstra/scope", """{"allowAllProjects":false}""", "admin", HttpStatusCode.Conflict, "bootstrap_key" },
        { "POST", "/api/v1/keys", """{"label":"odd","capability":7}""", "admin", HttpStatusCode.BadRequest, "invalid_request" },
        { "GET", "/api/v1/audit?before=-1", null, "admin", HttpStatusCode.BadRequest, "invalid_request" },
        { "GET", "/api/v1/audit?before=1", null, "admin", HttpStatusCode.BadRequest, "invalid_request" },
        { "GET", "/api/v1/audit?before=999999999", null, "admin", HttpStatusCode.BadRequest, "invalid_request" },
        { "DELETE", "/api/v1/keys/{secret}", null, "admin", HttpStatusCode.NotFound, "no_key" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AnAdminRoute_RefusesAnOperateKey_AndNoKeyRevokesTheBootstrapKey(string method, string path, string? body, string credential, HttpStatusCode expected, string error)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var operate = await verifier.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, Operator);
        var bearer = credential == "admin" ? Bootstrap : operate.Secret;

        var target = path.Replace("{issued}", operate.Key.Prefix, StringComparison.Ordinal).Replace("{secret}", operate.Secret, StringComparison.Ordinal);
        var answer = await door.SendAsync(new HttpMethod(method), target, bearer, body);
        var keys = await verifier.ListAsync();

        Assert.Equal(expected, answer.Status);
        Assert.Equal(error, JsonNode.Parse(answer.Body)?["error"]?.GetValue<string>());
        Assert.False(answer.Body.Contains(operate.Secret, StringComparison.Ordinal), "The answer repeated a full key it was given.");
        Assert.False(answer.Body.Contains("password", StringComparison.Ordinal) || answer.Body.Contains("query-token", StringComparison.Ordinal) || answer.Body.Contains("\"token\"", StringComparison.Ordinal), "The answer repeated a store credential it was given.");
        Assert.Equal(2, keys.Count(entry => entry.Key.IsUsableAt(DateTimeOffset.UtcNow)));
        Assert.Equal(ConnectKeyScope.Default, keys.Single(entry => !entry.Key.IsBootstrap).Key.EffectiveScope());
    }

    // AC-1446 criterion 2: the full key crosses once, in its issue's answer. No admin read carries it or its hash, nor
    // does the audit file, and no field is named for a credential. F5.6b2–b4 add their reads as rows here. The key
    // list also carries list_connect_keys' fields, and reading it is audited (scope items 5 and 6).
    [Theory]
    [InlineData("/api/v1/keys")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/audit, a line per page")]
    [InlineData("audit file")]
    public async Task AnAdminRead_NeverCarriesAKeyOrItsHash_WhichOnlyItsIssueAnswerCarriesOnce(string read)
    {
        await using var door = new _Door();
        await door.StartAsync();

        var issue = await door.SendAsync(HttpMethod.Post, "/api/v1/keys", Bootstrap, """{"label":"phone","capability":"operate","holdsAssistant":false}""");
        var secret = JsonNode.Parse(issue.Body)?["secret"]?.GetValue<string>() ?? "";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var prefix = JsonNode.Parse(issue.Body)?["key"]?["prefix"]?.GetValue<string>() ?? "";
        await door.GetAsync(door.NodeBase, "/api/v1/whoami", secret);
        await door.SendAsync(HttpMethod.Delete, $"/api/v1/keys/{prefix}", Bootstrap);
        // Two lines with one timestamp: paged a line at a time, a page boundary falls between them.
        var tie = DateTimeOffset.UtcNow;
        var trail = new NodeAccessAuditLog(door.AuditPath, NullLogger<NodeAccessAuditLog>.Instance);
        await trail.RecordAsync(new NodeAccessAuditEntry(tie, "unknown", null, "10.0.0.7", null, "tie-a"));
        await trail.RecordAsync(new NodeAccessAuditEntry(tie, "unknown", null, "10.0.0.7", null, "tie-b"));
        var text = read switch
        {
            "audit file" => await File.ReadAllTextAsync(door.AuditPath),
            "/api/v1/audit, a line per page" => await _AuditPagedAsync(door),
            _ => (await door.GetAsync(door.NodeBase, read, Bootstrap)).Body,
        };
        IEnumerable<string> credentialFields = read == "audit file" ? [] : _CredentialNames(JsonNode.Parse(text));
        var whole = (await door.GetAsync(door.NodeBase, "/api/v1/audit?count=500", Bootstrap)).Body;

        Assert.Equal(HttpStatusCode.Created, issue.Status);
        Assert.True(secret.StartsWith("ck_", StringComparison.Ordinal) && _Occurrences(issue.Body, secret) == 1, "The issue answer did not carry the new key exactly once.");
        Assert.False(issue.Body.Contains(hash, StringComparison.OrdinalIgnoreCase), "The issue answer carried the key's hash.");
        Assert.Contains(prefix, text, StringComparison.Ordinal);
        Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{read} carried the issued key.");
        Assert.False(text.Contains(hash, StringComparison.OrdinalIgnoreCase), $"{read} carried the issued key's hash.");
        Assert.False(text.Contains(Bootstrap, StringComparison.Ordinal), $"{read} carried the bootstrap key.");
        Assert.Empty(credentialFields);
        Assert.True(read == "/api/v1/keys" || (_Occurrences(text, "tie-a"), _Occurrences(text, "tie-b")) == (1, 1), $"{read} lost or repeated a line of the same timestamp.");
        Assert.True(read != "/api/v1/audit, a line per page" || _Ids(text).SequenceEqual(_Ids(whole)), "Paging a line at a time lost or repeated a line.");
        if (read == "/api/v1/keys")
        {
            Assert.Equal(
                new[] { "prefix", "label", "capability", "isBootstrap", "holdsAssistant", "scope", "createdAt", "expiresAt", "revokedAt", "lastUsedAt", "lastUsedFrom" },
                (JsonNode.Parse(text)?["keys"]?.AsArray() ?? []).Single(key => key?["prefix"]?.GetValue<string>() == prefix)?.AsObject().Select(property => property.Key) ?? []);
            Assert.Contains("api:list_keys", await File.ReadAllTextAsync(door.AuditPath), StringComparison.Ordinal);
        }
    }

    // Criterion 4: an API call with a holdsAssistant key leaves the assistant free; the same key on the MCP door
    // does take the line.
    [Fact]
    public async Task AnApiCall_NeverHoldsTheAssistant_WhereTheSameKeyOnTheMcpDoorDoes()
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var holding = await verifier.IssueAsync("holding", ConnectKeyCapability.Operate, 30, Operator, holdsAssistant: true);

        var api = await door.GetAsync(door.NodeBase, "/api/v1/whoami", holding.Secret);
        var afterApi = door.Presence.Current;
        using var mcp = await door.InitializeMcpAsync(holding.Secret);
        var afterMcp = door.Presence.Current;

        Assert.Equal(HttpStatusCode.OK, api.Status);
        Assert.Null(afterApi);
        Assert.Equal(HttpStatusCode.OK, mcp.StatusCode);
        Assert.Equal("holding", afterMcp?.Name);
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

    [Theory]
    [InlineData("file.txt", "project-a", HttpStatusCode.OK, "text/plain; charset=utf-8", "hello")]
    [InlineData("file.txt", "project-b", HttpStatusCode.NotFound, null, "")]
    [InlineData("../../x", "project-a", HttpStatusCode.BadRequest, "application/json", "The path leaves the project root.")]
    [InlineData("folder", "project-a", HttpStatusCode.BadRequest, "application/json", "The path names a directory.")]
    [InlineData("large.txt", "project-a", HttpStatusCode.RequestEntityTooLarge, "application/json", "The file exceeds 1 MiB.")]
    [InlineData("binary.bin", "project-a", HttpStatusCode.OK, "application/octet-stream", "")]
    public async Task ProjectFile_IsReadOnlyInsideTheKeyScope(string path, string allowedProject, HttpStatusCode status, string? contentType, string bodyPart)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var scope = new ConnectKeyScope { AllowAllProjects = false, AllowedProjectIds = [allowedProject] };
        var key = await verifier.IssueAsync("reader", ConnectKeyCapability.Operate, 30, Operator, scope: scope);

        var answer = await door.GetFileAsync($"/api/v1/projects/project-a/file?path={Uri.EscapeDataString(path)}", key.Secret);

        Assert.Equal(status, answer.Status);
        Assert.Equal(contentType, answer.ContentType);
        Assert.Contains(bodyPart, answer.Body, StringComparison.Ordinal);
    }

    // AC-1466 criterion 2: only a GET of /healthz needs no key. It is 200 with no section, names only slug-named
    // sections, and is 503 once one is unhealthy or hangs past the budget; anything else without a key stays 401.
    [Fact]
    public async Task Healthz_AnswersWithoutAKey_WithOnlyStatusAndSectionNames_WhileEveryOtherPathStays401()
    {
        await using var door = new _Door();
        await door.StartAsync();
        var empty = await door.GetAsync(door.NodeBase, "/healthz", bearer: null);
        var section = new _Section("probe");
        door.Health.Add("test", section);
        door.Health.Add("test", new _Section("/home/operator/.ssh"));
        door.Health.Add("test", new _Section("probe\n"));
        door.Health.Add("test", new _Section("probe"));
        string[] others = ["/healthz/", "/healthzx", "/healthz/rows", "/HEALTHZ", "/health", "/api/v1/health", "/api/v1/healthz", "/api/v1/whoami", "/mcp"];

        var healthy = await door.GetAsync(door.NodeBase, "/healthz", bearer: null);
        section.Healthy = false;
        var unhealthy = await door.GetAsync(door.NodeBase, "/healthz", bearer: null);
        var refused = new List<(string Path, HttpStatusCode Status)>();
        foreach (var path in others)
        {
            refused.Add((path, (await door.GetAsync(door.NodeBase, path, bearer: null)).Status));
        }

        var post = await door.SendAsync(HttpMethod.Post, "/healthz", bearer: null);
        var head = await door.SendAsync(HttpMethod.Head, "/healthz", bearer: null);
        section.Healthy = true;
        using var hanging = new _HangingSection();
        door.Health.Add("test", hanging);
        var clock = Stopwatch.StartNew();
        var hung = await door.GetAsync(door.NodeBase, "/healthz", bearer: null);
        var waited = clock.Elapsed;

        Assert.Equal(new _Answer(HttpStatusCode.OK, """{"status":"healthy","sections":[]}"""), empty);
        Assert.Equal(new _Answer(HttpStatusCode.OK, """{"status":"healthy","sections":[{"name":"probe","healthy":true}]}"""), healthy);
        Assert.Equal(new _Answer(HttpStatusCode.ServiceUnavailable, """{"status":"unhealthy","sections":[{"name":"probe","healthy":false}]}"""), unhealthy);
        Assert.Equal(others.Select(path => (path, HttpStatusCode.Unauthorized)), refused);
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (post.Status, head.Status));
        Assert.Equal(new _Answer(HttpStatusCode.ServiceUnavailable, """{"status":"unhealthy","sections":[{"name":"probe","healthy":true},{"name":"hang","healthy":false}]}"""), hung);
        Assert.True(waited < PluginHealthSections.ReadBudget + TimeSpan.FromSeconds(2), $"A hanging section held /healthz for {waited}.");
    }

    // AC-1470 criterion 1: an operate key scoped to project-a and profile "mine" reads only what lies in that scope, of
    // the keys only itself, and an action on a row outside it gets the very answer of an action that does not exist.
    [Fact]
    public async Task Health_ShowsAnOperateKeyOnlyItsScope_AndAnActionOutsideItIsTheSame404AsOneThatDoesNotExist()
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var scope = new ConnectKeyScope { AllowAllProjects = false, AllowedProjectIds = ["project-a"], AllowAllProfiles = false, AllowedProfileLabels = ["mine"] };
        var key = await verifier.IssueAsync("reader", ConnectKeyCapability.Operate, 30, Operator, scope: scope);
        door.LoginHealth.Current.Returns([
            new ProfileLoginHealth("mine", true, DateTimeOffset.UnixEpoch, null) { Provider = "claude", SignIn = ProfileSignInKind.SignedIn },
            new ProfileLoginHealth("theirs", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) { Provider = "codex", SignIn = ProfileSignInKind.Expired },
        ]);
        var section = new _ActionSection(
            new PluginHealthRow("Scheduler", PluginHealthStatus.Ok),
            new PluginHealthRow("Nightly A", PluginHealthStatus.Ok) { ProjectId = "project-a", ActionId = "run-a" },
            new PluginHealthRow("Nightly B", PluginHealthStatus.Failed) { ProjectId = "project-b", ActionId = "run-b" },
            new PluginHealthRow("Shared A", PluginHealthStatus.Ok) { ProjectId = "project-a", ActionId = "run-shared" },
            new PluginHealthRow("Shared B", PluginHealthStatus.Ok) { ProjectId = "project-b", ActionId = "run-shared" });
        door.Health.Add("test", section);

        var health = JsonNode.Parse((await door.GetAsync(door.NodeBase, "/api/v1/health", key.Secret)).Body);
        var outside = await door.SendAsync(HttpMethod.Post, "/api/v1/health/workflows/actions/run-b", key.Secret);
        var missing = await door.SendAsync(HttpMethod.Post, "/api/v1/health/workflows/actions/run-z", key.Secret);
        var noSection = await door.SendAsync(HttpMethod.Post, "/api/v1/health/nothing/actions/run-a", key.Secret);
        var shared = await door.SendAsync(HttpMethod.Post, "/api/v1/health/workflows/actions/run-shared", key.Secret);
        var inside = await door.SendAsync(HttpMethod.Post, "/api/v1/health/workflows/actions/run-a", key.Secret);
        var audit = await File.ReadAllTextAsync(door.AuditPath);

        Assert.Equal(["mine"], health?["profiles"]?.AsArray().Select(profile => profile?["label"]?.GetValue<string>()) ?? []);
        Assert.Equal(["reader"], health?["server"]?["keys"]?.AsArray().Select(shownKey => shownKey?["label"]?.GetValue<string>()) ?? []);
        Assert.Equal(["Scheduler", "Nightly A", "Shared A"], health?["sections"]?[0]?["rows"]?.AsArray().Select(row => row?["label"]?.GetValue<string>()) ?? []);
        Assert.Equal(new _Answer(HttpStatusCode.NotFound, ""), outside);
        Assert.Equal(outside, missing);
        Assert.Equal(outside, noSection);
        Assert.Equal(outside, shared);
        Assert.Equal(new _Answer(HttpStatusCode.OK, """{"section":"workflows","actionId":"run-a","succeeded":true}"""), inside);
        Assert.Equal(["run-a"], section.Runs);
        Assert.Contains("\"api:health_action\"", audit, StringComparison.Ordinal);
        Assert.Contains("workflows/run-a", audit, StringComparison.Ordinal);
        Assert.Equal(
            ["workflows/run-b", "workflows/run-z", "nothing/run-a", "workflows/run-shared"],
            audit.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line))
                .Where(line => line?["Tool"]?.GetValue<string>() == "api:health_action" && line?["Outcome"]?.GetValue<string>() == "not found"
                    && line?["KeyPrefix"]?.GetValue<string>() == key.Key.Prefix)
                .Select(line => line?["SubjectPrefix"]?.GetValue<string>()));
    }

    // AC-1470 criterion 2, with criterion 3 as the rows: nothing in /health or an action's answer is a token, a
    // credential or a key prefix; a 500-character label with control characters arrives cut and clean, and a section
    // without IPluginHealthActions offers no action.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Health_CarriesNoSecretOrKeyPrefix_AndAPluginLabelArrivesCutAndClean(bool sectionHasActions)
    {
        await using var door = new _Door();
        var verifier = await door.StartAsync();
        var key = await verifier.IssueAsync("reader", ConnectKeyCapability.Operate, 30, Operator);
        door.LoginHealth.Current.Returns([new ProfileLoginHealth("mine", true, DateTimeOffset.UnixEpoch, null) { Provider = "claude", SignIn = ProfileSignInKind.SignedIn }]);
        var label = "a\nb\u0007\u202E\u2028\U000E0001\uD800" + new string('x', 491);
        var row = new PluginHealthRow(label, PluginHealthStatus.Ok) { ActionId = "run" };
        door.Health.Add("test", sectionHasActions ? new _ActionSection(row) : new _Section("workflows", row));

        var answer = await door.GetAsync(door.NodeBase, "/api/v1/health", key.Secret);
        var action = await door.SendAsync(HttpMethod.Post, "/api/v1/health/workflows/actions/run", key.Secret);
        var health = JsonNode.Parse(answer.Body);
        var keys = await verifier.ListAsync();
        var shown = health?["sections"]?[0]?["rows"]?[0];

        Assert.Equal(500, label.Length);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(sectionHasActions ? HttpStatusCode.OK : HttpStatusCode.NotFound, action.Status);
        foreach (var body in new[] { answer.Body, action.Body })
        {
            Assert.DoesNotContain(key.Secret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Bootstrap, body, StringComparison.Ordinal);
            // The bootstrap prefix is the start of its own label here, so every prefix is checked as a whole value too.
            Assert.DoesNotContain(key.Key.Prefix, body, StringComparison.Ordinal);
            Assert.All(keys, entry => Assert.DoesNotContain($"\"{entry.Key.Prefix}\"", body, StringComparison.Ordinal));
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prefix", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(health?["server"]?["keys"]?.AsArray() ?? [], shownKey => Assert.Equal(["label", "capability", "lastUsedAt"], shownKey?.AsObject().Select(property => property.Key) ?? []));
        Assert.Equal(["label", "provider", "signIn", "lastCheck", "expiredSince", "announcedAt"], health?["profiles"]?[0]?.AsObject().Select(property => property.Key) ?? []);
        Assert.Equal("ab" + new string('x', HealthEndpoints.MaxLabelLength - 2), shown?["label"]?.GetValue<string>());
        Assert.Equal(sectionHasActions ? "run" : null, shown?["actionId"]?.GetValue<string>());
    }

    internal sealed record _Answer(HttpStatusCode Status, string Body);

    // A section with a row that must never reach /healthz.
    private static int _Occurrences(string text, string value) => text.Split(value).Length - 1;

    private static IEnumerable<long> _Ids(string audit) =>
        (JsonNode.Parse(audit)?.AsArray() ?? []).Select(entry => entry?["id"]?.GetValue<long>() ?? -1);

    // The whole audit as one array, read a line per page with each page's last id as the next cursor.
    private static async Task<string> _AuditPagedAsync(_Door door)
    {
        var all = new JsonArray();
        long? before = null;
        while (JsonNode.Parse((await door.GetAsync(door.NodeBase, $"/api/v1/audit?count=1{(before is { } id ? $"&before={id}" : "")}", Bootstrap)).Body) is JsonArray { Count: 1 } page)
        {
            before = page[0]?["id"]?.GetValue<long>();
            all.Add(page[0]?.DeepClone());
        }

        return all.ToJsonString();
    }

    // Every property name in a response that names a credential: an admin answer has no field for one to travel in.
    private static IEnumerable<string> _CredentialNames(JsonNode? node) => node switch
    {
        JsonObject properties => properties.SelectMany(property =>
            new[] { "token", "password", "secret", "hash", "apikey", "credential" }.Any(word => property.Key.Contains(word, StringComparison.OrdinalIgnoreCase))
                ? new[] { property.Key }
                : _CredentialNames(property.Value)),
        JsonArray items => items.SelectMany(_CredentialNames),
        _ => [],
    };

    private class _Section(string name, params PluginHealthRow[] rows) : IPluginHealthSection
    {
        public bool Healthy { get; set; } = true;

        public string Name => name;

        public PluginHealthReport Read() => new(Healthy, rows.Length > 0 ? rows : [new PluginHealthRow("row-label", PluginHealthStatus.Ok, DateTimeOffset.UnixEpoch)]);
    }

    // AC-1470: a section that offers actions and records each one it was asked to run.
    private sealed class _ActionSection(params PluginHealthRow[] rows) : _Section("workflows", rows), IPluginHealthActions
    {
        public List<string> Runs { get; } = [];

        public Task<PluginHealthActionResult> RunAsync(string actionId, CancellationToken cancellationToken)
        {
            Runs.Add(actionId);
            return Task.FromResult(new PluginHealthActionResult(true));
        }
    }

    // A section whose read never returns until the test ends.
    private sealed class _HangingSection : IPluginHealthSection, IDisposable
    {
        private readonly ManualResetEventSlim _released = new();

        public string Name => "hang";

        public PluginHealthReport Read()
        {
            _released.Wait();
            return new PluginHealthReport(true, []);
        }

        public void Dispose() => _released.Set();
    }

    // One node in a temp directory: cockpit.json, the audit trail and the certificate, and once started the real
    // endpoint host with cockpit-node on loopback and on an HTTPS port of its own.
    internal sealed class _Door : IAsyncDisposable
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
            PluginStores.LoadAsync().Returns(Task.FromResult<IReadOnlyList<PluginStoreConfig>>(
                [PluginStoreConfig.Remote("https://user:password@store.example/index.json?access_token=query-token", "store-token")]));
            Plugins.GetInstalledAsync().Returns(Task.FromResult<IReadOnlyList<InstalledPlugin>>([]));
        }

        public string Directory { get; }

        public string ConfigPath => Path.Combine(Directory, "state", "cockpit.json");

        public string ProjectDirectory => Path.Combine(Directory, "project");

        public string AuditPath => Path.Combine(Directory, "node-access-audit.jsonl");

        public McpAuthKey AppKey { get; } = new();

        public SessionMcpKeyring Keyring { get; } = new();

        public NodeControllerPresence Presence { get; } = new();

        public PluginHealthSections Health { get; } = new(NullLogger<PluginHealthSections>.Instance);

        public IProfileLoginHealth LoginHealth { get; } = Substitute.For<IProfileLoginHealth>();

        public NodeSessionMcpToolsTests.RecordingReadGateway ReadGateway { get; } = new();

        public NodeSessionMcpToolsTests.RecordingAgentGateway AgentGateway { get; } = new();

        public SessionRegistry Sessions { get; } = new();

        public IPluginProviderRegistry Providers { get; } = new PluginProviderRegistry();

        public IPluginAdministration Plugins { get; } = Substitute.For<IPluginAdministration>();

        public IPluginStoreConfigStore PluginStores { get; } = Substitute.For<IPluginStoreConfigStore>();

        public string NodeBase { get; private set; } = "";

        public string LoopbackBase { get; private set; } = "";

        public async Task<ConnectKeyVerifier> StartAsync()
        {
            System.IO.Directory.CreateDirectory(ProjectDirectory);
            System.IO.Directory.CreateDirectory(Path.Combine(ProjectDirectory, "folder"));
            await File.WriteAllTextAsync(Path.Combine(ProjectDirectory, "file.txt"), "hello");
            await File.WriteAllBytesAsync(Path.Combine(ProjectDirectory, "large.txt"), new byte[1024 * 1024 + 1]);
            await File.WriteAllBytesAsync(Path.Combine(ProjectDirectory, "binary.bin"), [0, 255]);
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
            services.AddSingleton<IAssistantReadGateway>(ReadGateway);
            services.AddSingleton<IAssistantAgentGateway>(AgentGateway);
            services.AddSingleton<ISessionRegistry>(Sessions);
            services.AddSingleton(Providers);
            services.AddSingleton(Plugins);
            services.AddSingleton(PluginStores);
            services.AddSingleton<IBackendEventLog>(new BackendEventLog());
            services.AddSingleton(broker);
            var editor = Substitute.For<IProjectEditor>();
            editor.FindProjectAsync("project-a").Returns(new Project("project-a", "Project A")
            {
                SourceDirectories = [new ProjectRepository(ProjectDirectory)],
            });
            services.AddSingleton(editor);
            services.AddSingleton<ISessionProfileStore>(new NodeSessionMcpToolsTests.StubProfileStore());
            services.AddSingleton(new NodeDiscoveryId(Path.Combine(Directory, "node-discovery-id.txt")));
            services.AddSingleton<IAgentMessageInbox>(new AgentMessageInbox());
            services.AddSingleton<IAssistantMemory>(new NodeSessionMcpToolsTests.StubMemory());
            services.AddSingleton(_audit);
            services.AddSingleton(verifier);
            services.AddSingleton<IConnectKeyAdministration>(verifier);
            services.AddSingleton(Presence);
            services.AddSingleton(Health);
            services.AddSingleton(LoginHealth);

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

        public async Task<_Answer> GetAsync(string baseUrl, string path, string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            using var response = await _http.SendAsync(request);
            return new _Answer(response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<_Answer> SendAsync(HttpMethod method, string path, string? bearer, string? json = null)
        {
            using var request = new HttpRequestMessage(method, NodeBase + path);
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            request.Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            return new _Answer(response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<(HttpStatusCode Status, string? ContentType, string Body)> GetFileAsync(string path, string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NodeBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await _http.SendAsync(request);
            return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), await response.Content.ReadAsStringAsync());
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
