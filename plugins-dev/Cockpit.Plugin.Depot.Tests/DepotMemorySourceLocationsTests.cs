using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// AC-502: <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/>/<see cref="ProjectMemorySourceRegistration.SignInAsync"/>
/// as this plugin builds them — calling the connection's own contributed MCP server through
/// <see cref="ICockpitHost.CallMcpToolAsync"/> rather than a fixture, since that call is the whole point of these
/// two delegates existing.
/// </summary>
public class DepotMemorySourceLocationsTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Synvolution", "https://depot.example.com");

    private static ProjectMemorySourceRegistration RegistrationFor(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration;

    [Fact]
    public async Task ListLocationsAsync_CallsListProjectsOnThisConnectionsOwnServer()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync("Depot: Synvolution", "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"projects":[]}""")));

        await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        await host.Received(1).CallMcpToolAsync("Depot: Synvolution", "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListLocationsAsync_ParsesSlugNameAndSummaryIntoALocationEach()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project","summary":{"documentCount":2,"lastModifiedAt":"2026-07-29T16:22:26.2487163+00:00"}}]}""")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Success, result.Outcome);
        var location = Assert.Single(result.Locations);
        Assert.Equal("cockpit", location.Value);
        Assert.Equal("Cockpit", location.Name);
        Assert.Contains("2 documents", location.Detail);
    }

    // --- AC-499: kind (Project/Brain) shown in the picker's own detail line ---------------------------------------

    [Fact]
    public async Task ListLocationsAsync_AProjectWithASummary_ShowsKindBeforeTheDocumentCount()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"projects":[{"slug":"cockpit","name":"Cockpit","kind":"Project","summary":{"documentCount":2}}]}""")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal("Project · 2 documents", Assert.Single(result.Locations).Detail);
    }

    [Fact]
    public async Task ListLocationsAsync_ABrainWithNoSummaryOrRole_ShowsJustTheKind()
    {
        // Raymond's own account mixes Depot projects and Depot brains under one connection (olaf, testy, vex) —
        // list_projects returns both with no summary requested (includeSummary: true is still sent, but a Brain's
        // own summary may legitimately be absent), so kind alone must still be visible rather than an empty line.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"projects":[{"slug":"olaf","name":"Olaf","kind":"Brain"}]}""")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal("Brain", Assert.Single(result.Locations).Detail);
    }

    [Fact]
    public async Task ListLocationsAsync_NoKindField_FallsBackToTheDocumentSummaryAlone()
    {
        // An older Depot server that predates the kind field — the picker must not regress to showing nothing.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"projects":[{"slug":"cockpit","name":"Cockpit","summary":{"documentCount":2}}]}""")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal("2 documents", Assert.Single(result.Locations).Detail);
    }

    [Fact]
    public async Task ListLocationsAsync_EmptyProjectList_IsSuccessWithNoLocations()
    {
        // Distinct from AuthorizationRequired/Failed (AC-502 criteria 4/5): a source with genuinely nothing in it
        // must not be indistinguishable from one that could not be asked.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"projects":[]}""")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Success, result.Outcome);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task ListLocationsAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.AuthorizationRequired, result.Outcome);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task ListLocationsAsync_ToolCallFails_ReportsFailedWithAMessage_NeverAnEmptyListDisguisedAsSuccess()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("connection reset")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Failed, result.Outcome);
        Assert.Equal("connection reset", result.Error);
    }

    [Fact]
    public async Task ListLocationsAsync_UnparsableContent_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("not json")));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SignInAsync_DrivesTheHostsOwnSignInForThisConnectionsServer_AndReportsSuccess()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SignInMcpServerAsync("Depot: Synvolution", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpSignInOutcome.Authorized));

        var signedIn = await RegistrationFor(host).SignInAsync!(CancellationToken.None);

        Assert.True(signedIn);
        await host.Received(1).SignInMcpServerAsync("Depot: Synvolution", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInAsync_Declined_ReportsFalse()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpSignInOutcome.Declined));

        Assert.False(await RegistrationFor(host).SignInAsync!(CancellationToken.None));
    }
}
