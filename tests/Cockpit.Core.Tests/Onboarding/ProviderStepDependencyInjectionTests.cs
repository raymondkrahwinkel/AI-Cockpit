using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Onboarding;

/// <summary>
/// The provider step (AC-510[b]) registers itself into the wizard's step list the same way <c>WelcomeStep</c>
/// does — via the Scrutor <c>ISingletonService</c> scan, not a hand-written registration in the wizard shell — and
/// its own dependencies (the store config/client and the provisioning seam) must all resolve from the same
/// container the composition root builds, in a separate file from <c>FirstRunWizardDependencyInjectionTests</c>
/// so it does not collide with AC-511's own step landing in that shared file.
/// </summary>
public class ProviderStepDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        return services.BuildServiceProvider();
    }

    // Awaited rather than plain `using`: AC-585's assistant step depends on `ISessionDialogService`, whose own
    // graph reaches an `IAsyncDisposable`-only singleton (`OrchestratorMcpServer`) — a synchronous `Dispose()`
    // over that throws on the way out, over whatever this test was actually asserting.
    [Fact]
    public async Task TheContainer_HasTheProviderStepRegistered()
    {
        await using var provider = BuildProvider();

        var steps = provider.GetServices<IFirstRunWizardStep>().ToList();

        Assert.Contains(steps, step => step is ProviderStep);
    }

    [Fact]
    public void TheContainer_ResolvesIPluginProvisioningService_AsASingleton()
    {
        using var provider = BuildProvider();

        var first = provider.GetService<IPluginProvisioningService>();
        var second = provider.GetService<IPluginProvisioningService>();

        Assert.NotNull(first);
        // AC-510[b]: one install path — the wizard's provider step and the plugin store dialog must share the
        // exact same instance, not each get their own.
        Assert.Same(first, second);
    }
}
