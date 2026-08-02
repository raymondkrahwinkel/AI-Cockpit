using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Onboarding;

/// <summary>
/// The container is built the way <c>Program.cs</c> builds it (AC-509), the same shape
/// <c>BackupDependencyInjectionTests</c> uses: <c>IFirstRunWizard</c> and its state store are picked up by the
/// Scrutor scan through their <c>ISingletonService</c> marker rather than a hand-written registration, so this is
/// what would have caught a typo in either — a missing registration compiles fine and only fails at the Help
/// menu's "Run setup again" (AC-512) or at the very first launch.
/// </summary>
public class FirstRunWizardDependencyInjectionTests
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
        services.AddTransient<Func<TtyViewModel>>(
            provider => () => provider.GetRequiredService<TtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainer_ResolvesTheWizardAndItsStateStore()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<IFirstRunWizard>());
        Assert.NotNull(provider.GetService<IFirstRunWizardStateStore>());
    }

    [Fact]
    public void TheContainer_HasAtLeastTheWelcomeStepRegistered()
    {
        using var provider = BuildProvider();

        var steps = provider.GetServices<IFirstRunWizardStep>().ToList();

        Assert.Contains(steps, step => step is WelcomeStep);
    }
}
