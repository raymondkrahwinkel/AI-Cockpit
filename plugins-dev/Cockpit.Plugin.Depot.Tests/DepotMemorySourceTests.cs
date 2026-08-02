using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotMemorySource.BuildRegistrationPairs` (AC-501) — one registration per connection instead of the
// single fixed one this plugin handed the host before. The registry that receives it
// (`ProjectMemorySourceRegistry.Register`) refuses a blank scheme, title or instruction, so this is not
// cosmetic: a registration that regresses to blank here is one the host silently drops, and the operator would
// never learn why a connection stopped appearing as a memory source.
public class DepotMemorySourceTests
{
    private static DepotConnectionRegistration Connection(string id, string name, string url = "https://depot.example.com") =>
        new(id, name, url);

    private static ICockpitHost Host() => Substitute.For<ICockpitHost>();

    [Fact]
    public void FirstConnection_KeepsThePlainDepotScheme()
    {
        // The existing-projects compatibility guarantee (AC-501 acceptance criterion 3): a project stored as
        // "depot:cockpit" before this ticket must keep resolving, whichever connection is now first.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], Host());

        Assert.Equal("depot", Assert.Single(pairs).Registration.Scheme);
    }

    [Fact]
    public void SecondConnection_GetsASchemeNamespacedFromItsOwnName()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Acme"),
            Connection("c2", "Wispslate"),
        ], Host());

        Assert.Equal("depot", pairs[0].Registration.Scheme);
        Assert.Equal("depot.wispslate", pairs[1].Registration.Scheme);
    }

    [Fact]
    public void EachRegistration_HasATitleDerivedFromTheConnectionName()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Acme"),
            Connection("c2", "Wispslate"),
        ], Host());

        Assert.Equal("Depot project — Acme", pairs[0].Registration.Title);
        Assert.Equal("Depot project — Wispslate", pairs[1].Registration.Title);
    }

    [Fact]
    public void EachRegistration_InstructionNamesItsOwnInstance()
    {
        // Acceptance criterion 7: the Instruction has to say *which* Depot a starting session's memory lives on,
        // not just that it lives on Depot.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Wispslate")], Host());

        Assert.Contains("Wispslate", pairs.Single().Registration.Instruction);
    }

    [Fact]
    public void Instruction_StillCarriesTheHonestyClause_WhenTheMcpIsUnavailable()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], Host());

        Assert.Contains(
            "If the Depot MCP is not available in this session, say so rather than working from memory you cannot see.",
            pairs.Single().Registration.Instruction);
    }

    [Fact]
    public void NoConnections_BuildsNoRegistrations()
    {
        // Acceptance criterion 5: without a configured connection the row behaves exactly as it did before this
        // plugin existed — nothing offered, not a fixed "Depot project" nothing points at.
        Assert.Empty(DepotMemorySource.BuildRegistrationPairs([], Host()));
    }

    [Fact]
    public void ASymbolOnlyName_StillProducesAUsableScheme()
    {
        // Nothing in the slug survives a name like "★★★" — the connection's own id is the fallback, which is always
        // ProjectMemoryRef.IsUsableScheme-valid (a GUID's hex digits), so this connection is never silently dropped.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Primary"),
            Connection("abc123", "★★★"),
        ], Host());

        // Mirrors ProjectMemoryRef.IsUsableScheme (Cockpit.Core, not referenced from this test project): at least
        // two characters, no colon, no surrounding whitespace.
        var scheme = pairs[1].Registration.Scheme;
        Assert.Equal("depot.abc123", scheme);
        Assert.True(scheme.Length >= 2 && !scheme.Contains(':') && scheme == scheme.Trim());
    }

    [Fact]
    public void ThreeNonPrimaryConnectionsSharingBothNameAndId_StillGetDistinctSchemes()
    {
        // Pathological input (hand-edited or corrupted storage sharing an id) — three, not two: the second
        // collision already falls back to the shared id, so only a third one proves the id-fallback itself is
        // re-checked against what is already taken rather than assumed to always be free.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("primary", "Acme"),
            Connection("dup", "Work"),
            Connection("dup", "Work"),
            Connection("dup", "Work"),
        ], Host());

        var schemes = pairs.Skip(1).Select(pair => pair.Registration.Scheme).ToList();
        Assert.Equal(schemes.Count, schemes.Distinct().Count());
    }

    [Fact]
    public void TwoNonPrimaryConnectionsWithTheSameName_StillGetDistinctSchemes()
    {
        // The slug alone would collide on "depot.work" for both non-primary connections; the second one to claim it
        // falls back to its own id instead of silently losing to the registry's first-one-wins Register refusal.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("primary", "Acme"),
            Connection("c1", "Work"),
            Connection("c2", "Work"),
        ], Host());

        Assert.Equal("depot.work", pairs[1].Registration.Scheme);
        Assert.Equal("depot.c2", pairs[2].Registration.Scheme);
    }

    [Fact]
    public void BuildRegistrations_ReturnsTheSameRegistrationsAsThePairs()
    {
        var connections = new[] { Connection("c1", "Acme"), Connection("c2", "Wispslate") };
        var host = Host();

        var registrations = DepotMemorySource.BuildRegistrations(connections, host);
        var pairs = DepotMemorySource.BuildRegistrationPairs(connections, host);

        // ProjectMemorySourceRegistration's own equality ignores ListLocationsAsync/SignInAsync (AC-502) — two
        // delegates built fresh for the same connection are never reference-equal, so this is exactly the
        // comparison that equality override exists to make possible here.
        Assert.Equal(pairs.Select(pair => pair.Registration), registrations);
    }

    // --- AC-503/AC-499: CheckReachability wiring -----------------------------------------------------------------
    // Rebuilt for AC-499: the original "outline" probe (ProbeMcpToolAsync) was measured against a real Depot server
    // and found to always fail — outline is a single-document tool requiring {project, path}, called here with only
    // {project}. This now asks list_projects (the same tool ListLocationsAsync already uses) and matches the typed
    // slug against the returned list — see DepotMemorySource._CheckReachabilityAsync's own remarks.

    [Fact]
    public void NoHostPassed_LeavesCheckReachabilityNull()
    {
        // The default for every existing caller of this method (and every test above it) — a row from a
        // registration built without a host must behave exactly as it always has: nothing shown under it.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")]);

        Assert.Null(pairs.Single().Registration.CheckReachability);
    }

    [Fact]
    public void AHostPassed_WiresUpCheckReachability()
    {
        var host = Substitute.For<ICockpitHost>();
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        Assert.NotNull(pairs.Single().Registration.CheckReachability);
    }

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
    public async Task CheckReachability_CallsListProjects_AgainstThisConnectionsOwnMcpServerName()
    {
        var host = _HostReturning(_TwoProjectsJson);
        var connection = Connection("c1", "Acme");
        var pairs = DepotMemorySource.BuildRegistrationPairs([connection], host);

        await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        // "Depot: Acme" — DepotConnectionRegistration.McpServerName's own fixed prefix, so a hand-typed
        // server name here can never silently drift from the name AddMcpServer actually registered under.
        await host.Received(1).CallMcpToolAsync(connection.McpServerName, "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckReachability_RequestsWithoutTheSummaryWalk()
    {
        // Depot's own list_projects description warns includeSummary "walks each returned project's file tree
        // server-side — for an Admin caller that is every project on the server". The picker (ListLocationsAsync)
        // is opened once and can afford that; this check reruns on every debounced edit and must not.
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        await host.Received(1).CallMcpToolAsync(
            Arg.Any<string>(),
            "list_projects",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(arguments => Equals(arguments!["includeSummary"], false)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckReachability_SlugInTheList_ReturnsConfirmed()
    {
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, result.State);
    }

    [Fact]
    public async Task CheckReachability_SlugMatchIsCaseInsensitive()
    {
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("COCKPIT", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, result.State);
    }

    [Fact]
    public async Task CheckReachability_OnConfirmed_DetailNamesTheProjectAndItsKind()
    {
        // AC-499: the confirmation may say what it found, since that data comes free with the same call.
        var host = _HostReturning(_TwoProjectsJson);
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("olaf", CancellationToken.None);

        Assert.Equal("Olaf · Brain", result.Detail);
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
    public async Task CheckReachability_EmptyList_ReturnsNotFound()
    {
        var host = _HostReturning("""{"projects":[]}""");
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

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
    public async Task CheckReachability_OrdinaryCallFailure_ReturnsCheckFailed_WithTheReason()
    {
        // AC-499: a call that reached the server and failed for some other reason must read as "the check failed",
        // never as "not signed in" — Raymond was signed in the whole time; this is the defect the picker's own
        // working list_projects call proved by contrast.
        var host = _HostReturning(PluginMcpToolCallResult.Failed("connection reset"));
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, result.State);
        Assert.Equal("connection reset", result.Detail);
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

    [Fact]
    public async Task CheckReachability_NoLoggerRegistered_StillReturnsAnAnswer_NeverThrows()
    {
        // Most tests (and a host/test double predating this) never stub Services at all — an unconfigured
        // Substitute.For<ICockpitHost>() answers null for it, so the logging seam must tolerate that rather than
        // NullReferenceException-ing out of a check that otherwise has a perfectly good answer to give.
        var host = _HostReturning(PluginMcpToolCallResult.Failed("connection reset"));
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host);

        var result = await pairs.Single().Registration.CheckReachability!("cockpit", CancellationToken.None);

        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, result.State);
    }

    // --- AC-499: FamilyKey / InstanceTitle -------------------------------------------------------------------

    [Fact]
    public void FirstConnection_CarriesTheDepotFamilyKeyAndItsOwnInstanceTitle()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], Host());

        var registration = pairs.Single().Registration;
        Assert.Equal("depot", registration.FamilyKey);
        Assert.Equal("Acme", registration.InstanceTitle);
    }

    [Fact]
    public void EveryConnection_CarriesTheSameFamilyKey_WhateverItsOwnScheme()
    {
        // The scheme is per-connection (namespaced from the second connection on), but the family every connection
        // opts into is the one "Depot" entry — FamilyKey must not drift with the scheme.
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Acme"),
            Connection("c2", "Wispslate"),
        ], Host());

        Assert.All(pairs, pair => Assert.Equal("depot", pair.Registration.FamilyKey));
    }

    [Fact]
    public void SecondConnection_InstanceTitleIsItsOwnName_NotTheNamespacedScheme()
    {
        var pairs = DepotMemorySource.BuildRegistrationPairs([
            Connection("c1", "Acme"),
            Connection("c2", "Wispslate"),
        ], Host());

        Assert.Equal("Wispslate", pairs[1].Registration.InstanceTitle);
    }

    [Fact]
    public void NoHostPassed_StillSetsFamilyKeyAndInstanceTitle()
    {
        // FamilyKey/InstanceTitle are plain data on the registration, unlike ListLocationsAsync/SignInAsync/
        // CheckReachability which need a host to close over — a registration built without one must not silently
        // drop them.
        var pairs = DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")]);

        var registration = pairs.Single().Registration;
        Assert.Equal("depot", registration.FamilyKey);
        Assert.Equal("Acme", registration.InstanceTitle);
    }
}
