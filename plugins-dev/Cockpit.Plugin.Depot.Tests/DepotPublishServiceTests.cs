using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Publishing;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1574: publishing a local folder to a Depot project, against a fake Depot that keeps documents and artifacts the way
// Depot does (SHA-256 checksums over the stored bytes, a baseChecksum that must match) and remembers every call.
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
            host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var arguments = call.ArgAt<IReadOnlyDictionary<string, object?>?>(2) ?? new Dictionary<string, object?>();
                    Calls.Add((call.ArgAt<string>(1), arguments));
                    return Task.FromResult(_Answer(call.ArgAt<string>(1), arguments));
                });
            var http = new HttpClient(new _PutHandler(this));
            var storage = new FakePluginStorage();
            var settings = new DepotSettings(storage);
            Link = new DepotPublication("link-1", "c1", "cockpit", Source, target);
            settings.Publications = [Link];
            Service = new DepotPublishService(
                new DepotMcp(host, () => [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]), settings, Cache, http);
        }

        public int Count(string tool) => Calls.Count(call => call.Tool == tool);

        public void Local(string relative, string content) => Local(relative, Encoding.UTF8.GetBytes(content));

        public void Local(string relative, byte[] content)
        {
            var path = Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public DepotPublishRequest Run(bool overwrite = false) => new("c1", Id: Link.Id, Confirmed: true, OverwriteConflicts: overwrite);

        public DepotPublishRequest PlanRequest => new("c1", Id: Link.Id);

        public void Seed(params PublishManifestEntry[] entries) => Cache.Set($"publish-manifest:{Link.Id}", entries.ToList());

        public List<PublishManifestEntry> Manifest => Cache.Get<List<PublishManifestEntry>>($"publish-manifest:{Link.Id}") ?? [];

        private PluginMcpToolCallResult _Answer(string tool, IReadOnlyDictionary<string, object?> arguments)
        {
            var path = arguments.TryGetValue("path", out var given) ? given as string : null;
            switch (tool)
            {
                case "list_projects":
                    return PluginMcpToolCallResult.Success(JsonSerializer.Serialize(new { projects = new[] { new { slug = "cockpit", name = "Cockpit", role = Role } } }));
                case "list":
                    return PluginMcpToolCallResult.Success(JsonSerializer.Serialize(new
                    {
                        entries = Documents.Where(pair => path is null || pair.Key.StartsWith(path + "/", StringComparison.Ordinal))
                            .Select(pair => new { path = pair.Key, size = pair.Value.Length, checksum = Sha(pair.Value) }),
                    }));
                case "list_artifacts":
                    return PluginMcpToolCallResult.Success(JsonSerializer.Serialize(new
                    {
                        entries = Artifacts.Where(pair => path is null || pair.Key == path || pair.Key.StartsWith(path + "/", StringComparison.Ordinal))
                            .Select(pair => new { path = pair.Key, size = pair.Value.Length, checksum = Sha(pair.Value) }),
                    }));
                case "write":
                    var key = (string)arguments["path"]!;
                    if (arguments.TryGetValue("baseChecksum", out var guard) && Documents.TryGetValue(key, out var existing) && Sha(existing) != (string?)guard)
                    {
                        return PluginMcpToolCallResult.Failed("[Conflict] the file changed since you read it");
                    }

                    Documents[key] = (string)arguments["content"]!;
                    return PluginMcpToolCallResult.Success(JsonSerializer.Serialize(new { path = key, checksum = Sha(Documents[key]) }));
                case "request_upload":
                    return PluginMcpToolCallResult.Success(JsonSerializer.Serialize(new { uploadUrl = "https://storage.example.com/upload/" + Uri.EscapeDataString(path!) }));
                case "delete":
                    if (arguments["kind"] as string == "artifact")
                    {
                        Artifacts.Remove(path!);
                    }
                    else
                    {
                        Documents.Remove(path!);
                    }

                    return PluginMcpToolCallResult.Success("{}");
                default:
                    return PluginMcpToolCallResult.Failed($"no {tool}");
            }
        }

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

    private sealed class _PutHandler(_Rig rig) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            rig.Puts++;
            if (rig.FailPut)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath["/upload/".Length..]);
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

    private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string Local = "line one\r\nline two\r\n";

    private static string _Content(string token) => token switch { "L" => Local, "A" => "alpha\r\n", _ => "beta\r\n" };

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

    // The table: what the plan makes of a local file, given what Depot holds and what the last publication left in the manifest.
    // L is the local content, A and B are other content; "-" is nothing. Dot entries and links are not in the plan at all.
    [Theory]
    [InlineData("a.md", "-", "-", "new")]
    [InlineData("a.md", "-", "A", "new")]
    [InlineData("a.md", "A", "A", "changed")]
    [InlineData("a.md", "L", "L", "unchanged")]
    [InlineData("a.md", "L", "-", "adopt")]
    [InlineData("a.md", "L", "A", "adopt")]
    [InlineData("a.md", "A", "-", "conflict")]
    [InlineData("a.md", "B", "A", "conflict")]
    [InlineData("none", "A", "A", "delete")]
    [InlineData("none", "B", "A", "delete-conflict")]
    [InlineData("README", "-", "-", "skipped")]
    [InlineData(".hidden.md", "-", "-", "absent")]
    [InlineData("linked", "-", "-", "absent")]
    public async Task Plan_ClassifiesEachPath_AndLeavesDotEntriesAndLinksOut(string file, string depot, string manifest, string expected)
    {
        var rig = _NewRig();
        if (file == "linked")
        {
            var outside = Path.Combine(rig.Root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
            _Link(Path.Combine(rig.Source, "linked"), outside);
        }
        else if (file != "none")
        {
            rig.Local(file, Local);
        }

        if (depot != "-")
        {
            rig.Documents["docs/a.md"] = _Content(depot);
        }

        if (manifest != "-")
        {
            rig.Seed(new PublishManifestEntry("a.md", DepotBrowserKind.Document, Sha(_Content(manifest)), 0, DateTimeOffset.MinValue));
        }

        var answer = await rig.Service.PlanAsync(rig.PlanRequest, default);

        var items = answer.Data!.Items;
        if (expected == "absent")
        {
            Assert.Empty(items);
            return;
        }

        var item = Assert.Single(items);
        Assert.Equal((file == "README" ? file : "a.md", expected), (item.Path, item.Action));
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

        var first = (await rig.Service.RunAsync(rig.Run(), default)).Data!;
        Assert.Equal((1, 1, 1), (first.Written, first.Adopted, rig.Count("request_upload")));
        Assert.Equal(0, rig.Count("write"));
        Assert.Equal(new byte[] { 1, 2, 3 }, rig.Artifacts["docs/img/b.png"]);
        Assert.Equal("none", rig.Calls.Single(call => call.Tool == "request_upload").Arguments["share"]);
        Assert.Equal([Sha(Local), Sha(new byte[] { 1, 2, 3 })], rig.Manifest.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry => entry.Checksum));

        rig.Calls.Clear();
        rig.Puts = 0;
        var second = (await rig.Service.RunAsync(rig.Run(), default)).Data!;
        Assert.Equal((0, 2), (second.Written, second.Unchanged));
        Assert.Equal(0, rig.Count("write") + rig.Count("request_upload") + rig.Count("delete") + rig.Puts);

        rig.Local("a.md", "edited\r\n");
        rig.Calls.Clear();
        await rig.Service.RunAsync(rig.Run(), default);
        var write = Assert.Single(rig.Calls, call => call.Tool == "write");
        Assert.Equal(Sha(Local), write.Arguments["baseChecksum"]);

        rig.Documents["docs/a.md"] = "someone else\r\n";
        rig.Local("a.md", "edited again\r\n");
        rig.Calls.Clear();
        var declined = (await rig.Service.RunAsync(rig.Run(overwrite: false), default)).Data!;
        Assert.Equal(DepotPublishAction.Conflict, Assert.Single(declined.Problems).Action);
        Assert.Equal(0, rig.Count("write"));
        await rig.Service.RunAsync(rig.Run(overwrite: true), default);
        Assert.Equal(Sha("someone else\r\n"), Assert.Single(rig.Calls, call => call.Tool == "write").Arguments["baseChecksum"]);

        rig.Local("c.png", new byte[] { 9 });
        rig.FailPut = true;
        var failed = (await rig.Service.RunAsync(rig.Run(), default)).Data!;
        Assert.Equal(DepotPublishAction.Failed, Assert.Single(failed.Problems).Action);
        Assert.DoesNotContain(rig.Manifest, entry => entry.Path == "c.png");
        rig.FailPut = false;
        await rig.Service.RunAsync(rig.Run(), default);
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
        await rig.Service.RunAsync(rig.Run(), default);
        rig.Documents["docs/stranger.md"] = "not ours\r\n";
        rig.Artifacts["docs/stranger.png"] = [7];
        var publishedA = Sha("one\r\n");

        File.Delete(Path.Combine(rig.Source, "a.md"));
        File.Delete(Path.Combine(rig.Source, "b.png"));
        rig.Artifacts["docs/b.png"] = [6];
        rig.Calls.Clear();
        var report = (await rig.Service.RunAsync(rig.Run(), default)).Data!;

        var delete = Assert.Single(rig.Calls, call => call.Tool == "delete");
        Assert.Equal(("docs/a.md", "memory", false, publishedA), (delete.Arguments["path"], delete.Arguments["kind"], delete.Arguments["hard"], delete.Arguments["baseChecksum"]));
        Assert.Equal(1, report.Deleted);
        Assert.Equal(DepotPublishAction.DeleteConflict, Assert.Single(report.Problems).Action);
        Assert.Equal(["b.png", "keep.md"], rig.Manifest.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.True(rig.Documents.ContainsKey("docs/stranger.md") && rig.Artifacts.ContainsKey("docs/stranger.png"));
    }
}
