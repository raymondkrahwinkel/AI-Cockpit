using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.ClaimProjectOwnership"/>/<see cref="CockpitHost.GetProjectFieldOwnership"/> (AC-604): the
/// host's forwarding half of <see cref="IProjectOwnershipRegistry"/>, exercised through the real DI-resolved
/// registry rather than a mock — <see cref="ProjectOwnershipRegistryTests"/> already covers the registry's own rules.
/// </summary>
public class CockpitHostProjectOwnershipTests
{
    [Fact]
    public void ClaimThenGet_ReturnsWhatWasClaimed()
    {
        var host = _BuildHost();

        host.ClaimProjectOwnership(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work", IsEditable: true)));

        var ownership = host.GetProjectFieldOwnership("proj-1");
        Assert.Equal("Depot — Work", ownership![HostProjectField.Name]!.SourceName);
    }

    [Fact]
    public void Get_AProjectNoOneClaimed_IsNull()
    {
        var host = _BuildHost();

        Assert.Null(host.GetProjectFieldOwnership("never-claimed"));
    }

    [Fact]
    public void Claim_ASecondPluginClaimingTheSameProject_IsIgnored()
    {
        var host = _BuildHost();
        host.ClaimProjectOwnership(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work")));

        host.ClaimProjectOwnership(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("A different plugin")));

        Assert.Equal("Depot — Work", host.GetProjectFieldOwnership("proj-1")![HostProjectField.Name]!.SourceName);
    }

    private static ICockpitHost _BuildHost()
    {
        var services = new ServiceCollection();
        services.AddServices(typeof(ProjectOwnershipRegistry).Assembly);
        var provider = services.BuildServiceProvider();

        return new CockpitHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }
}
