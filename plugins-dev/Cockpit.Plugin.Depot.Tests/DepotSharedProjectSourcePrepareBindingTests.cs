using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.PrepareBindingAsync` (AC-246): the full definition comes from the shared REST feed.
public class DepotSharedProjectSourcePrepareBindingTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    [Fact]
    public async Task PrepareBindingAsync_FullDefinition_MapsEveryFieldOntoTheBinding()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _StubHandler(_Projects("handbook", """
            {
              "schemaVersion": 1,
              "name": "Handbook",
              "description": "Loonverwerking",
              "gitUrl": "git@github.com:example/handbook.git",
              "behaviorPrompt": "Always ask before touching prod.",
              "isolateInWorktreeByDefault": true,
              "mcpOverlay": { "enabled": ["github", "youtrack"] },
              "resources": [ { "role": "Instructions", "reference": "docs/RUNBOOK.md", "label": "Runbook" } ]
            }
            """));
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PrepareBindingAsync($"{scheme}:handbook", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var binding = result.Binding!;
        Assert.Equal("Handbook", binding.Name);
        Assert.Equal("Loonverwerking", binding.Description);
        Assert.Equal("git@github.com:example/handbook.git", binding.GitUrl);
        Assert.Equal("Always ask before touching prod.", binding.BehaviorPrompt);
        Assert.True(binding.IsolateInWorktreeByDefault);
        Assert.Equal(["github", "youtrack"], binding.EnabledMcpServerNames);
        var resource = Assert.Single(binding.Resources);
        Assert.Equal("Instructions", resource.Role);
        Assert.Equal("docs/RUNBOOK.md", resource.Reference);
        Assert.Equal("Runbook", resource.Label);
    }

    [Fact]
    public async Task PrepareBindingAsync_AnIdBelongingToADifferentConnection_FailsWithoutCallingDepotAtAll()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler("""{"projects":[]}""");
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PrepareBindingAsync("some-other-scheme:cockpit", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PrepareBindingAsync_TheDefinitionIsBrokenJson_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _StubHandler(_Projects("broken", """{"schemaVersion":"bad"}"""));
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PrepareBindingAsync($"{scheme}:broken", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    private static string _Projects(string slug, string definition) => JsonSerializer.Serialize(new
    {
        projects = new[]
        {
            new
            {
                address = $"acme/{slug}",
                slug,
                name = slug,
                role = "Editor",
                kind = "Project",
                organization = new { slug = "acme", name = "Acme", role = "Member" },
                cockpit = new
                {
                    state = "ok",
                    checksum = "chk",
                    updatedAt = "2026-10-23T12:00:00Z",
                    definition = JsonSerializer.Deserialize<JsonElement>(definition),
                },
            },
        },
    });

    private sealed class _StubHandler(string projects) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(projects),
                RequestMessage = request,
            });
        }
    }
}
