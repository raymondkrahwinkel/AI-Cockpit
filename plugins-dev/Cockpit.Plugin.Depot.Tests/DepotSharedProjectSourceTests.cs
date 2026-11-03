using System.Net;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): the shared-project group is built from Depot's inline REST definitions.
public class DepotSharedProjectSourceTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    // AC-1665, layer 2 (plugin contract / cache): the list is cached per connection, so a sign-in as someone else must
    // take the previous account's list away, or an unreachable Depot shows it as the "last known list".
    [Fact]
    public async Task ListAsync_AfterTheAccountChanged_TheLastKnownListOfThePreviousAccountIsGone()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        host.Cache.Returns(new InMemoryPluginCache());
        var connection = Connection();
        new Settings.DepotSettings(host.Storage) { Connections = [connection] };
        using var plugin = new DepotPlugin();
        plugin.Initialize(host);
        using var known = _Client("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Viewer","kind":"Project"}]}""");
        Assert.Equal(DepotProjectListingOutcome.Success, (await new DepotProjectListingSource(host, connection, known).ListAsync(CancellationToken.None)).Outcome);
        using var unreachable = new HttpClient(new _StubHandler(_ => throw new HttpRequestException("Depot is down.")));
        var source = new DepotSharedProjectSource(connection, "depot:test", host, known);
        host.SharedProjectSources.Returns([source]);
        var changes = new List<string?>();
        source.Changed += changes.Add;

        host.McpServerAccountChanged += Raise.Event<EventHandler<string>>(host, connection.McpServerName);

        // The Projects page reads again too, or it keeps the "sign in" (or the other account's list) until Refresh.
        Assert.Equal<string?>([null], changes);

        var result = await new DepotProjectListingSource(host, connection, unreachable).ListAsync(CancellationToken.None);
        Assert.Equal(DepotProjectListingOutcome.Failed, result.Outcome);
        Assert.Empty(result.Projects);
    }

    private static HttpClient _Client(string json) =>
        new(new _StubHandler(_ => _Ok(json)));

    private static HttpResponseMessage _Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class _StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}
