using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
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
    public async Task PrepareBindingAsync_NoGitUrl_TheMigratie2026Case_LeavesItNull()
    {
        // A notes-only shared project (AC-246 decision, 2026-08-02): no source of its own to clone.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "migratie-2026", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Migratie-2026"}"""));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:migratie-2026", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Binding!.GitUrl);
        Assert.Empty(result.Binding.Resources);
    }

    [Fact]
    public async Task PrepareBindingAsync_ZeroResourceRows_IsSuccessWithAnEmptyList()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "bare", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Bare","resources":[]}"""));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:bare", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Binding!.Resources);
    }

    [Fact]
    public async Task PrepareBindingAsync_TenAbsoluteResourceRows_AllPassThroughUnfiltered()
    {
        // Defensive case: a non-blank absolute reference from a hand edit or a pre-AC-246 writer (the sibling
        // test below covers this build's actual writer, which produces a blank reference instead). This reader
        // does not classify or drop anything either way — that is the App layer's job.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        var rows = string.Join(",", Enumerable.Range(1, 10)
            .Select(i => $$"""{"role":"Reference","reference":"/home/erik/work/note-{{i}}.md","portability":"absolute"}"""));
        _StubRead(host, "ten-absolute", _DefinitionEnvelope($$"""{"schemaVersion":1,"name":"Ten Absolute","resources":[{{rows}}]}"""));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:ten-absolute", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Binding!.Resources.Count);
        Assert.All(result.Binding.Resources, resource => Assert.StartsWith("/home/erik/work/note-", resource.Reference));
    }

    [Fact]
    public async Task PrepareBindingAsync_TenAbsoluteRowsThroughTheRealWritePipeline_ArriveAsPlaceholders()
    {
        // AC-246 (Raymond, 2026-08-02): the real path, not a hand-edit hypothetical — ten absolute resource rows
        // written through the actual writer and (de)serializer produce ten placeholders with a blank Reference,
        // but Role and Label survive.
        var entries = Enumerable.Range(1, 10)
            .Select(i => CockpitProjectResourceEntry.Create("Reference", $"/home/erik/work/note-{i}.md", $"Note {i}"))
            .ToList();
        Assert.All(entries, entry => Assert.NotNull(entry)); // every one is a placeholder, not a drop
        Assert.All(entries, entry => Assert.True(entry!.Placeholder));

        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition
        {
            Name = "Ten Absolute (real pipeline)",
            Resources = entries!,
        });

        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "ten-absolute-real", _DefinitionEnvelope(written));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:ten-absolute-real", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Binding!.Resources.Count);
        Assert.All(result.Binding.Resources, resource => Assert.Equal(string.Empty, resource.Reference));
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => $"Note {i}").OrderBy(label => label, StringComparer.Ordinal),
            result.Binding.Resources.Select(resource => resource.Label).OrderBy(label => label, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PrepareBindingAsync_ABlankResourceReference_IsLeftOut()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "blank-row", _DefinitionEnvelope(
            """{"schemaVersion":1,"name":"Blank Row","resources":[{"role":"Reference","reference":"   "},{"role":"Reference","reference":"docs/ok.md"}]}"""));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:blank-row", CancellationToken.None);

        Assert.True(result.Succeeded);
        var resource = Assert.Single(result.Binding!.Resources);
        Assert.Equal("docs/ok.md", resource.Reference);
    }

    [Fact]
    public async Task PrepareBindingAsync_DefinitionNamesALogo_DownloadsItOntoTheBinding()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "handbook", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Handbook","logo":".cockpit/logo.png"}"""));
        host.CallMcpToolAsync(Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"downloadUrl":"https://depot.example.com/blob/download/xyz"}""")));
        var expectedBytes = new byte[] { 137, 80, 78, 71 };
        using var httpClient = new HttpClient(new _StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedBytes),
        }));

        var result = await SourceFor(host, httpClient).PrepareBindingAsync($"{scheme}:handbook", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedBytes, result.Binding!.LogoBytes);
    }

    [Fact]
    public async Task PrepareBindingAsync_NoLogoOnTheDefinition_NeverAttemptsADownload()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "bare", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Bare"}"""));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:bare", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Binding!.LogoBytes);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareBindingAsync_TheLogoDownloadFails_StillSucceedsWithoutOne()
    {
        // AC-763: a logo is decoration — a failed download costs the picture, not the whole bind (SharedProjectBinding.LogoBytes' own remarks).
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "handbook", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Handbook","logo":".cockpit/logo.png"}"""));
        host.CallMcpToolAsync(Arg.Any<string>(), "request_download", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("blob not found")));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:handbook", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Binding!.LogoBytes);
    }

    [Fact]
    public async Task PrepareBindingAsync_NotSignedIn_ReportsFailedWithAFinishSettingUpSpecificMessage()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        host.CallMcpToolAsync(Arg.Any<string>(), "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:whatever", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Binding);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PrepareBindingAsync_TheReadToolFails_ReportsFailedWithTheServersOwnMessage()
    {
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "gone", PluginMcpToolCallResult.Failed("project not found"));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:gone", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("project not found", result.Error);
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
    public async Task PrepareBindingAsync_TheConnectionDroppedBetweenListAndBind_ReportsFailedRatherThanThrowing()
    {
        // "koppelen terwijl de Depot-verbinding wegvalt" (AC-246 harness case) — an unreachable connection reported
        // through the ordinary CallMcpToolAsync "answers, never throws" contract (ICockpitHost's own promise), the
        // same shape a dropped connection actually takes.
        var host = Substitute.For<ICockpitHost>();
        var scheme = _Scheme(host);
        _StubRead(host, "cockpit", PluginMcpToolCallResult.Failed("connection reset"));

        var result = await SourceFor(host).PrepareBindingAsync($"{scheme}:cockpit", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection reset", result.Error);
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
