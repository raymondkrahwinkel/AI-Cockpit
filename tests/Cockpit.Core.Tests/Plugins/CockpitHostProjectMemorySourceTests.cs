using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="DesktopBackendHost.RemoveProjectMemorySource"/> (AC-501): the host's forwarding half of
/// <see cref="IProjectMemorySourceRegistry.Remove"/>, exercised through the real DI-resolved registry rather than a
/// mock — <see cref="ProjectMemorySourceRegistryTests"/> already covers the registry's own rules in isolation.
/// </summary>
public class CockpitHostProjectMemorySourceTests
{
    // --- AC-499: AddProjectMemorySourceFamily -----------------------------------------------------------------

    [Fact]
    public void AddProjectMemorySourceFamily_ASecondPluginDeclaringTheSameKey_IsIgnored()
    {
        // First one wins, the same rule AddProjectMemorySource's own scheme registration follows — a second plugin
        // declaring "depot" again must not silently replace the first's Title/EmptyHint/ConfigureAsync.
        var (host, registry) = _BuildHostAndRegistry();
        host.AddProjectMemorySourceFamily(new ProjectMemorySourceFamily("depot", "Depot"));

        host.AddProjectMemorySourceFamily(new ProjectMemorySourceFamily("depot", "Depot (second copy)"));

        Assert.Equal("Depot", Assert.Single(registry.Families).Title);
    }

    private static (ICockpitHost Host, IProjectMemorySourceRegistry Registry) _BuildHostAndRegistry()
    {
        var services = new ServiceCollection();
        services.AddServices(typeof(ProjectMemorySourceRegistry).Assembly);
        var provider = services.BuildServiceProvider();

        var host = new DesktopBackendHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

        return (host, provider.GetRequiredService<IProjectMemorySourceRegistry>());
    }
}
