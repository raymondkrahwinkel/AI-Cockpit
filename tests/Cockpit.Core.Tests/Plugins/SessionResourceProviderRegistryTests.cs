using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
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

        registry.Register(provider).Should().BeTrue();
        registry.Register(provider).Should().BeFalse();

        registry.Providers.Should().ContainSingle();
    }

    [Fact]
    public void Register_TwoPluginsWithSomethingToGive_KeepsBoth()
    {
        // Unlike a project field, two providers is the expected case rather than a clash: they contribute different
        // variables and there is no key to collide on.
        var registry = new SessionResourceProviderRegistry();

        registry.Register(new StubProvider()).Should().BeTrue();
        registry.Register(new StubProvider()).Should().BeTrue();

        registry.Providers.Should().HaveCount(2);
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

        registry.Providers.Should().Equal(first, second);
    }

    [Fact]
    public void TheAppsOwnScan_ResolvesTheRegistry()
    {
        // The resolver takes ISessionResourceProviderRegistry as a constructor dependency, so a missing marker
        // interface is the app failing to start rather than a quiet degradation — nothing else here would notice,
        // since every other test builds the registry with new().
        var services = new ServiceCollection();
        services.AddServices(typeof(SessionResourceProviderRegistry).Assembly);

        services.BuildServiceProvider().GetService<ISessionResourceProviderRegistry>()
            .Should().BeOfType<SessionResourceProviderRegistry>();
    }
}
