using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.ListPublishTargetsAsync`/`PublishAsync` (AC-620): first definitions use REST create-only.
public class DepotSharedProjectSourcePublishTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    private static SharedProjectPublishDefinition Definition(
        string name = "New Project", IReadOnlyList<SharedProjectPublishResource>? resources = null) =>
        new(name, Description: "A fresh project", GitUrl: "git@github.com:example/new-project.git",
            BehaviorPrompt: "Be terse.", IsolateInWorktreeByDefault: false, EnabledMcpServerNames: ["github"],
            Resources: resources ?? []);

    [Fact]
    public async Task PublishAsync_WriteRejectedForRoleBelowEditor_ReturnsPermissionDenied()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler(
            "new-project", definition: null, writeStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PublishAsync(
            $"{scheme}:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.PermissionDenied, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PublishAsync_TargetAlreadyHasADefinition_ReturnsAlreadyPublishedAndNeverWrites()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler("existing", """{"schemaVersion":1,"name":"Existing"}""");
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PublishAsync(
            $"{scheme}:existing", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.AlreadyPublished, result.Outcome);
        Assert.Equal(0, handler.PutCount);
    }

    [Fact]
    public async Task PublishAsync_WrongScheme_FailsWithoutCallingDepot()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _DefinitionHandler("new-project", definition: null);
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PublishAsync(
            "other-scheme:new-project", Definition(), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Failed, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task PublishAsync_FirstWriteHasNoBaseChecksum_AndASecretShapedResourceRowNeverReachesTheWrittenDefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler("new-project", definition: null);
        using var http = new HttpClient(handler);
        var resources = new List<SharedProjectPublishResource>
        {
            new("Instructions", "docs/RUNBOOK.md", "Runbook"),
            new("Credential", "~/.ssh/id_rsa", "Deploy key"),
        };

        var result = await SourceFor(host, http).PublishAsync(
            $"{scheme}:new-project", Definition(resources: resources), CancellationToken.None);

        Assert.Equal(SharedProjectPublishOutcome.Success, result.Outcome);
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.DoesNotContain("id_rsa", handler.WrittenContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy key", handler.WrittenContent, StringComparison.Ordinal);
        Assert.Contains("RUNBOOK.md", handler.WrittenContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPublishTargetsAsync_KeepsOnlyEditorAndOwnerRoles_NotViewerOrUnknown()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        var scheme = _Scheme(host);
        using var httpClient = new HttpClient(new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"projects":[
              {"slug":"can-write","address":"acme/can-write","name":"Can write","role":"Editor","kind":"Project"},
              {"slug":"also-owner","name":"Also owner","role":"Owner","kind":"Project"},
              {"slug":"read-only","name":"Read only","role":"Viewer","kind":"Project"},
              {"slug":"unknown-role","name":"Unknown role","role":"Weird","kind":"Project"}
            ]}
            """),
        }));

        var result = await SourceFor(host, httpClient).ListPublishTargetsAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { $"{scheme}:acme/can-write", $"{scheme}:also-owner" }.OrderBy(id => id, StringComparer.Ordinal),
            result.Targets.Select(target => target.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    private sealed class _DefinitionHandler(
        string slug,
        string? definition,
        HttpStatusCode writeStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? IfNoneMatch { get; private set; }
        public int PutCount { get; private set; }
        public int RequestCount { get; private set; }
        public string? WrittenContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_Projects(slug, definition)),
                    RequestMessage = request,
                };
            }

            PutCount++;
            IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
            WrittenContent = await (request.Content ?? throw new InvalidOperationException("The request had no body.")).ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(writeStatus)
            {
                Content = new StringContent("""{"checksum":"chk1"}"""),
                RequestMessage = request,
            };
        }

        private static string _Projects(string projectSlug, string? value) => JsonSerializer.Serialize(new
        {
            projects = new[]
            {
                new
                {
                    address = $"acme/{projectSlug}",
                    slug = projectSlug,
                    name = projectSlug,
                    role = "Editor",
                    kind = "Project",
                    organization = new { slug = "acme", name = "Acme", role = "Member" },
                    cockpit = new
                    {
                        state = value is null ? "absent" : "ok",
                        checksum = value is null ? null : "chk",
                        updatedAt = value is null ? null : "2026-10-23T12:00:00Z",
                        definition = value is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(value),
                    },
                },
            },
        });
    }

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
