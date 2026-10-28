using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1568/AC-1637/AC-1638: project discovery, file operations and shares use REST; MCP remains agent-facing.
public class DepotBrowserServiceTests
{
    private const string Projects = """{"projects":[{"slug":"cockpit","address":"acme/cockpit","name":"Cockpit","role":"{ROLE}"}]}""";

    // A host whose MCP tools answer from `Tools` (tool name → answer) and which remembers every call.
    private sealed class _Rig
    {
        public Dictionary<string, Func<IReadOnlyDictionary<string, object?>, PluginMcpToolCallResult>> Tools { get; } = new();
        public List<(string Tool, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];
        public List<(HttpMethod Method, string Path, string Body)> RestCalls { get; } = [];
        public ICockpitHost Host { get; } = Substitute.For<ICockpitHost>();
        public DepotBrowserService Service { get; }
        public Func<HttpRequestMessage, string, string>? RestResponse { get; set; }
        public HttpStatusCode FileStatus { get; set; } = HttpStatusCode.OK;
        public Action? BeforeProjectsResponse { get; set; }
        public IReadOnlyList<DepotConnectionRegistration> Connections { get; set; } =
            [new("c1", "Work", "https://depot.example.com")];

        public _Rig(string role = "Editor")
        {
            Host.Cache.Returns(new InMemoryPluginCache());
            Host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _DepotHandler(this, Projects.Replace("{ROLE}", role)));
            Host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var tool = call.ArgAt<string>(1);
                    var arguments = call.ArgAt<IReadOnlyDictionary<string, object?>?>(2) ?? new Dictionary<string, object?>();
                    Calls.Add((tool, arguments));
                    return Task.FromResult(Tools.TryGetValue(tool, out var answer) ? answer(arguments) : PluginMcpToolCallResult.Failed($"no {tool}"));
                });
            Service = new DepotBrowserService(Host, () => Connections);
        }

        public int Count(string tool) => Calls.Count(call => call.Tool == tool);

        public int CountRest(string operation) => RestCalls.Count(call => call.Path.Contains($"/{operation}", StringComparison.Ordinal));
    }

    private sealed class _DepotHandler(_Rig rig, string projects) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request had no URI.");
            if (uri.AbsolutePath == "/cockpit/projects")
            {
                rig.BeforeProjectsResponse?.Invoke();
                return _Json(projects);
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            rig.RestCalls.Add((request.Method, uri.PathAndQuery, body));
            if (rig.FileStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(rig.FileStatus);
            }

            return _Json(rig.RestResponse?.Invoke(request, body) ?? _Default(uri.AbsolutePath, body));
        }

        private static string _Default(string path, string body)
        {
            if (path.EndsWith("/write", StringComparison.Ordinal))
            {
                using var request = JsonDocument.Parse(body);
                var entry = request.RootElement[0];
                var filePath = entry.GetProperty("path").GetString() ?? string.Empty;
                var content = entry.GetProperty("content").GetString() ?? string.Empty;
                return JsonSerializer.Serialize(new
                {
                    results = new[] { new { path = filePath, status = "written", checksum = _Sha(content) } },
                });
            }

            if (path.EndsWith("/uploads", StringComparison.Ordinal))
            {
                using var request = JsonDocument.Parse(body);
                var filePath = request.RootElement.GetProperty("files")[0].GetProperty("path").GetString() ?? string.Empty;
                return JsonSerializer.Serialize(new
                {
                    uploads = new[] { new { path = filePath, uploadUrl = "https://d/blob/upload/abc" } },
                });
            }

            return path.EndsWith("/read", StringComparison.Ordinal)
                ? """{"files":[],"missing":[],"truncated":false}"""
                : """{"entries":[]}""";
        }

        private static HttpResponseMessage _Json(string content) =>
            new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

        private static string _Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static DepotBrowserRequest _Request(string? path = null, string? mode = null, string? shareId = null) =>
        new("c1", "acme/cockpit", path, mode, shareId);

    private static string _TreeResponse(HttpRequestMessage request, string _) =>
        (request.RequestUri ?? throw new InvalidOperationException("The request had no URI.")).AbsolutePath.EndsWith("/shares", StringComparison.Ordinal)
            ? """{"shares":[{"shareId":"s1","path":"img/b.png","isPublic":true,"url":"https://d/artifact/p/s1"}]}"""
            : request.RequestUri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal)
            ? """{"entries":[{"path":"img/a.png","contentType":"image/png","size":10},{"path":"img/b.png","contentType":"image/png","size":11}]}"""
            : request.RequestUri.Query.Contains("after=", StringComparison.Ordinal)
                ? """{"entries":[{"path":"docs/page-two.md","size":9,"updatedAt":"2026-08-29T17:08:18+00:00"}]}"""
                : """{"entries":[{"path":"docs/page-one.md","size":5}],"nextCursor":"docs/page-one.md"}""";

    private static string _ShareResponse(HttpRequestMessage request, string _) => request.Method == HttpMethod.Post
        ? """{"shareId":"s9","url":"https://d/artifact/p/s9","isPublic":true}"""
        : """{"revoked":true}""";

    [Fact]
    public async Task Tree_ReadsEveryPage_MergesArtifacts_AndMarksSharesFromOneListShares()
    {
        var rig = new _Rig();
        rig.RestResponse = _TreeResponse;

        var answer = await rig.Service.TreeAsync(_Request(), default);

        Assert.NotNull(answer.Data);
        var nodes = answer.Data.Nodes;
        Assert.Equal(["docs/page-one.md", "docs/page-two.md", "img/a.png", "img/b.png"], nodes.Select(node => node.Path));
        Assert.Equal(DepotBrowserKind.Document, nodes[1].Kind);
        Assert.Equal(DepotBrowserKind.Artifact, nodes[2].Kind);
        Assert.Null(nodes[2].ShareId);
        Assert.Equal(("s1", true), (nodes[3].ShareId, nodes[3].IsPublic));
        Assert.Equal(1, rig.CountRest("shares"));
        Assert.True(answer.Data.CanShare);
    }

    [Fact]
    public async Task Share_PassesTheMode_ButOnlyForAnEditor_AndUnshareRevokesByShareId()
    {
        var editor = new _Rig("Editor");
        editor.RestResponse = _ShareResponse;

        var shared = await editor.Service.ShareAsync(_Request("img/a.png", "public"), default);
        var revoked = await editor.Service.UnshareAsync(_Request(shareId: "s9"), default);

        Assert.Equal(new DepotBrowserShare("s9", "https://d/artifact/p/s9", true), shared.Data);
        Assert.Equal((HttpMethod.Post, "/sync/acme/cockpit/shares", "{\"path\":\"img/a.png\",\"mode\":\"public\"}"), editor.RestCalls[0]);
        Assert.Equal((HttpMethod.Delete, "/sync/acme/cockpit/shares/s9", string.Empty), editor.RestCalls[1]);
        Assert.True(revoked.Data);

        var viewer = new _Rig("Viewer");
        var refusedShare = await viewer.Service.ShareAsync(_Request("img/a.png", "members"), default);
        var refusedUnshare = await viewer.Service.UnshareAsync(_Request(shareId: "s9"), default);

        Assert.Equal(DepotBrowserStatus.Error, refusedShare.Status);
        Assert.Equal(DepotBrowserStatus.Error, refusedUnshare.Status);
        Assert.Empty(viewer.RestCalls);
    }

    // AC-1573: a binary file asks for a ticket without a share (the default would leave one behind on a failed PUT);
    // a text file is a document, which the artifact store refuses.
    [Theory]
    [InlineData("img/a.png", "uploads", "\"share\":\"none\"", "https://d/blob/upload/abc")]
    [InlineData("docs/a.md", "write", "\"content\":\"# hi\"", null)]
    public async Task Upload_RoutesByKind_AndAskedForNoShare(
        string path, string operation, string requestFragment, string? uploadUrl)
    {
        var rig = new _Rig();

        var answer = await rig.Service.UploadAsync(_Request(path) with { Content = "# hi", ContentType = "image/png", Size = 3 }, default);

        Assert.Equal(DepotBrowserStatus.Ok, answer.Status);
        var request = Assert.Single(rig.RestCalls);
        Assert.Contains($"/{operation}", request.Path, StringComparison.Ordinal);
        Assert.Contains(requestFragment, request.Body, StringComparison.Ordinal);
        Assert.NotNull(answer.Data);
        Assert.Equal(uploadUrl, answer.Data.UploadUrl);
    }

    [Fact]
    public async Task Read_RefusesATruncatedAnswer_InsteadOfShowingPartialContent()
    {
        await _ReadSaveContractAsync();
        var rig = new _Rig
        {
            RestResponse = (_, _) => """{"files":[{"path":"large.md","content":"partial"}],"missing":[],"truncated":true}""",
        };

        var answer = await rig.Service.ReadAsync(_Request("large.md"), default);

        Assert.Equal(DepotBrowserStatus.Error, answer.Status);
        Assert.Contains("truncated", answer.Message, StringComparison.OrdinalIgnoreCase);
        var truncated = await Assert.ThrowsAsync<FileSourceException>(() =>
            _Source(rig).ReadAsync(DepotFileRefs.Create("c1", "acme/cockpit", "large.md"), default));
        Assert.Equal(FileSourceErrorKind.TooLarge, truncated.Kind);
    }

    [Fact]
    public async Task Upload_NeedsTheEditorRole_AViewerWritesNothing()
    {
        await _CreateContractAsync();
        var viewer = new _Rig("Viewer");

        var binary = await viewer.Service.UploadAsync(_Request("a.png"), default);
        var text = await viewer.Service.UploadAsync(_Request("a.md") with { Content = "x" }, default);

        Assert.Equal((DepotBrowserStatus.Error, DepotBrowserStatus.Error), (binary.Status, text.Status));
        Assert.Equal(0, viewer.CountRest("uploads") + viewer.CountRest("write"));
        var source = _Source(viewer);
        using var rejectedContent = new MemoryStream(Encoding.UTF8.GetBytes("not allowed"));
        var forbidden = await Assert.ThrowsAsync<FileSourceException>(() =>
            source.WriteAsync(DepotFileRefs.Create("c1", "acme/cockpit", "new.md"), rejectedContent, null, true, default));
        Assert.Equal(FileSourceErrorKind.Forbidden, forbidden.Kind);
        Assert.Equal(0, viewer.CountRest("write"));

        // AC-1574: publishing is a write path too, refused for a Viewer on add, plan and run alike.
        var settings = new Cockpit.Plugin.Depot.Settings.DepotSettings(new FakePluginStorage());
        settings.Publications = [new DepotPublication("p1", "c1", "acme/cockpit", Path.GetTempPath(), "docs")];
        var publisher = new Cockpit.Plugin.Depot.Publishing.DepotPublishService(
            viewer.Host,
            () => [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")],
            settings,
            new InMemoryPluginCache(),
            new HttpClient());
        var refusals = new[]
        {
            (await publisher.AddAsync(new DepotPublishRequest("c1", "cockpit", Source: Path.GetTempPath()), default)).Status,
            (await publisher.PlanAsync(new DepotPublishRequest("c1", Id: "p1"), default)).Status,
            (await publisher.RunAsync(new DepotPublishRequest("c1", Id: "p1", Confirmed: true), default)).Status,
        };
        Assert.All(refusals, status => Assert.Equal(DepotBrowserStatus.Error, status));
        Assert.Equal(0, viewer.CountRest("files") + viewer.CountRest("artifacts") + viewer.CountRest("delete"));
    }

    private static IFileSource _Source(_Rig rig) => new Files.DepotFileSourceProvider(rig.Host,
        () => rig.Connections).Open("c1");

    private static async Task _ReadSaveContractAsync()
    {
        var depot = new _ConditionalDepot();
        depot.Files["a.md"] = "# original";
        var rig = new _Rig { RestResponse = depot.Respond };
        var source = _Source(rig);
        var file = DepotFileRefs.Create("c1", "acme/cockpit", "a.md");
        var read = await source.ReadAsync(file, default);
        Assert.True(read.CanWrite);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("# original", await reader.ReadToEndAsync());
        using var text = new MemoryStream(Encoding.UTF8.GetBytes("# saved"));
        var saved = await source.WriteAsync(file, text, read.Version, false, default);
        Assert.Equal(FileWriteStatus.Written, saved.Status);
        Assert.Equal("# saved", depot.Files["a.md"]);
        using var staleText = new MemoryStream(Encoding.UTF8.GetBytes("# stale"));
        var stale = await source.WriteAsync(file, staleText, read.Version, false, default);
        Assert.Equal(new FileWriteResult(FileWriteStatus.Conflict, saved.Version), stale);
        Assert.Equal("# saved", depot.Files["a.md"]);
        depot.Files.Remove("a.md");
        using var deletedText = new MemoryStream(Encoding.UTF8.GetBytes("# deleted"));
        var deleted = await source.WriteAsync(file, deletedText, saved.Version, false, default);
        Assert.Equal(FileWriteStatus.Conflict, deleted.Status);
        Assert.False(depot.Files.ContainsKey("a.md"));
    }

    private static async Task _CreateContractAsync()
    {
        await _ConnectionReplacementAsync();
        var depot = new _ConditionalDepot();
        var rig = new _Rig { RestResponse = depot.Respond };
        var source = _Source(rig);
        var file = DepotFileRefs.Create("c1", "acme/cockpit", "new.md");
        using var empty = new MemoryStream();
        var created = await source.WriteAsync(file, empty, null, true, default);
        Assert.Equal(FileWriteStatus.Written, created.Status);
        Assert.False(string.IsNullOrEmpty(created.Version));
        using var duplicate = new MemoryStream(Encoding.UTF8.GetBytes("duplicate"));
        var conflict = await source.WriteAsync(file, duplicate, null, true, default);
        Assert.Equal(new FileWriteResult(FileWriteStatus.Conflict, created.Version), conflict);
        Assert.Equal("", depot.Files["new.md"]);
        Assert.All(rig.RestCalls.Where(c => c.Path.EndsWith("/write", StringComparison.Ordinal)),
            c => Assert.Contains("\"baseChecksum\":\"\"", c.Body, StringComparison.Ordinal));
        using var unguarded = new MemoryStream();
        await Assert.ThrowsAsync<FileSourceException>(() => source.WriteAsync(file, unguarded, null, false, default));
        Assert.Equal(2, rig.CountRest("write"));
        using var forgedContent = new MemoryStream();
        var forged = await Assert.ThrowsAsync<FileSourceException>(() => source.WriteAsync(
            file with { Path = "../escape.md" }, forgedContent, null, true, default));
        Assert.Equal(FileSourceErrorKind.Unsupported, forged.Kind);
        Assert.Equal(2, rig.CountRest("write"));
    }

    private static async Task _ConnectionReplacementAsync()
    {
        var depot = new _ConditionalDepot();
        var rig = new _Rig { RestResponse = depot.Respond };
        var source = _Source(rig);
        rig.BeforeProjectsResponse = () => rig.Connections = [new("c1", "Work", "https://different.example.com")];
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("# original Depot"));
        var changed = await Assert.ThrowsAsync<FileSourceException>(() => source.WriteAsync(
            DepotFileRefs.Create("c1", "acme/cockpit", "a.md"), content, null, true, default));
        Assert.Equal(FileSourceErrorKind.Unavailable, changed.Kind);
        Assert.Empty(rig.RestCalls);
        Assert.Empty(depot.Files);
    }

    private sealed class _ConditionalDepot
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        private static string _Checksum(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        public string Respond(HttpRequestMessage request, string body)
        {
            using var json = JsonDocument.Parse(body);
            var uri = request.RequestUri ?? throw new InvalidOperationException("The request had no URI.");
            if (uri.AbsolutePath.EndsWith("/read", StringComparison.Ordinal))
            {
                var path = json.RootElement[0].GetString() ?? throw new InvalidOperationException("The read request had no path.");
                return JsonSerializer.Serialize(new { files = new[] { new { path, content = Files[path], checksum = _Checksum(Files[path]) } }, truncated = false });
            }
            var entry = json.RootElement[0];
            var target = entry.GetProperty("path").GetString() ?? throw new InvalidOperationException("The write request had no path.");
            var current = Files.TryGetValue(target, out var content) ? _Checksum(content) : "";
            var expected = entry.GetProperty("baseChecksum").GetString();
            if (expected is not null && expected != current)
                return JsonSerializer.Serialize(new { results = new[] { new { path = target, status = "conflict", checksum = current } } });
            Files[target] = entry.GetProperty("content").GetString() ?? throw new InvalidOperationException("The write request had no content.");
            return JsonSerializer.Serialize(new { results = new[] { new { path = target, status = "written", checksum = _Checksum(Files[target]) } } });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DepotBrowserStatus.SignIn, "Sign in to Work")]
    [InlineData(HttpStatusCode.Forbidden, DepotBrowserStatus.Error, "Depot did not accept the file operation.")]
    public async Task Tree_NotSignedInOrRefused_SaysSoWithoutSigningIn(HttpStatusCode response, string status, string message)
    {
        var rig = new _Rig { FileStatus = response };

        var answer = await rig.Service.TreeAsync(_Request(), default);

        Assert.Equal((status, message), (answer.Status, answer.Message));
        await rig.Host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
