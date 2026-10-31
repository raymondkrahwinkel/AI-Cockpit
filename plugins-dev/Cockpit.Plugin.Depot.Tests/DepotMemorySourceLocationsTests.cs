using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;
using System.Net;

namespace Cockpit.Plugin.Depot.Tests;

// AC-502/AC-1634/AC-1992: exercises the delegates as the plugin builds them, through the connection's REST listing.
public class DepotMemorySourceLocationsTests
{
    private const string _Brains =
        """{"projects":[{"address":"acme/zyra","slug":"zyra","name":"Zyra","kind":"Brain"},{"address":"acme/cockpit","slug":"cockpit","name":"Cockpit","kind":"Project"}]}""";

    private sealed class _Handler(HttpStatusCode status, string content, bool throws = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throws
                ? throw new HttpRequestException(content)
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }

    // Layer 2, plugin contract (Depot brain catalogue): only brains, each carrying its own server, so a same-named
    // brain on another connection is a different choice that the first connection refuses to load; a failed or
    // refused listing is its own outcome, never an empty success.
    [Fact]
    public async Task BrainChoices_OnlyBrains_KeepConnectionIdentity_AndReportUnavailable()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        ProjectMemorySourceRegistration RegistrationFor(DepotConnectionRegistration connection, _Handler handler) =>
            DepotMemorySource.BuildRegistrationPairs([connection], host, new HttpClient(handler)).Single().Registration;
        var acme = RegistrationFor(new("c1", "Acme", "https://depot.acme.example"), new(HttpStatusCode.OK, _Brains));
        var other = RegistrationFor(new("c2", "Other", "https://depot.other.example"), new(HttpStatusCode.OK, _Brains));

        var acmeBrain = Assert.Single((await (acme.ListBrainsAsync ?? throw new InvalidOperationException("No brain listing."))(CancellationToken.None)).Locations);
        var otherBrain = Assert.Single((await (other.ListBrainsAsync ?? throw new InvalidOperationException("No brain listing."))(CancellationToken.None)).Locations);

        Assert.Equal(new ProjectMemorySourceLocation("acme/zyra@depot.acme.example", "Zyra", "acme/zyra · Acme"), acmeBrain);
        Assert.Equal("acme/zyra@depot.other.example", otherBrain.Value);
        var load = acme.BrainInstruction ?? throw new InvalidOperationException("No brain instruction.");
        var instruction = load(acmeBrain.Value);
        Assert.NotNull(instruction);
        Assert.Contains("\"Depot: Acme\" MCP server", instruction);
        Assert.Contains("bootstrap tool for the brain \"acme/zyra\"", instruction);
        Assert.Contains("https://depot.acme.example", instruction);
        Assert.Null(load(otherBrain.Value));

        var failed = RegistrationFor(new("c4", "Down", "https://depot.down.example"), new(HttpStatusCode.OK, "connection reset", throws: true));
        var refused = RegistrationFor(new("c3", "Refused", "https://depot.refused.example"), new(HttpStatusCode.Unauthorized, string.Empty));
        foreach (var list in new[] { failed.ListBrainsAsync, failed.ListLocationsAsync })
        {
            var result = await (list ?? throw new InvalidOperationException("No listing."))(CancellationToken.None);
            Assert.Equal(ProjectMemorySourceLocationsOutcome.Failed, result.Outcome);
            Assert.Equal("connection reset", result.Error);
        }

        Assert.Equal(
            ProjectMemorySourceLocationsOutcome.AuthorizationRequired,
            (await (refused.ListBrainsAsync ?? throw new InvalidOperationException("No brain listing."))(CancellationToken.None)).Outcome);
    }
}
