using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.RemoveProjectMemorySource"/> (AC-501): the host's forwarding half of
/// <see cref="IProjectMemorySourceRegistry.Remove"/>, exercised through the real DI-resolved registry rather than a
/// mock — <see cref="ProjectMemorySourceRegistryTests"/> already covers the registry's own rules in isolation.
/// </summary>
public class CockpitHostProjectMemorySourceTests
{
    [Fact]
    public void AddThenRemove_TakesTheSourceOutOfProjectMemorySources()
    {
        var host = _BuildHost();
        host.AddProjectMemorySource(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there."));

        host.RemoveProjectMemorySource("depot");

        Assert.Empty(host.ProjectMemorySources);
    }

    [Fact]
    public void Remove_ASchemeNeverRegistered_LeavesOtherSourcesUntouched()
    {
        var host = _BuildHost();
        host.AddProjectMemorySource(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there."));

        host.RemoveProjectMemorySource("notes");

        Assert.Single(host.ProjectMemorySources);
    }

    [Fact]
    public void RemoveThenAdd_TheSameScheme_RegistersTheNewContent()
    {
        // The live-refresh case DepotSettingsControl.Save leans on: a scheme just freed must be immediately
        // re-registrable with different content, not stuck refused as "already taken" by what was just removed.
        var host = _BuildHost();
        host.AddProjectMemorySource(new ProjectMemorySourceRegistration("depot", "Depot project", "Read it there."));
        host.RemoveProjectMemorySource("depot");

        host.AddProjectMemorySource(new ProjectMemorySourceRegistration("depot", "Depot project (renamed)", "Read it there."));

        Assert.Equal("Depot project (renamed)", Assert.Single(host.ProjectMemorySources).Title);
    }

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

    private static ICockpitHost _BuildHost() => _BuildHostAndRegistry().Host;

    private static (ICockpitHost Host, IProjectMemorySourceRegistry Registry) _BuildHostAndRegistry()
    {
        var services = new ServiceCollection();
        services.AddServices(typeof(ProjectMemorySourceRegistry).Assembly);
        var provider = services.BuildServiceProvider();

        var host = new CockpitHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

        return (host, provider.GetRequiredService<IProjectMemorySourceRegistry>());
    }
}
