using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// The workspace store reaches the cockpit through an <em>optional</em> constructor parameter — the shape that
/// compiles, runs, and quietly stays null, leaving a tab strip that forgets every workspace on restart. So the
/// container is built the way <c>Program.cs</c> builds it, and asked. (Same reasoning as
/// <c>BackupDependencyInjectionTests</c>.)
/// </summary>
public class WorkspaceDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<SessionViewModel>>(
            provider => () => provider.GetRequiredService<SessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainer_HasSomethingThatCanPersistWorkspaces()
    {
        using var provider = BuildProvider();

        provider.GetService<IWorkspaceSettingsStore>().Should().NotBeNull();
    }

    [Fact]
    public async Task TheCockpit_OwnsAWorkspaceStrip_SoTheShortcutsHaveSomethingToSwitch()
    {
        await using var provider = BuildProvider();

        var cockpit = provider.GetRequiredService<CockpitViewModel>();

        cockpit.Workspaces.Should().NotBeNull();
        cockpit.Workspaces.Active.Should().NotBeNull("the grid always needs a workspace to render");
        cockpit.Workspaces.Active!.Type.Should().Be(WorkspaceType.Sessions);
    }
}
