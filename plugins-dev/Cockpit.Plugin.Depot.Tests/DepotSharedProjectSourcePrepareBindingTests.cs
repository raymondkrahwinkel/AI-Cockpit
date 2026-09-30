using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.PrepareBindingAsync` (AC-246): the second, fuller read the "Finish setting up…"
// bind step needs. Every fixture is the actual JSON text Depot's `read` tool would send, parsed by the real
// deserializer — never a fake that hands back an already-built type, per `DepotSharedProjectSourceTests`.
public class DepotSharedProjectSourcePrepareBindingTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient? httpClient = null) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static string _Scheme(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;

    private static void _StubRead(ICockpitHost host, string slug, PluginMcpToolCallResult result) =>
        host.CallMcpToolAsync(
            Arg.Any<string>(), "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => args != null && (string)args["project"]! == slug),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    private static PluginMcpToolCallResult _DefinitionEnvelope(string definitionJson, string checksum = "chk") =>
        PluginMcpToolCallResult.Success(
            $$"""{"path":".cockpit/project.json","content":{{System.Text.Json.JsonSerializer.Serialize(definitionJson)}},"checksum":"{{checksum}}","size":1}""");

    [Fact]
    public async Task PrepareBindingAsync_FullDefinition_MapsEveryFieldOntoTheBinding()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "handbook", _DefinitionEnvelope("""
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

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:handbook", CancellationToken.None);

        Assert.True(result.Succeeded);
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

        var result = await SourceFor(host).PrepareBindingAsync("some-other-scheme:cockpit", CancellationToken.None);

        Assert.False(result.Succeeded);
        await host.DidNotReceive().CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareBindingAsync_TheDefinitionIsBrokenJson_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "broken", _DefinitionEnvelope("not json at all"));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:broken", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }
}
