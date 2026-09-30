using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Backend.Tests.Plugins;

/// <summary>
/// Which plugins get asked what a starting session should carry (AC-165). Order matters here in a way it does not
/// for most registries: it decides which plugin wins a variable two of them set.
/// </summary>
public class SessionResourceProviderRegistryTests
{
    private sealed class StubProvider : ISessionResourceProvider
    {
        public Task<SessionResourceContribution> GetSessionResourcesAsync(SessionResourceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionResourceContribution.None);
    }

    // AC-1391: SessionResourceResolver's own ISessionProjectResolver dependency is implemented in Cockpit.App
    // (SessionProjectResolver, by design — see that interface's doc comment); Backend.Tests stands one in for it.
    private sealed class StubProjectResolver : ISessionProjectResolver
    {
        public Task<string?> ProjectIdOfAsync(string? paneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    [Fact]
    public void Providers_AreAskedInRegistrationOrder()
    {
        // The merge keeps the first contributor's value for a key, so this order is what decides the winner.
        var registry = new SessionResourceProviderRegistry();
        var first = new StubProvider();
        var second = new StubProvider();

        registry.Register(first);
        registry.Register(second);

        Assert.Equal(new[] { first, second }, registry.Providers);
    }

}
