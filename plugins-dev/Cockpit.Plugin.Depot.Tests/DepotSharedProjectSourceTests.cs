using System.Net;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource` (AC-245): the shared-project group is built from Depot's inline REST definitions.
public class DepotSharedProjectSourceTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient)
    {
        host.Cache.Returns(new InMemoryPluginCache());
        return new DepotSharedProjectSource(Connection(), DepotMemorySource.Scheme, host, httpClient);
    }

    [Fact]
    public async Task ListAsync_A404FailsAsTooOldWithoutFallingBackToMcp()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("too old", result.Error, StringComparison.Ordinal);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListAsync_AnAdminsProject_WithCanWriteFalse_IsReadOnlyAndShowsItsRole()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","role":"Admin","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"ok","checksum":"chk","updatedAt":"2026-10-20T10:00:00Z","definition":{"schemaVersion":1,"name":"Cockpit"},"canWrite":false}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        var project = Assert.Single(result.Projects);
        Assert.Equal("Admin", project.Role);
        Assert.False(project.CanWriteBack);
    }

    [Fact]
    public async Task ListAsync_AViewersAbsentDefinition_IsReportedAsVisibleButUnreadable()
    {
        var host = Substitute.For<ICockpitHost>();
        using var http = _Client("""{"projects":[{"address":"acme/viewer-only","slug":"viewer-only","name":"Viewer Only","role":"Viewer","kind":"Project","organization":{"slug":"acme","name":"Acme","role":"Member"},"cockpit":{"state":"absent","checksum":null,"updatedAt":null}}]}""");

        var result = await SourceFor(host, http).ListAsync(CancellationToken.None);

        Assert.Empty(result.Projects);
        var unreadable = Assert.Single(result.VisibleButUnreadable);
        Assert.Equal("Viewer Only", unreadable.Name);
        Assert.Equal("Viewer", unreadable.Role);
    }

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
