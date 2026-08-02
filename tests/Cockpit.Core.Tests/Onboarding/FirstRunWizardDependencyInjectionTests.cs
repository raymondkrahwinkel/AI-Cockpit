using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
    private static ServiceProvider BuildProvider(Action<ServiceCollection>? configure = null)
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

        configure?.Invoke(services);

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

    /// <summary>
    /// AC-511 adds its step the way the shell says a step is added — an <c>ISingletonService</c> marker and nothing
    /// else — so this is what catches a constructor dependency the container cannot satisfy: the step would then be
    /// absent from the wizard with nothing failing anywhere else.
    /// </summary>
    [Fact]
    public void TheContainer_ResolvesTheWorkKindStep_WithoutTheShellKnowingAboutIt()
    {
        using var provider = BuildProvider();

        var steps = provider.GetServices<IFirstRunWizardStep>().ToList();

        Assert.Contains(steps, step => step is WorkKindStep);
    }

    /// <summary>
    /// The Help menu reaches the wizard through an optional constructor parameter that defaults to null (AC-512),
    /// which is a shape that fails quietly: an unsatisfied parameter still compiles, still passes a test that only
    /// asks the container for <see cref="IFirstRunWizard"/>, and only shows up as a menu item that does nothing.
    /// Resolving the view model the way the app does and driving the command is the one shape that tells an
    /// injected wizard apart from the default.
    /// </summary>
    [Fact]
    public async Task TheResolvedCockpitViewModel_ReachesTheWizard_RatherThanItsNullDefault()
    {
        var wizard = Substitute.For<IFirstRunWizard>();
        // Awaited rather than plain `using`: resolving the view model puts an IAsyncDisposable in the container,
        // and a synchronous Dispose then throws over the top of whatever this test was actually asserting.
        await using var provider = BuildProvider(services => services.AddSingleton(wizard));

        var cockpit = provider.GetRequiredService<CockpitViewModel>();
        await cockpit.RunSetupAgainCommand.ExecuteAsync(null);

        await wizard.Received(1).ShowAsync(Arg.Any<CancellationToken>());
    }
}
