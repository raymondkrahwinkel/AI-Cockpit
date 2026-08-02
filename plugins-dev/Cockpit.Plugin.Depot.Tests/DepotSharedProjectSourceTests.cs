using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): what the Projects workspace's "Shared via Depot — …" group is
// built from. Every fixture below is the actual JSON text a Depot server would send — parsed by the real
// `list_projects` parser and the real `Cockpit.Plugin.Depot.ProjectDefinition.CockpitProjectDefinitionJson`
// deserializer, not a fake that hands back an already-built `SharedProject` — the exact naad AC-604's
// own comment on this ticket named as the one worth measuring against a real-looking response rather than trusting
// a shortcut fake.
public class DepotSharedProjectSourceTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host).Single();

    private static void _StubListProjects(ICockpitHost host, string json) =>
        host.CallMcpToolAsync(Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(json)));

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

    [Fact]
    public async Task ListAsync_TheProjectId_MatchesTheConnectionsOwnMemorySourceScheme()
    {
        // So a bound local project's MemoryRef and this catalog's Id agree on what "the same project" means —
        // ProjectsViewModel's bound-project filter and AC-604 claim reconciliation both key off this.
        var host = Substitute.For<ICockpitHost>();
        var scheme = DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration.Scheme;
        _StubListProjects(host, """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "cockpit", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Cockpit"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Equal($"{scheme}:cockpit", Assert.Single(result.Projects).Id);
    }

    [Fact]
    public async Task ListAsync_ListProjectsFails_ReportsAWholeSourceFailure()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("connection reset")));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("connection reset", result.Error);
        Assert.Empty(result.Projects);
    }

    [Fact]
    public async Task ListAsync_NotSignedIn_ReportsAWholeSourceFailure()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
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
    public async Task ListAsync_ZeroProjects_IsSuccessWithAnEmptyList()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[]}""");

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Projects);
    }

    [Fact]
    public async Task ListAsync_TwoHundredProjects_AllWithDefinitions_AreAllIncluded()
    {
        var host = Substitute.For<ICockpitHost>();
        var slugs = Enumerable.Range(1, 200).Select(i => $"proj-{i}").ToList();
        var listing = string.Join(",", slugs.Select(slug => $$"""{"slug":"{{slug}}","name":"{{slug}}","role":"Owner","kind":"Project"}"""));
        _StubListProjects(host, $$"""{"projects":[{{listing}}]}""");
        foreach (var slug in slugs)
        {
            _StubRead(host, slug, _DefinitionEnvelope($$"""{"schemaVersion":1,"name":"{{slug}}"}"""));
        }

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.Projects.Count);
    }

    [Fact]
    public async Task ListAsync_AProjectWithoutACockpitDefinition_IsSilentlyLeftOutButOthersStillAppear()
    {
        // The ordinary case: most Depot projects never opted into Cockpit sharing at all. One missing definition
        // must not cost the connection's other, genuinely shared projects.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"plain","name":"Plain Depot Project","role":"Editor","kind":"Project"},{"slug":"shared","name":"Shared","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "plain", PluginMcpToolCallResult.Failed("not found"));
        _StubRead(host, "shared", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Shared"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Shared", Assert.Single(result.Projects).Name);
        Assert.Empty(result.VisibleButUnreadable);
    }

    [Fact]
    public async Task ListAsync_ADefinitionThatIsBrokenJson_IsLeftOutRatherThanFailingTheWholeListing()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"broken","name":"Broken","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "broken", _DefinitionEnvelope("not json at all"));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Projects);
    }

    [Fact]
    public async Task ListAsync_ADefinitionWithAnUnknownSchemaVersion_IsStillIncluded()
    {
        // AC-244's own forward-compat contract: schemaVersion is a marker, not a gate.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"future","name":"Future","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "future", _DefinitionEnvelope("""{"schemaVersion":99,"name":"From the future"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Equal("From the future", Assert.Single(result.Projects).Name);
    }

    [Fact]
    public async Task ListAsync_ADefinitionWithAnUnknownExtraField_IsStillIncluded()
    {
        // CockpitProjectDefinition.ExtensionData is what carries a newer build's unknown field through unharmed.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"newer","name":"Newer","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "newer", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Newer","fromANewerBuild":{"nested":true}}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Equal("Newer", Assert.Single(result.Projects).Name);
    }

    [Fact]
    public async Task ListAsync_ANameOfTenThousandCharacters_IsPassedThroughUnmodified()
    {
        var host = Substitute.For<ICockpitHost>();
        var longName = new string('x', 10_000);
        _StubListProjects(host, """{"projects":[{"slug":"long","name":"Long","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "long", _DefinitionEnvelope($$"""{"schemaVersion":1,"name":"{{longName}}"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Equal(longName, Assert.Single(result.Projects).Name);
    }

    [Fact]
    public async Task ListAsync_UnicodeAndRtlInNameAndDescription_IsPassedThroughUnmodified()
    {
        var host = Substitute.For<ICockpitHost>();
        const string name = "مشروع الكوكبيت 🚀 日本語";
        const string description = "תיאור בעברית — right-to-left mixed with emoji 🛰️";
        _StubListProjects(host, """{"projects":[{"slug":"i18n","name":"i18n","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "i18n", _DefinitionEnvelope(
            $$"""{"schemaVersion":1,"name":{{System.Text.Json.JsonSerializer.Serialize(name)}},"description":{{System.Text.Json.JsonSerializer.Serialize(description)}}}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        var project = Assert.Single(result.Projects);
        Assert.Equal(name, project.Name);
        Assert.Equal(description, project.Description);
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

    // --- Depot's own access guard: read requires at least Editor today (measured against origin/dev,
    // ReadFileQuery.cs); list_projects has no such gate. Intended to change so a Viewer gets read access too
    // (Raymond, 2026-08-02) — until then, a Viewer/Unknown-role project whose read fails is a named, visible
    // degradation rather than a silent drop. ------------------------------------------------------------------

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
    public async Task ListAsync_AViewersProjectWhoseReadSucceeds_IsIncludedNormally()
    {
        // Forward-compat with the intended DEP-side fix: once Depot lets a Viewer read, this source needs no
        // change — the read simply starts succeeding and the project flows into the ordinary list.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"viewer-ok","name":"Viewer Ok","role":"Viewer","kind":"Project"}]}""");
        _StubRead(host, "viewer-ok", _DefinitionEnvelope("""{"schemaVersion":1,"name":"Viewer Ok"}"""));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Equal("Viewer Ok", Assert.Single(result.Projects).Name);
        Assert.Empty(result.VisibleButUnreadable);
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
    public async Task ListAsync_AnEditorsProjectWhoseReadFails_IsSilentlyLeftOutNotVisibleButUnreadable()
    {
        // For Editor/Owner, a failed read unambiguously means "not shared this way" — not the role-gating ambiguity
        // Viewer/Unknown carries.
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"editor-no-def","name":"Editor No Def","role":"Editor","kind":"Project"}]}""");
        _StubRead(host, "editor-no-def", PluginMcpToolCallResult.Failed("not found"));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        Assert.Empty(result.VisibleButUnreadable);
    }

    [Fact]
    public async Task ListAsync_MissingRoleField_TreatsItAsUnknownAndDegradesTheSameWayAsViewer()
    {
        var host = Substitute.For<ICockpitHost>();
        _StubListProjects(host, """{"projects":[{"slug":"no-role","name":"No Role","kind":"Project"}]}""");
        _StubRead(host, "no-role", PluginMcpToolCallResult.Failed("forbidden"));

        var result = await SourceFor(host).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        Assert.Single(result.VisibleButUnreadable);
    }
}
