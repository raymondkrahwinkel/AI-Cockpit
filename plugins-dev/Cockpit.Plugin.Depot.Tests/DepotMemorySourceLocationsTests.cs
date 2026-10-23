using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;
using System.Net;

namespace Cockpit.Plugin.Depot.Tests;

// AC-502/AC-1634: exercises the delegates as the plugin builds them, through the connection's REST listing.
public class DepotMemorySourceLocationsTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Acme", "https://depot.example.com");

    private static ProjectMemorySourceRegistration RegistrationFor(ICockpitHost host, HttpMessageHandler handler) =>
        DepotMemorySource.BuildRegistrationPairs([Connection()], host, new HttpClient(handler)).Single().Registration;

    private sealed class _Handler(string content, bool throws = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throws
                ? throw new HttpRequestException(content)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }

    [Fact]
    public async Task ListLocationsAsync_ParsesSlugNameKindAndRoleIntoALocationEach()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());

        var result = await RegistrationFor(host, new _Handler(
            """{"projects":[{"slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project"}]}""")).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Success, result.Outcome);
        var location = Assert.Single(result.Locations);
        Assert.Equal("cockpit", location.Value);
        Assert.Equal("Cockpit", location.Name);
        Assert.Equal("Project · Admin", location.Detail);
    }

    [Fact]
    public async Task ListLocationsAsync_ToolCallFails_ReportsFailedWithAMessage_NeverAnEmptyListDisguisedAsSuccess()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());

        var result = await RegistrationFor(host, new _Handler("connection reset", throws: true)).ListLocationsAsync!(CancellationToken.None);

        Assert.Equal(ProjectMemorySourceLocationsOutcome.Failed, result.Outcome);
        Assert.Equal("connection reset", result.Error);
    }
}
