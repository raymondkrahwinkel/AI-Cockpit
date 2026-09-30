using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): what the Projects workspace's "Shared via Depot — …" group is built
// from. Every fixture is the actual JSON text a Depot server would send, parsed by the real `list_projects`
// and definition deserializers — never a fake handing back an already-built `SharedProject` (AC-604).
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
}
