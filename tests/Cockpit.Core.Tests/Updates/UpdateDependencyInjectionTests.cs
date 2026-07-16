using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The Updates tab has a "Check now" button (#71), and it reaches its service through an optional constructor
/// parameter — the shape that compiles, runs, and quietly stays null. So the container is built the way
/// <c>Program.cs</c> builds it, and asked.
/// </summary>
public class UpdateDependencyInjectionTests
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
    public async Task TheContainer_HasSomethingThatCanLookForANewerCockpit()
    {
        await using var provider = BuildProvider();

        var updates = provider.GetRequiredService<IUpdateService>();

        // It must know what it is, or it cannot say what is newer than it.
        updates.Current.Version.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheCockpit_IsBuiltWithIt_SoCheckNowIsNotADeadButton()
    {
        await using var provider = BuildProvider();

        provider.GetRequiredService<CockpitViewModel>().CanCheckForUpdates.Should().BeTrue();
    }
}
