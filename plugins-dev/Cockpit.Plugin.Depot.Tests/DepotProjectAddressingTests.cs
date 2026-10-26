using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;
using System.Net;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1520: Cockpit must work against a Depot before organizations (no `address`, bare slug) and after (org/slug required).
public class DepotProjectAddressingTests
{
    private const string Legacy = """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Owner","kind":"Project"}]}""";
    private const string Addressed = """{"projects":[{"address":"wispslate/cockpit","slug":"cockpit","name":"Cockpit","role":"Owner","kind":"Project"}]}""";
    private const string TwoOrganizations =
        """{"projects":[{"address":"acme/foo","slug":"foo","role":"Owner","kind":"Project"},{"address":"wispslate/foo","slug":"foo","role":"Owner","kind":"Project"}]}""";

    private static ProjectMemorySourceRegistration RegistrationFor(ICockpitHost host) =>
        DepotMemorySource.BuildRegistrationPairs([new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")], host).Single().Registration;

    private static ICockpitHost HostListing(string listProjectsJson, params string[] storedValues)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _Handler(listProjectsJson));
        host.RewriteProjectReferencesAsync("depot", Arg.Any<Func<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(storedValues.Count(value => call.ArgAt<Func<string, string?>>(1)(value) is not null)));
        return host;
    }

    private sealed class _Handler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }

    private static Task<ProjectMemorySourceLocationsResult> _ListLocationsAsync(ICockpitHost host) =>
        (RegistrationFor(host).ListLocationsAsync ?? throw new InvalidOperationException("The registration lists no locations."))(CancellationToken.None);

    private static Task<string> ProjectArgumentAsync(ICockpitHost host, string storedValue) =>
        DepotProjectAddressing.For(host, new DepotConnectionRegistration("c1", "Work", "https://depot.example.com"), "depot")
            .ProjectArgumentAsync(storedValue, CancellationToken.None);

    [Theory]
    [InlineData("cockpit", "cockpit")]
    [InlineData("wispslate/cockpit", "cockpit")]
    public async Task ADepotWithoutAddresses_GetsTheBareSlug_AndNothingIsRewritten(string stored, string expectedProject)
    {
        var host = HostListing(Legacy, stored);
        var location = Assert.Single((await _ListLocationsAsync(host)).Locations);

        Assert.Equal(("cockpit", "Cockpit", "Project · Owner"), (location.Value, location.Name, location.Detail));
        Assert.Equal(expectedProject, await ProjectArgumentAsync(host, stored));
        await host.DidNotReceive().RewriteProjectReferencesAsync(Arg.Any<string>(), Arg.Any<Func<string, string?>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("cockpit", "wispslate/cockpit")]
    [InlineData("wispslate/cockpit", "wispslate/cockpit")]
    public async Task ADepotWithAddresses_UpgradesBareReferences_AndSendsTheAddress(string stored, string expectedProject)
    {
        var host = HostListing(Addressed, stored);
        var locations = await _ListLocationsAsync(host);

        Assert.Equal("wispslate/cockpit", locations.Locations.Single().Value);
        Assert.Equal(expectedProject, await ProjectArgumentAsync(host, stored));
        var rewrite = Assert.IsType<Func<string, string?>>(host.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "RewriteProjectReferencesAsync").GetArguments()[1]);
        Assert.Equal("wispslate/cockpit", rewrite("cockpit"));
        Assert.Null(rewrite("wispslate/cockpit"));
        Assert.Equal("wispslate/cockpit/notes.md", rewrite("cockpit/notes.md"));
        Assert.Null(rewrite("wispslate/cockpit/notes.md"));

        // The read path of a shared definition (DepotSharedProjectSource._ToBinding) translates through the same rule.
        var addressing = DepotProjectAddressing.For(host, new DepotConnectionRegistration("c1", "Work", "https://depot.example.com"), "depot");
        Assert.Equal("depot:wispslate/cockpit/notes.md", addressing.UpgradeReference("depot:cockpit/notes.md"));
        Assert.Equal("depot:wispslate/cockpit/notes.md", addressing.UpgradeReference("depot:wispslate/cockpit/notes.md"));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("missing")]
    public async Task ABareReferenceWithNoSingleMatch_IsLeftAlone_AndTheOperatorIsTold(string stored)
    {
        var host = HostListing(TwoOrganizations, stored);
        await _ListLocationsAsync(host);

        host.Received(1).ShowToast(Arg.Is<string>(message => message.Contains(stored)), PluginToastSeverity.Warning, Arg.Any<string?>(), Arg.Any<Action?>());
        var rewrite = Assert.IsType<Func<string, string?>>(host.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "RewriteProjectReferencesAsync").GetArguments()[1]);
        Assert.Null(rewrite(stored));
    }
}
