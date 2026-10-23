using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): the shared-project group is built from Depot's inline REST definitions.
public class DepotSharedProjectSourceTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient)
    {
        host.Cache.Returns(new InMemoryPluginCache());
        return new DepotSharedProjectSource(Connection(), DepotMemorySource.Scheme, host, httpClient);
    }

    private static void _StubListProjects(ICockpitHost host, string json) =>
        host.CallMcpToolAsync(
                Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(json)));

    [Fact]
    public async Task ListAsync_TheProjectsEndpointProvidesProjectsWithoutDefinitionReads()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => _Ok("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Viewer","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"sha256","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Endpoint project","description":"From Depot","gitUrl":"https://example.com/repo.git"}}}]}"""));
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        var project = Assert.Single(result.Projects);
        Assert.Equal("Endpoint project", project.Name);
        Assert.Equal("depot:acme/cockpit", project.Id);
        Assert.Equal("acme", project.Organization?.Slug);
        Assert.Equal("Member", project.Organization?.Role);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_A404FallsBackOnlyForDiscoveryAndReportsThatDefinitionsNeedRest()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project"}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("REST API", result.Error, StringComparison.Ordinal);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_A304ReturnsTheCachedProjectsEndpointResult()
    {
        var host = Substitute.For<ICockpitHost>();
        var first = _Ok("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Owner"},"cockpit":{"state":"ok","checksum":"sha256","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Cached project"}}}]}""");
        first.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"projects-v1\"");
        var handler = new _QueuedHandler(first, new HttpResponseMessage(HttpStatusCode.NotModified));
        using var http = new HttpClient(handler);
        var source = SourceFor(host, http);

        await source.ListAsync(CancellationToken.None);
        var result = await source.ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Cached project", Assert.Single(result.Projects).Name);
        Assert.Equal("\"projects-v1\"", Assert.Single(handler.Requests[1].Headers.IfNoneMatch).Tag);
    }

    [Fact]
    public async Task ListAsync_AProjectWithAValidDefinition_IsIncluded()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit (Depot name)","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"chk","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Cockpit","description":"The cockpit itself"}}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var project = Assert.Single(result.Projects);
        Assert.Equal("Cockpit", project.Name);
        Assert.Equal("The cockpit itself", project.Description);
        Assert.Equal("Editor", project.Role);
        Assert.Equal("depot:acme/cockpit", project.Id);
        Assert.Empty(result.VisibleButUnreadable);
    }

    [Fact]
    public async Task ListAsync_AnAdminsProject_MayWriteBackAndShowsItsRole()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"chk","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Cockpit"}}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Admin", project.Role);
        Assert.True(project.CanWriteBack);
    }

    [Fact]
    public async Task ListAsync_ProjectsEndpointReturnsUnparsableJson_ReportsAWholeSourceFailureRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("not json");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ListAsync_ABrainKindProject_IsNeverIncluded()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/brain","slug":"brain","name":"A Brain","role":"Owner","kind":"Brain","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"chk","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Brain"}}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Projects);
    }

    [Fact]
    public async Task ListAsync_AViewersAbsentDefinition_IsReportedAsVisibleButUnreadable()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/viewer-only","slug":"viewer-only","name":"Viewer Only","role":"Viewer","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"absent","checksum":null,"updatedAt":null}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        var unreadable = Assert.Single(result.VisibleButUnreadable);
        Assert.Equal("Viewer Only", unreadable.Name);
        Assert.Equal("Viewer", unreadable.Role);
    }

    [Fact]
    public async Task ListAsync_AnUnrecognisedRoleStringWithAbsentDefinition_IsReportedAsVisibleButUnreadable()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/weird-role","slug":"weird-role","name":"Weird Role","role":"SuperAdmin","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"absent","checksum":null,"updatedAt":null}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        Assert.Single(result.VisibleButUnreadable);
    }

    [Fact]
    public async Task ListAsync_ATooLargeDefinition_DoesNotHideTheValidProjects()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/good","slug":"good","name":"Good","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"good","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Shared"}}},{"address":"acme/large","slug":"large","name":"Large","role":"Editor","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"tooLarge","checksum":null,"updatedAt":"2026-10-20T10:00:00Z"}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.Equal("Shared", Assert.Single(result.Projects).Name);
        var tooLarge = Assert.Single(result.ProjectReadStates, state => state.Id == "depot:acme/large");
        Assert.Equal(SharedProjectReadState.TooLarge, tooLarge.State);
    }

    [Fact]
    public async Task ListAsync_InlineDefinitions_PreserveServerOrder()
    {
        var host = Substitute.For<ICockpitHost>();
        var payload = JsonSerializer.Serialize(new
        {
            projects = Enumerable.Range(1, 7).Select(index => new
            {
                address = $"acme/project-{index}",
                slug = $"project-{index}",
                name = $"Project {index}",
                role = "Editor",
                kind = "Project",
                organization = new { slug = "acme", name = "Acme", role = "Member" },
                cockpit = new
                {
                    state = "ok",
                    checksum = $"chk-{index}",
                    updatedAt = "2026-10-20T10:00:00Z",
                    definition = new { schemaVersion = 1, name = $"Shared {index}" },
                },
            }),
        });
        using var http = _Client(payload);

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.Equal(7, result.Projects.Count);
        Assert.Equal(
            Enumerable.Range(1, 7).Select(index => $"depot:acme/project-{index}"),
            result.Projects.Select(project => project.Id));
    }

    private static HttpClient _Client(string json) =>
        new(new _StubHandler(_ => _Ok(json)));

    private static HttpResponseMessage _Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }

    private sealed class _QueuedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = _responses.Dequeue();
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
