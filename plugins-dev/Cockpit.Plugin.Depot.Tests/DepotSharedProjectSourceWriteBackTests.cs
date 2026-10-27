using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.WriteBackAsync` (AC-247): the operator's edit lands through Depot's REST definition route.
public class DepotSharedProjectSourceWriteBackTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    private static SharedProjectDefinitionEdit Edit(
        string name = "Edited name", string? description = "Edited description", SharedProjectLogoEdit? logoEdit = null,
        string? gitUrl = "https://example.com/edited.git") =>
        new(name, description, BehaviorPrompt: "Edited behaviour", IsolateInWorktreeByDefault: true,
            EnabledMcpServerNames: ["github"], LogoEdit: logoEdit, GitUrl: gitUrl);

    [Fact]
    public async Task WriteBackAsync_Success_ReturnsTheChecksumTheWriteConfirmed()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler("""{"schemaVersion":1,"name":"Cockpit"}""", writtenChecksum: "chk-after");
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).WriteBackAsync(
            $"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Success, result.Outcome);
        Assert.Equal("chk-after", result.Checksum);
        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(handler.WrittenContent, out var sent, out var error), error);
        Assert.Equal("https://example.com/edited.git", sent!.GitUrl);
    }

    [Fact]
    public async Task WriteBackAsync_SendsTheOperatorsBaseChecksumRatherThanTheFreshReadsOwn()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler(
            """{"schemaVersion":1,"name":"Cockpit"}""", currentChecksum: "chk-fresh-read");
        using var http = new HttpClient(handler);

        await SourceFor(host, http).WriteBackAsync(
            $"{scheme}:cockpit", Edit(), "chk-operator-opened-with", CancellationToken.None);

        Assert.Equal("\"chk-operator-opened-with\"", handler.IfMatch);
    }

    [Fact]
    public async Task WriteBackAsync_APlaceholderResourceRow_SurvivesTheRoundTripRatherThanBeingDropped()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var reference = OperatingSystem.IsWindows() ? @"C:\Users\erik\work\notes.md" : "/home/erik/work/notes.md";
        var placeholder = CockpitProjectResourceEntry.Create("Reference", reference, "Notes") ?? throw new InvalidOperationException("The resource row was refused.");
        Assert.True(placeholder.Placeholder);
        var current = CockpitProjectDefinitionJson.Serialize(
            new CockpitProjectDefinition { Name = "Cockpit", Resources = [placeholder] });
        var handler = new _DefinitionHandler(current);
        using var http = new HttpClient(handler);

        await SourceFor(host, http).WriteBackAsync(
            $"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(handler.WrittenContent, out var sent, out var error), error);
        Assert.NotNull(sent);
        Assert.NotNull(sent.Resources);
        var resource = Assert.Single(sent.Resources);
        Assert.True(resource.Placeholder);
        Assert.Equal("Notes", resource.Label);
        Assert.Equal(string.Empty, resource.Reference);
    }

    [Fact]
    public async Task WriteBackAsync_PermissionDenied_ReturnsAReason()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var handler = new _DefinitionHandler(
            """{"schemaVersion":1,"name":"Cockpit"}""", writeStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).WriteBackAsync(
            $"{scheme}:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.PermissionDenied, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task WriteBackAsync_AnIdBelongingToADifferentConnection_FailsWithoutCallingDepotAtAll()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _DefinitionHandler("""{"schemaVersion":1,"name":"Cockpit"}""");
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).WriteBackAsync(
            "some-other-scheme:cockpit", Edit(), "chk-before", CancellationToken.None);

        Assert.Equal(SharedProjectWriteBackOutcome.Failed, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class _DefinitionHandler(
        string definition,
        string currentChecksum = "chk-before",
        HttpStatusCode writeStatus = HttpStatusCode.OK,
        string writtenChecksum = "chk-after") : HttpMessageHandler
    {
        public string? IfMatch { get; private set; }
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
                    Content = new StringContent(_Projects(definition, currentChecksum)),
                    RequestMessage = request,
                };
            }

            IfMatch = request.Headers.IfMatch.SingleOrDefault()?.Tag;
            WrittenContent = await (request.Content ?? throw new InvalidOperationException("The request had no body.")).ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(writeStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { checksum = writtenChecksum })),
                RequestMessage = request,
            };
        }

        private static string _Projects(string value, string checksum) => JsonSerializer.Serialize(new
        {
            projects = new[]
            {
                new
                {
                    address = "acme/cockpit",
                    slug = "cockpit",
                    name = "Cockpit",
                    role = "Editor",
                    kind = "Project",
                    organization = new { slug = "acme", name = "Acme", role = "Member" },
                    cockpit = new
                    {
                        state = "ok",
                        checksum,
                        updatedAt = "2026-10-23T12:00:00Z",
                        definition = JsonSerializer.Deserialize<JsonElement>(value),
                    },
                },
            },
        });
    }
}
