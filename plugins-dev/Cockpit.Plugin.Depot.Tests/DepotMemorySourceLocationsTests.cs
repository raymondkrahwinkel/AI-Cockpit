using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-502: exercises the delegates as this plugin builds them, calling the connection's own contributed
// MCP server through `ICockpitHost.CallMcpToolAsync` rather than a fixture — that call is the whole
// point of these delegates existing.
public class DepotMemorySourceLocationsTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Acme", "https://depot.example.com");

    private static ProjectMemorySourceRegistration RegistrationFor(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host).Single().Registration;

    [Fact]
    public async Task ListLocationsAsync_CallsListProjectsOnThisConnectionsOwnServer()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync("Depot: Acme", "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"projects":[]}""")));

        await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        await host.Received(1).CallMcpToolAsync("Depot: Acme", "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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

    public static IEnumerable<object[]> ProjectListings() =>
    [
        ["""{"projects":[{"slug":"cockpit","name":"Cockpit","kind":"Project","summary":{"documentCount":2}}]}""", "Project · 2 documents"],
        // Raymond's own account mixes Depot projects and Depot brains under one connection (olaf, testy, vex) —
        // list_projects returns both with no summary requested (includeSummary: true is still sent, but a Brain's
        // own summary may legitimately be absent), so kind alone must still be visible rather than an empty line.
        ["""{"projects":[{"slug":"olaf","name":"Olaf","kind":"Brain"}]}""", "Brain"],
        // An older Depot server that predates the kind field — the picker must not regress to showing nothing.
        ["""{"projects":[{"slug":"cockpit","name":"Cockpit","summary":{"documentCount":2}}]}""", "2 documents"],
    ];

    [Theory]
    [MemberData(nameof(ProjectListings))]
    public async Task ListLocationsAsync_TheDetailLine_SaysWhateverTheServerActuallyReported(string projects, string expectedDetail)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(projects)));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(expectedDetail, Assert.Single(result.Locations).Detail);
    }

    // A source with genuinely nothing in it must not be indistinguishable from one that could not be asked
    // (AC-502 criteria 4/5) — same empty list, different outcome.
    public static IEnumerable<object[]> EmptyAnswers() =>
    [
        [PluginMcpToolCallResult.Success("""{"projects":[]}"""), ProjectMemorySourceLocationsOutcome.Success],
        [PluginMcpToolCallResult.AuthorizationRequired, ProjectMemorySourceLocationsOutcome.AuthorizationRequired],
    ];

    [Theory]
    [MemberData(nameof(EmptyAnswers))]
    public async Task ListLocationsAsync_NoLocations_StillSaysWhyThereAreNone(
        PluginMcpToolCallResult toolResult, ProjectMemorySourceLocationsOutcome expected)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(toolResult));

        var result = await RegistrationFor(host).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
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
        host.SignInMcpServerAsync("Depot: Acme", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpSignInOutcome.Authorized));

        var signedIn = await RegistrationFor(host).SignInAsync!(CancellationToken.None);

        Assert.True(signedIn);
        await host.Received(1).SignInMcpServerAsync("Depot: Acme", Arg.Any<CancellationToken>());
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
