using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotMemorySource.BuildRegistrationPairs` (AC-501): one registration per connection. Not cosmetic —
// `ProjectMemorySourceRegistry.Register` refuses a blank scheme/title/instruction, so a regression to
// blank here is silently dropped by the host and the operator never learns why.
public class DepotMemorySourceTests
{
    private static DepotConnectionRegistration Connection(string id, string name, string url = "https://depot.example.com") =>
        new(id, name, url);

    // AC-503/AC-499: rebuilt because the original "outline" probe always failed against a real Depot server
    // (outline needs {project, path}, called here with only {project}). Now asks list_projects and matches
    // the typed slug against the returned list.

    private static ICockpitHost _HostReturning(string content) =>
        _HostReturning(PluginMcpToolCallResult.Success(content));

    private static ICockpitHost _HostReturning(PluginMcpToolCallResult result)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return host;
    }

    private const string _TwoProjectsJson =
        """{"projects":[{"slug":"cockpit","name":"Cockpit","kind":"Project"},{"slug":"olaf","name":"Olaf","kind":"Brain"}]}""";

    [Fact]
    public async Task CheckReachability_SlugInTheList_ReturnsConfirmed()
    {
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, result.State);
    }

    [Fact]
    public async Task CheckReachability_SlugNotInTheList_ReturnsNotFound()
    {
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("no-such-project", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.NotFound, result.State);
    }

    [Fact]
    public async Task CheckReachability_AuthorizationRequired_ReturnsNotSignedIn()
    {
        // The one case that actually means "go sign in" — the case Raymond's own live test found conflated with an
        // ordinary failed call before this ticket.
        var host = _HostReturning(PluginMcpToolCallResult.AuthorizationRequired);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.NotSignedIn, result.State);
    }

    [Fact]
    public async Task CheckReachability_UnparsableResponse_ReturnsCheckFailed_NeverThrows()
    {
        var host = _HostReturning("not json");
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, result.State);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task CheckReachability_OnAuthorizationRequired_NeverSurfacesADetail()
    {
        // Iron Law #8, belt-and-braces: NotSignedIn always shows its own fixed sentence — nothing plugin-supplied
        // is ever attached here, unlike CheckFailed which deliberately does carry one.
        var host = _HostReturning(PluginMcpToolCallResult.AuthorizationRequired);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task CheckReachability_OnFailure_LeavesADiagnosticLogLine_WithoutAnyTokenMaterial()
    {
        // AC-499: what silently told Raymond he might not be signed in — a failed check leaving zero trace anywhere
        // grep-able — resolved via ICockpitHost.Services, the same DI seam Cockpit.App.Plugins.CockpitHost's own
        // internal logging already uses.
        var logger = Substitute.For<ILogger>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var services = new ServiceCollection().AddSingleton(loggerFactory).BuildServiceProvider();

        var host = Substitute.For<ICockpitHost>();
        host.Services.Returns(services);
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("connection reset")));
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => !state!.ToString()!.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
                && !state.ToString()!.Contains("token", StringComparison.OrdinalIgnoreCase)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // --- AC-499: FamilyKey / InstanceTitle -------------------------------------------------------------------

    [Fact]
    public async Task AppendNoteAsync_CallsDepotsAtomicAppend_NeverWrite()
    {
        // AC-492: `append` is server-side atomic and creates the file; `write` would replace it. A sign-in demand
        // comes back as its own outcome so the host can offer SignInAsync and retry with the same note.
        var host = _HostReturning(PluginMcpToolCallResult.AuthorizationRequired);
        var connection = Connection("c1", "Acme");
        var registration = DepotMemorySource.BuildRegistrationPairs([connection], host).Single().Registration;

        var result = await registration.AppendNoteAsync!("cockpit", "\n## stamp\n\nnote\n", CancellationToken.None);

        Assert.Equal(ProjectMemoryAppendOutcome.AuthorizationRequired, result.Outcome);
        await host.Received(1).CallMcpToolAsync(
            connection.McpServerName,
            "append",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(arguments =>
                Equals(arguments!["project"], "cockpit")
                && Equals(arguments["path"], DepotMemorySource.NotesPath)
                && Equals(arguments["content"], "\n## stamp\n\nnote\n")),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await host.DidNotReceive().CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Null(DepotMemorySource.BuildRegistrationPairs([connection]).Single().Registration.AppendNoteAsync);
    }
}
