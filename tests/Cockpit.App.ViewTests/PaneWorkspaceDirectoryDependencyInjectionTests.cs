using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class PaneWorkspaceDirectoryDependencyInjectionTests
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

    // Core.Tests has no pumped dispatcher; this path would otherwise be false-green or time out.
    /// <summary>
    /// AC-439: <c>PaneWorkspaceDirectory</c> resolves <c>CockpitViewModel</c> lazily in
    /// <c>WorkspaceIdsByPane</c>, breaking the circular dependency. This test proves the deferred resolve
    /// succeeds and returns real data, rather than merely compiling.
    /// </summary>
    [Fact]
    public async Task TheContainer_ResolvesThePaneWorkspaceDirectory_AndItsLazyCockpitViewModelResolveSucceeds()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            await using var provider = BuildProvider();
            var cockpit = provider.GetRequiredService<CockpitViewModel>();
            var directory = provider.GetRequiredService<IPaneWorkspaceDirectory>();

            var byPane = directory.WorkspaceIdsByPane();

            Assert.Empty(byPane);
            Assert.Same(cockpit, provider.GetRequiredService<CockpitViewModel>());
        });
    }
}
