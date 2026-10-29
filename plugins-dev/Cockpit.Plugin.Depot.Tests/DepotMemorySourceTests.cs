using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
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

    // AC-503/AC-499/AC-1634: the reachability probe matches the typed slug against Depot's REST project list.

    private static ICockpitHost _HostReturning(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new _Handler(status, content));
        return host;
    }

    private sealed class _Handler(HttpStatusCode status, string content, bool throws = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throws
                ? throw new HttpRequestException(content)
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }

    private sealed class _AppendHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return request.RequestUri?.AbsolutePath == "/cockpit/projects"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"projects":[{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","kind":"Project"}]}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
    }

    private const string _TwoProjectsJson =
        """{"projects":[{"slug":"cockpit","name":"Cockpit","kind":"Project"},{"slug":"olaf","name":"Olaf","kind":"Brain"}]}""";

    [Fact]
    public async Task CheckReachability_MapsEachAnswerToItsState_AndNeverThrows()
    {
        var listed = await _CheckAsync(_HostReturning(_TwoProjectsJson), "cockpit");
        var missing = await _CheckAsync(_HostReturning(_TwoProjectsJson), "no-such-project");
        // The one case that actually means "go sign in" — the case Raymond's own live test found conflated with an
        // ordinary failed call before this ticket.
        var refused = await _CheckAsync(_HostReturning(string.Empty, HttpStatusCode.Unauthorized), "cockpit");
        var garbled = await _CheckAsync(_HostReturning("not json"), "cockpit");

        Assert.Equal(ProjectMemorySourceReachability.Confirmed, listed.State);
        Assert.Equal(ProjectMemorySourceReachability.NotFound, missing.State);
        Assert.Equal(ProjectMemorySourceReachability.NotSignedIn, refused.State);
        // Iron Law #8: NotSignedIn shows its own fixed sentence; nothing plugin-supplied is attached, unlike CheckFailed.
        Assert.Null(refused.Detail);
        Assert.Equal(ProjectMemorySourceReachability.CheckFailed, garbled.State);
        Assert.NotNull(garbled.Detail);
    }

    private static Task<ProjectMemorySourceReachabilityResult> _CheckAsync(ICockpitHost host, string slug) =>
        (DepotMemorySource.BuildRegistrationPairs([Connection("c1", "Acme")], host).Single().Registration.CheckReachability
            ?? throw new InvalidOperationException("The registration has no reachability check."))(slug, CancellationToken.None);

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

    // --- AC-499: FamilyKey / InstanceTitle -------------------------------------------------------------------

    [Fact]
    public async Task AppendNoteAsync_PostsToDepotsAtomicAppend_NeverCallsMcp()
    {
        // AC-492/AC-1636: append stays server-side atomic over REST. A 401 remains its own outcome so the host can
        // offer SignInAsync and retry with the same note.
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        var handler = new _AppendHandler();
        host.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => handler);
        var connection = Connection("c1", "Acme");
        var registration = DepotMemorySource.BuildRegistrationPairs([connection], host).Single().Registration;

        var result = await (registration.AppendNoteAsync ?? throw new InvalidOperationException("The registration cannot append."))("acme/cockpit", "\n## stamp\n\nnote\n", CancellationToken.None);

        Assert.Equal(ProjectMemoryAppendOutcome.AuthorizationRequired, result.Outcome);
        Assert.Equal("https://depot.example.com/sync/acme/cockpit/append", handler.LastRequest?.RequestUri?.ToString());
        Assert.Contains("\"path\":\"Inbox.md\"", handler.LastBody);
        Assert.Contains("\"content\":\"\\n## stamp\\n\\nnote\\n\"", handler.LastBody);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Null(DepotMemorySource.BuildRegistrationPairs([connection]).Single().Registration.AppendNoteAsync);
    }
}
