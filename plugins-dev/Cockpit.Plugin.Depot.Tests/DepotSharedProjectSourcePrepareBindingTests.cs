using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.PrepareBindingAsync` (AC-246): the second, fuller read the "Finish setting
// up…" bind step needs. Every fixture is the actual JSON text Depot's `read` tool would send, parsed by the
// real `Cockpit.Plugin.Depot.ProjectDefinition.CockpitProjectDefinitionJson` deserializer — the same
// "measure against a real-looking response, never a fake that hands back an already-built type" discipline
// `DepotSharedProjectSourceTests` already documents for `ListAsync`.
public class DepotSharedProjectSourcePrepareBindingTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host).Single();

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
        // The purely defensive half of this case: a non-blank absolute reference reaching a real Depot definition
        // regardless — a hand edit, or an older writer that predates AC-246's placeholder shape (see the sibling
        // test below for the case this build's own writer actually produces now: a blank reference). This reader
        // does not classify or drop anything itself either way — that is the App layer's job
        // (SharedProjectBindingDialogViewModel), reading whatever this returns.
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
        // AC-246 (Raymond, 2026-08-02): this is the real path now, not a hand-edit hypothetical — a project with
        // ten absolute resource rows writes ten placeholders, through the actual writer
        // (CockpitProjectResourceEntry.Create) and the actual (de)serializer, not a hand-assembled read-side
        // fixture. Blank Reference, but Role and Label survive.
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
