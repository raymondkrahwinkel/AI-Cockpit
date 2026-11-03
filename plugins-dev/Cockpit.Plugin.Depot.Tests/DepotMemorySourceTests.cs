using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Net;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotMemorySource.BuildRegistrationPairs` (AC-501): one registration per connection. Not cosmetic —
// `ProjectMemorySourceRegistry.Register` refuses a blank scheme/title/instruction, so a regression to
// blank here is silently dropped by the host and the operator never learns why.
public class DepotMemorySourceTests
{
    private static DepotConnectionRegistration Connection(string id, string name, string url = "https://depot.example.com") =>
        new(id, name, url);

    private sealed class _Handler(HttpStatusCode status, string content, bool throws = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throws
                ? throw new HttpRequestException(content)
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
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
        host.Cache.Returns(new InMemoryPluginCache());
        var pairs = DepotMemorySource.BuildRegistrationPairs(
            [Connection("c1", "Acme")], host, new HttpClient(new _Handler(HttpStatusCode.OK, "connection reset", throws: true)));

        await (pairs.Single().Registration.CheckReachability ?? throw new InvalidOperationException("The registration has no reachability check."))("cockpit", CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => !$"{state}".Contains("Bearer", StringComparison.OrdinalIgnoreCase)
                && !$"{state}".Contains("token", StringComparison.OrdinalIgnoreCase)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
