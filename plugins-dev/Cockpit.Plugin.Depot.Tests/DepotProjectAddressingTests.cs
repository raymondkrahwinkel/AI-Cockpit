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

    [Fact]
    public async Task BareReferencesWithNoSingleMatch_AreLeftAlone_AndTheOperatorIsTold()
    {
        foreach (var stored in new[] { "foo", "missing" })
        {
            var host = HostListing(TwoOrganizations, stored);
            await _ListLocationsAsync(host);

            host.Received(1).ShowToast(Arg.Is<string>(message => message.Contains(stored)), PluginToastSeverity.Warning, Arg.Any<string?>(), Arg.Any<Action?>());
            var rewrite = Assert.IsType<Func<string, string?>>(host.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "RewriteProjectReferencesAsync").GetArguments()[1]);
            Assert.Null(rewrite(stored));
        }
    }
}
