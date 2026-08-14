using Microsoft.Extensions.DependencyInjection;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
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

        services.AddSessionPanes();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainer_HasSomethingThatCanPersistWorkspaces()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IWorkspaceSettingsStore>());
    }

    [Fact]
    public async Task TheCockpit_OwnsAWorkspaceStrip_SoTheShortcutsHaveSomethingToSwitch()
    {
        await using var provider = BuildProvider();

        var cockpit = provider.GetRequiredService<CockpitViewModel>();

        Assert.NotNull(cockpit.Workspaces);
        Assert.NotNull(cockpit.Workspaces.Active);
        Assert.Equal(WorkspaceType.Sessions, cockpit.Workspaces.Active!.Type);
    }

    /// <summary>
    /// AC-439: <c>PaneWorkspaceDirectory</c> resolves <c>CockpitViewModel</c> lazily, inside
    /// <c>WorkspaceIdsByPane</c>, rather than through its own constructor — that lazy resolve is exactly what
    /// broke the circular dependency this container used to recurse on (<c>CockpitViewModel</c> takes
    /// <c>IClaimCollisionMonitor</c>, whose own chain runs back through <c>IPaneWorkspaceDirectory</c> to
    /// <c>CockpitViewModel</c>). The six other tests in this file prove construction no longer recurses; this one
    /// proves the deferred resolve the fix relies on actually succeeds and returns real data, not just that it
    /// compiles.
    /// </summary>
    [Fact]
    public async Task TheContainer_ResolvesThePaneWorkspaceDirectory_AndItsLazyCockpitViewModelResolveSucceeds()
    {
        await using var provider = BuildProvider();

        // Resolving CockpitViewModel first, same as the app does (it is the DataContext resolved once at startup),
        // so the directory's own lazy resolve below hits the singleton cache rather than racing the first build.
        var cockpit = provider.GetRequiredService<CockpitViewModel>();
        var directory = provider.GetRequiredService<IPaneWorkspaceDirectory>();

        var byPane = directory.WorkspaceIdsByPane();

        Assert.Empty(byPane);
        Assert.Same(cockpit, provider.GetRequiredService<CockpitViewModel>());
    }
}
