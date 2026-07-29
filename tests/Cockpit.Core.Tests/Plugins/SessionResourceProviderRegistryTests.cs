using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Plugins;

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

    [Fact]
    public void Register_TheSameProviderTwice_KeepsOne()
    {
        // A plugin whose Initialize ran again would otherwise have its contribution counted twice on every launch.
        var registry = new SessionResourceProviderRegistry();
        var provider = new StubProvider();

        Assert.True(registry.Register(provider));
        Assert.False(registry.Register(provider));

        Assert.Single(registry.Providers);
    }

    [Fact]
    public void Register_TwoPluginsWithSomethingToGive_KeepsBoth()
    {
        // Unlike a project field, two providers is the expected case rather than a clash: they contribute different
        // variables and there is no key to collide on.
        var registry = new SessionResourceProviderRegistry();

        Assert.True(registry.Register(new StubProvider()));
        Assert.True(registry.Register(new StubProvider()));

        Assert.Equal(2, System.Linq.Enumerable.Count(registry.Providers));
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

    [Fact]
    public void TheAppsOwnScan_ResolvesTheRegistry()
    {
        // The resolver takes ISessionResourceProviderRegistry as a constructor dependency, so a missing marker
        // interface is the app failing to start rather than a quiet degradation — nothing else here would notice,
        // since every other test builds the registry with new().
        var services = new ServiceCollection();
        services.AddServices(typeof(SessionResourceProviderRegistry).Assembly);

        Assert.IsType<SessionResourceProviderRegistry>(services.BuildServiceProvider().GetService<ISessionResourceProviderRegistry>());
    }

    [Fact]
    public void TheAppsOwnScan_ResolvesTheResolverByItsContract()
    {
        // Both launch routes take ISessionResourceResolver as an optional dependency, so a resolver the scan does not
        // register against that interface is not a startup failure — it is null, and every contribution silently
        // never happens. This is the only thing standing between that and shipping.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServices(typeof(SessionResourceProviderRegistry).Assembly);

        Assert.IsType<SessionResourceResolver>(services.BuildServiceProvider().GetService<ISessionResourceResolver>());
    }
}
