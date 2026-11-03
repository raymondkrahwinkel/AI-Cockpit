using System.Net;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSharedProjectSource.PrepareBindingAsync` (AC-246): the full definition comes from the shared REST feed.
public class DepotSharedProjectSourcePrepareBindingTests
{
    private static DepotConnectionRegistration Connection() => new("c1", "Work", "https://depot.example.com");

    private static ISharedProjectSource SourceFor(ICockpitHost host, HttpClient httpClient) =>
        DepotMemorySource.BuildSharedProjectSources([Connection()], host, httpClient).Single();

    [Fact]
    public async Task PrepareBindingAsync_AnIdBelongingToADifferentConnection_FailsWithoutCallingDepotAtAll()
    {
        var host = Substitute.For<ICockpitHost>();
        var handler = new _StubHandler("""{"projects":[]}""");
        using var http = new HttpClient(handler);

        var result = await SourceFor(host, http).PrepareBindingAsync("some-other-scheme:cockpit", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class _StubHandler(string projects) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(projects),
                RequestMessage = request,
            });
        }
    }
}
