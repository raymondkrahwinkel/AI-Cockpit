using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;
using System.Net;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): what the Projects workspace's "Shared via Depot — …" group is built
// from. Every fixture is the actual JSON text a Depot server would send, parsed by the real `list_projects`
// and definition deserializers — never a fake handing back an already-built `SharedProject` (AC-604).
public class DepotSharedProjectSourceTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient? httpClient = null)
    {
        host.Cache.Returns(new InMemoryPluginCache());
        return new DepotSharedProjectSource(
            Connection(),
            DepotMemorySource.Scheme,
            host,
            httpClient ?? new HttpClient(new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
    }

    private static void _StubListProjects(ICockpitHost host, string json) =>
        host.CallMcpToolAsync(Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(json)));

    private static void _StubRead(ICockpitHost host, string slug, PluginMcpToolCallResult result) =>
        host.CallMcpToolAsync(
            Arg.Any<string>(), "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && Equals(args["project"], slug)),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    private static void _StubReads(ICockpitHost host, Func<string, CancellationToken, Task<PluginMcpToolCallResult>> read) =>
        host.CallMcpToolAsync(
            Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var arguments = (IReadOnlyDictionary<string, object?>)call[2];
                var project = arguments["project"] as string
                    ?? throw new InvalidOperationException("The read request did not name a project.");
                return read(project, (CancellationToken)call[4]);
            });

    private static PluginMcpToolCallResult _DefinitionEnvelope(string definitionJson, string checksum = "chk") =>
        PluginMcpToolCallResult.Success(
            $$"""{"path":".cockpit/project.json","content":{{System.Text.Json.JsonSerializer.Serialize(definitionJson)}},"checksum":"{{checksum}}","size":1}""");

    [Fact]
    public async Task ListAsync_TheProjectsEndpointProvidesProjectsWithoutDefinitionReads()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Viewer","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"sha256","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Endpoint project","description":"From Depot","gitUrl":"https://example.com/repo.git"}}}]}""")
        });
        using var httpClient = new HttpClient(handler);

        var result = await SourceFor(host, httpClient).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        var project = Assert.Single(result.Projects);
        Assert.Equal("Endpoint project", project.Name);
        Assert.Equal("depot:acme/cockpit", project.Id);
        Assert.Equal("acme", project.Organization?.Slug);
        Assert.Equal("Member", project.Organization?.Role);
        await host.DidNotReceive().CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_A404FromTheProjectsEndpointFallsBackToMcp()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "cockpit", _DefinitionEnvelope("""{"schemaVersion":1,"name":"MCP project"}"""));

        var result = await SourceFor(host, httpClient).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("MCP project", Assert.Single(result.Projects).Name);
        await host.Received(1).CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_A304ReturnsTheCachedProjectsEndpointResult()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _QueuedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"projects-v1\"") },
                Content = new StringContent(
                    """{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Owner"},"cockpit":{"state":"ok","checksum":"sha256","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Cached project"}}}]}""")
            },
            new HttpResponseMessage(HttpStatusCode.NotModified));
        using var httpClient = new HttpClient(handler);
        var source = SourceFor(host, httpClient);

        await source.ListAsync(CancellationToken.None);
        var result = await source.ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Cached project", Assert.Single(result.Projects).Name);
        Assert.Equal("\"projects-v1\"", Assert.Single(handler.Requests[1].Headers.IfNoneMatch).Tag);
        await host.DidNotReceive().CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_AProjectWithAValidDefinition_IsIncluded()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit (Depot name)","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "cockpit", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Cockpit","description":"The cockpit itself"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var project = Assert.Single(result.Projects);
        Assert.Equal("Cockpit", project.Name); // the portable definition's own name wins over Depot's own project name
        Assert.Equal("The cockpit itself", project.Description);
        Assert.Equal("Editor", project.Role);
        Assert.EndsWith(":cockpit", project.Id); // "<scheme>:cockpit" — scheme is connection-derived, asserted precisely below
        Assert.Empty(result.VisibleButUnreadable);
    }

    // AC-699: the same "Admin" role the publish picker used to drop also gates AC-247's write-back — a global
    // admin may write anywhere, so reading it as Unknown made every project on the connection read-only.
    [Fact]
    public async Task ListAsync_AnAdminsProject_MayWriteBackAndShowsItsRole()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project"}]}""");
        _StubRead(host, "cockpit", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Cockpit"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Admin", project.Role);
        Assert.True(project.CanWriteBack);
    }

    [Fact]
    public async Task ListAsync_ListProjectsReturnsUnparsableJson_ReportsAWholeSourceFailureRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, "not json");

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ListAsync_ABrainKindProject_IsNeverEvenReadForADefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"a-brain","name":"A Brain","role":"Owner","kind":"Brain"}]}""");

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Projects);
        await host.DidNotReceive().CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // Depot's own access guard: read requires at least Editor today; list_projects has no such gate. Until
    // that changes (Raymond, 2026-08-02), a Viewer/Unknown-role project whose read fails must be a named,
    // visible degradation, not a silent drop.

    [Fact]
    public async Task ListAsync_AViewersProjectWhoseReadFails_IsReportedAsVisibleButUnreadable()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"viewer-only","name":"Viewer Only","role":"Viewer","kind":"Project"}]}""");
        _StubRead(host, "viewer-only", PluginMcpToolCallResult.Failed("This action requires the Editor role"));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Projects);
        var unreadable = Assert.Single(result.VisibleButUnreadable);
        Assert.Equal("Viewer Only", unreadable.Name);
        Assert.Equal("Viewer", unreadable.Role);
    }

    [Fact]
    public async Task ListAsync_AnUnrecognisedRoleStringWhoseReadFails_IsReportedAsVisibleButUnreadable()
    {
        // Unknown is ordinal 0 — the least-powerful reading of a role this build does not recognise — so it is
        // treated the same as Viewer, not silently dropped.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"weird-role","name":"Weird Role","role":"SuperAdmin","kind":"Project"}]}""");
        _StubRead(host, "weird-role", PluginMcpToolCallResult.Failed("forbidden"));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        Assert.Single(result.VisibleButUnreadable);
    }

    [Fact]
    public async Task ListAsync_AProjectThatTimesOut_DoesNotHideTheProjectsThatWereRead()
    {
        var host = Substitute.For<ICockpitHost>();
        var originalTimeout = DepotSharedProjectSource.DefinitionReadTimeout;
        var definitions = Enumerable.Range(1, 24).ToDictionary(
            index => $"project-{index}",
            _ => Task.FromResult(_DefinitionEnvelope("""{"schemaVersion":1,"name":"Shared"}""")));
        var never = new TaskCompletionSource<PluginMcpToolCallResult>();
        definitions.Add("never", never.Task);
        var projects = string.Join(',', Enumerable.Range(1, 24).Select(index => $$"""{"slug":"project-{{index}}","name":"Project {{index}}","role":"Editor","kind":"Project"}"""));
        _StubListProjects(host, $$"""{"projects":[{{projects}},{"slug":"never","name":"Never","role":"Editor","kind":"Project"}]}""");
        _StubReads(host, (slug, _) => definitions[slug]);

        DepotSharedProjectSource.DefinitionReadTimeout = TimeSpan.FromMilliseconds(30);
        try
        {
            var result = await SourceFor(host).ListAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(result.Succeeded);
            Assert.Equal(24, result.Projects.Count);
            var timeout = Assert.Single(result.ProjectReadStates, state => state.Id.EndsWith(":never", StringComparison.Ordinal));
            Assert.Equal(SharedProjectReadState.Timeout, timeout.State);
            Assert.Equal("24 of 25 project definitions read; 1 timed out.", result.Error);
        }
        finally
        {
            DepotSharedProjectSource.DefinitionReadTimeout = originalTimeout;
            never.TrySetResult(_DefinitionEnvelope("""{"schemaVersion":1,"name":"Never"}"""));
        }
    }

    [Fact]
    public async Task ListAsync_ReadingDefinitions_NeverExceedsTheConcurrencyLimit()
    {
        var host = Substitute.For<ICockpitHost>();
        var started = new SemaphoreSlim(0);
        var gate = new TaskCompletionSource<PluginMcpToolCallResult>();
        var sync = new object();
        var active = 0;
        var maximum = 0;
        var projects = string.Join(',', Enumerable.Range(1, 7).Select(index => $$"""{"slug":"project-{{index}}","name":"Project {{index}}","role":"Editor","kind":"Project"}"""));
        _StubListProjects(host, $$"""{"projects":[{{projects}}]}""");
        _StubReads(host, async (_, _) =>
        {
            var running = Interlocked.Increment(ref active);
            lock (sync)
            {
                maximum = Math.Max(maximum, running);
            }

            started.Release();
            try
            {
                return await gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var listing = SourceFor(host).ListAsync(CancellationToken.None);
        try
        {
            var firstReads = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => started.WaitAsync(TimeSpan.FromSeconds(1))));

            Assert.All(firstReads, Assert.True);
            Assert.Equal(6, maximum);
            gate.TrySetResult(_DefinitionEnvelope("""{"schemaVersion":1,"name":"Shared"}"""));
            var result = await listing;
            Assert.Equal(7, result.Projects.Count);
            Assert.Equal(
                Enumerable.Range(1, 7).Select(index => $"project-{index}"),
                result.Projects.Select(project => project.Id.Split(':').Last()));
        }
        finally
        {
            gate.TrySetResult(_DefinitionEnvelope("""{"schemaVersion":1,"name":"Shared"}"""));
        }
    }

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class _QueuedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
