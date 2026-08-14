using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// The live-session registry only folds in the headless sources the container hands it (AC-106), so the wiring is
/// the thing that can be wrong: a delegation engine that is registered but never reaches the registry leaves
/// delegated worktrees unguarded exactly as before, and nothing about the classes themselves would show it.
/// Building the real container the way <c>Program.cs</c> does is the only place that answers this.
/// </summary>
public class LiveSessionRegistryDependencyInjectionTests
{
    private static ServiceProvider BuildProvider(ILiveSessionSource? extraSource = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        if (extraSource is not null)
        {
            services.AddSingleton(extraSource);
        }

        services.AddSessionPanes();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_GivesTheRegistryTheDelegationEngineAsALiveSessionSource()
    {
        await using var provider = BuildProvider();

        var sources = provider.GetServices<ILiveSessionSource>().ToList();

        Assert.Single(sources, source => source.GetType().Name == "DelegationService");
        Assert.IsType<LiveSessionRegistry>(provider.GetRequiredService<ILiveSessionRegistry>());
    }

    [Fact]
    public async Task Container_ResolvesTheRegistryAndTheDelegationEngineAsOneSharedInstanceEach()
    {
        // Both are singletons that hold state the other side reads — running tasks on one, the fold on the other.
        // A second instance of either would answer about tasks nobody started, which reads as "nothing is live".
        await using var provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<LiveSessionRegistry>(),
            provider.GetRequiredService<ILiveSessionRegistry>());
        Assert.Same(
            provider.GetRequiredService<Cockpit.Core.Abstractions.Delegation.IDelegationService>(),
            provider.GetServices<ILiveSessionSource>().Single());
    }

    [Fact]
    public async Task TheWorktreePanel_AsksTheRegistryRatherThanTheTabsDirectly()
    {
        // The panel is the operator's half of the same guard, and it used to read the open tabs straight — so a
        // session with no tab was free as far as Remove, "Clean up finished" and reattach were concerned. Wiring it
        // to the registry is one line in the cockpit's constructor, which is exactly the kind of line that can be
        // quietly put back without a single test noticing.
        var headless = Substitute.For<ILiveSessionSource>();
        headless.LiveSessionIds.Returns(new HashSet<string>(StringComparer.Ordinal) { "task-with-no-tab" });
        await using var provider = BuildProvider(headless);

        var cockpit = provider.GetRequiredService<CockpitViewModel>();

        Assert.NotNull(cockpit.Worktrees.LiveSessionIds);
        Assert.Contains("task-with-no-tab", cockpit.Worktrees.LiveSessionIds!());
    }
}
