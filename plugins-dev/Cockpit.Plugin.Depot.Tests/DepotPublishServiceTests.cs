using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Publishing;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1574/AC-1637: publishing against a fake sync API that keeps documents and artifacts the way Depot does and
// remembers every file outcome.
public sealed class DepotPublishServiceTests : IDisposable
{
    private sealed class _Rig : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "depot-publish-" + Guid.NewGuid().ToString("n"));
        public string Source => Path.Combine(Root, "source");
        public Dictionary<string, string> Documents { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Artifacts { get; } = new(StringComparer.Ordinal);
        public List<(string Tool, IReadOnlyDictionary<string, object?> Arguments)> Calls { get; } = [];
        public int Puts { get; set; }
        public bool FailPut { get; set; }
        public string Role { get; set; } = "Editor";
        public InMemoryPluginCache Cache { get; } = new();
        public DepotPublishService Service { get; }
        public DepotPublication Link { get; }

        public _Rig(string target = "docs")
        {
            Directory.CreateDirectory(Source);
            var host = Substitute.For<ICockpitHost>();
            host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _DepotHandler(this));
            var http = new HttpClient(new _PutHandler(this));
            var storage = new FakePluginStorage();
            var settings = new DepotSettings(storage);
            Link = new DepotPublication("link-1", "c1", "acme/cockpit", Source, target);
            settings.Publications = [Link];
            Service = new DepotPublishService(
                host,
                () => [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")],
                settings,
                Cache,
                http);
        }

        public int Count(string tool) => Calls.Count(call => call.Tool == tool);

        public void Local(string relative, string content) => Local(relative, Encoding.UTF8.GetBytes(content));

        public void Local(string relative, byte[] content)
        {
            var path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(_Required(Path.GetDirectoryName(path)));
            File.WriteAllBytes(path, content);
        }

        public async Task<DepotPublishRequest> RunAsync(bool overwrite = false) =>
            new("c1", Id: Link.Id, Confirmed: true, OverwriteConflicts: overwrite, PlanFingerprint: (await Service.PlanAsync(PlanRequest, default)).Data?.Fingerprint);

        public DepotPublishRequest PlanRequest => new("c1", Id: Link.Id);

        public List<PublishManifestEntry> Manifest => Cache.Get<List<PublishManifestEntry>>($"publish-manifest:{Link.Id}") ?? [];

        public void Dispose()
        {
            // A directory link goes first and on its own, so deleting the tree never follows it.
            foreach (var entry in Directory.Exists(Source) ? new DirectoryInfo(Source).GetFileSystemInfos().Where(entry => entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) : [])
            {
                Directory.Delete(entry.FullName, recursive: false);
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class _DepotHandler(_Rig rig) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = _Required(request.RequestUri);
            var path = uri.AbsolutePath;
            if (path == "/cockpit/projects")
            {
                return _Json(new
                {
                    projects = new[]
                    {
                        new { slug = "cockpit", address = "acme/cockpit", name = "Cockpit", role = rig.Role },
                    },
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/files", StringComparison.Ordinal))
            {
                var prefix = _Query(uri, "path");
                return _Json(new
                {
                    entries = rig.Documents.Where(pair => prefix is null || pair.Key.StartsWith(prefix + "/", StringComparison.Ordinal))
                        .Select(pair => new { path = pair.Key, size = pair.Value.Length, checksum = Sha(pair.Value) }),
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                var prefix = _Query(uri, "path");
                return _Json(new
                {
                    entries = rig.Artifacts.Where(pair => prefix is null || pair.Key == prefix || pair.Key.StartsWith(prefix + "/", StringComparison.Ordinal))
                        .Select(pair => new { path = pair.Key, size = pair.Value.Length, checksum = Sha(pair.Value) }),
                });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/write", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await _Required(request.Content).ReadAsStringAsync(cancellationToken));
                var results = new List<object>();
                foreach (var entry in body.RootElement.EnumerateArray())
                {
                    var filePath = entry.GetProperty("path").GetString() ?? string.Empty;
                    var content = entry.GetProperty("content").GetString() ?? string.Empty;
                    var baseChecksum = entry.TryGetProperty("baseChecksum", out var guard) && guard.ValueKind == JsonValueKind.String
                        ? guard.GetString()
                        : null;
                    rig.Calls.Add(("write", new Dictionary<string, object?>
                    {
                        ["path"] = filePath,
                        ["content"] = content,
                        ["baseChecksum"] = baseChecksum,
                    }));
                    if (baseChecksum is not null && rig.Documents.TryGetValue(filePath, out var existing) && Sha(existing) != baseChecksum)
                    {
                        results.Add(new { path = filePath, status = "conflict", checksum = Sha(existing), message = "Changed in Depot." });
                        continue;
                    }

                    rig.Documents[filePath] = content;
                    results.Add(new { path = filePath, status = "written", checksum = Sha(content), message = (string?)null });
                }

                return _Json(new { results });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/uploads", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await _Required(request.Content).ReadAsStringAsync(cancellationToken));
                var share = body.RootElement.GetProperty("share").GetString() ?? "none";
                var uploads = body.RootElement.GetProperty("files").EnumerateArray().Select(file =>
                {
                    var filePath = file.GetProperty("path").GetString() ?? string.Empty;
                    rig.Calls.Add(("request_upload", new Dictionary<string, object?> { ["path"] = filePath, ["share"] = share }));
                    return new { path = filePath, uploadUrl = "https://storage.example.com/upload/" + Uri.EscapeDataString(filePath) };
                }).ToList();
                return _Json(new { uploads });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/delete", StringComparison.Ordinal))
            {
                var filePath = _Query(uri, "path") ?? string.Empty;
                var kind = _Query(uri, "kind") ?? "auto";
                var arguments = new Dictionary<string, object?>
                {
                    ["path"] = filePath,
                    ["kind"] = kind,
                    ["hard"] = string.Equals(_Query(uri, "hard"), "true", StringComparison.OrdinalIgnoreCase),
                    ["baseChecksum"] = _Query(uri, "baseChecksum"),
                };
                rig.Calls.Add(("delete", arguments));
                if (kind == "artifact")
                {
                    rig.Artifacts.Remove(filePath);
                }
                else
                {
                    rig.Documents.Remove(filePath);
                }

                return _Json(new { path = filePath });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string? _Query(Uri uri, string name) =>
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(part => Uri.UnescapeDataString(part[0]) == name)
                .Select(part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : string.Empty)
                .FirstOrDefault();

        private static HttpResponseMessage _Json<T>(T value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };
    }

    private sealed class _PutHandler(_Rig rig) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            rig.Puts++;
            if (rig.FailPut)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            var body = await _Required(request.Content).ReadAsByteArrayAsync(cancellationToken);
            var path = Uri.UnescapeDataString(_Required(request.RequestUri).AbsolutePath["/upload/".Length..]);
            rig.Artifacts[path] = body;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { path, checksum = Sha(body), bytesWritten = body.Length })),
            };
        }
    }

    private readonly List<_Rig> _rigs = [];

    private _Rig _NewRig(string target = "docs")
    {
        var rig = new _Rig(target);
        _rigs.Add(rig);
        return rig;
    }

    public void Dispose()
    {
        foreach (var rig in _rigs)
        {
            rig.Dispose();
        }
    }

    private static T _Required<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException($"Expected a {typeof(T).Name}, got null.");

    private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string Local = "line one\r\nline two\r\n";

    // A directory link without needing the privilege a symbolic link wants on Windows.
    private static void _Link(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("mklink did not start.");
            process.WaitForExit();
            Assert.True(Directory.Exists(link), "The test could not create a directory link.");
        }
    }

    // What the plan makes of a local file the Run tests below never meet: a first publication over a document Depot
    // already holds with other content is a conflict, a file without an extension is skipped, and dot entries and
    // links are not in the plan at all. `depot` is what Depot holds at docs/a.md, null for nothing.
    [Theory]
    [InlineData("a.md", "alpha\r\n", "conflict")]
    [InlineData("README", null, "skipped")]
    [InlineData(".hidden.md", null, "absent")]
    [InlineData("linked", null, "absent")]
    public async Task Plan_ClassifiesEachPath_AndLeavesDotEntriesAndLinksOut(string file, string? depot, string expected)
    {
        var rig = _NewRig();
        if (file == "linked")
        {
            var outside = Path.Combine(rig.Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
            _Link(Path.Combine(rig.Source, "linked"), outside);
        }
        else
        {
            rig.Local(file, Local);
        }

        if (depot is not null)
        {
            rig.Documents["docs/a.md"] = depot;
        }

        var answer = await rig.Service.PlanAsync(rig.PlanRequest, default);

        var items = _Required(answer.Data).Items;
        if (expected == "absent")
        {
            Assert.Empty(items);
            return;
        }

        var item = Assert.Single(items);
        Assert.Equal((file, expected), (item.Path, item.Action));
        Assert.Equal(0, rig.Count("write") + rig.Count("request_upload") + rig.Count("delete"));
    }

    // A first run adopts what Depot already holds byte for byte, writes the rest, and never touches an unchanged file again;
    // a conflict is written only after the second yes, against the checksum the plan saw, and a PUT that fails leaves no trace.
    [Fact]
    public async Task Run_AdoptsThenWritesOnlyWhatChanged_ConflictsNeedTheSecondYes_AndAFailedPutIsRetried()
    {
        var rig = _NewRig();
        rig.Local("a.md", Local);
        rig.Local("img/b.png", new byte[] { 1, 2, 3 });
        rig.Documents["docs/a.md"] = Local;

        var unconfirmed = await rig.Service.RunAsync(rig.PlanRequest, default);
        Assert.Equal(DepotBrowserStatus.Error, unconfirmed.Status);
        Assert.Equal(0, rig.Count("list"));

        var stale = await rig.Service.RunAsync((await rig.RunAsync()) with { PlanFingerprint = "of another plan" }, default);
        Assert.Equal(DepotBrowserStatus.Error, stale.Status);
        Assert.Equal(0, rig.Count("request_upload") + rig.Count("write"));

        var first = _Required((await rig.Service.RunAsync(await rig.RunAsync(), default)).Data);
        Assert.Equal((1, 1, 1), (first.Written, first.Adopted, rig.Count("request_upload")));
        Assert.Equal(0, rig.Count("write"));
        Assert.Equal(new byte[] { 1, 2, 3 }, rig.Artifacts["docs/img/b.png"]);
        Assert.Equal("none", rig.Calls.Single(call => call.Tool == "request_upload").Arguments["share"]);
        Assert.Equal([Sha(Local), Sha(new byte[] { 1, 2, 3 })], rig.Manifest.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Checksum));

        rig.Calls.Clear();
        rig.Puts = 0;
        var second = _Required((await rig.Service.RunAsync(await rig.RunAsync(), default)).Data);
        Assert.Equal((0, 2), (second.Written, second.Unchanged));
        Assert.Equal(0, rig.Count("write") + rig.Count("request_upload") + rig.Count("delete") + rig.Puts);

        rig.Local("a.md", "edited\r\n");
        rig.Calls.Clear();
        await rig.Service.RunAsync(await rig.RunAsync(), default);
        var write = Assert.Single(rig.Calls, call => call.Tool == "write");
        Assert.Equal(Sha(Local), write.Arguments["baseChecksum"]);

        rig.Documents["docs/a.md"] = "someone else\r\n";
        rig.Local("a.md", "edited again\r\n");
        rig.Calls.Clear();
        var declined = _Required((await rig.Service.RunAsync(await rig.RunAsync(overwrite: false), default)).Data);
        Assert.Equal(DepotPublishAction.Conflict, Assert.Single(declined.Problems).Action);
        Assert.Equal(0, rig.Count("write"));
        await rig.Service.RunAsync(await rig.RunAsync(overwrite: true), default);
        Assert.Equal(Sha("someone else\r\n"), Assert.Single(rig.Calls, call => call.Tool == "write").Arguments["baseChecksum"]);

        rig.Local("c.png", new byte[] { 9 });
        rig.FailPut = true;
        var failed = _Required((await rig.Service.RunAsync(await rig.RunAsync(), default)).Data);
        Assert.Equal(DepotPublishAction.Failed, Assert.Single(failed.Problems).Action);
        Assert.DoesNotContain(rig.Manifest, entry => entry.Path == "c.png");
        rig.FailPut = false;
        await rig.Service.RunAsync(await rig.RunAsync(), default);
        Assert.Contains(rig.Manifest, entry => entry.Path == "c.png");
        Assert.Equal(new byte[] { 9 }, rig.Artifacts["docs/c.png"]);
    }

    // Only what this link published is its to delete, softly and guarded; whatever else sits under the target is never called.
    [Fact]
    public async Task Run_DeletesOnlyManifestPaths_SoftlyAndGuarded_AndNeverTheStrangers()
    {
        var rig = _NewRig();
        rig.Local("a.md", "one\r\n");
        rig.Local("b.png", new byte[] { 5 });
        rig.Local("keep.md", "keep\r\n");
        await rig.Service.RunAsync(await rig.RunAsync(), default);
        rig.Documents["docs/stranger.md"] = "not ours\r\n";
        rig.Artifacts["docs/stranger.png"] = [7];
        var publishedA = Sha("one\r\n");

        File.Delete(Path.Combine(rig.Source, "a.md"));
        File.Delete(Path.Combine(rig.Source, "b.png"));
        rig.Artifacts["docs/b.png"] = [6];
        rig.Calls.Clear();
        var report = _Required((await rig.Service.RunAsync(await rig.RunAsync(), default)).Data);

        var delete = Assert.Single(rig.Calls, call => call.Tool == "delete");
        Assert.Equal(("docs/a.md", "memory", false, publishedA), (delete.Arguments["path"], delete.Arguments["kind"], delete.Arguments["hard"], delete.Arguments["baseChecksum"]));
        Assert.Equal(1, report.Deleted);
        Assert.Equal(DepotPublishAction.DeleteConflict, Assert.Single(report.Problems).Action);
        Assert.Equal(["b.png", "keep.md"], rig.Manifest.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.True(rig.Documents.ContainsKey("docs/stranger.md") && rig.Artifacts.ContainsKey("docs/stranger.png"));
    }
}
