using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Infrastructure;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Guards that the permission-rule wiring resolves the same way <c>Program.cs</c> builds it: the
/// store must register (a missing registration would break every session's construction, since
/// <c>ClaudeCliSession</c> now depends on it).
/// </summary>
public class PermissionDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<ClaudeSessionViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeSessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_ResolvesThePermissionRuleStoreAndACliSession()
    {
        await using var provider = BuildProvider();

        provider.GetService<IPermissionRuleStore>().Should().NotBeNull();
        provider.GetService<IPermissionCoordinator>().Should().NotBeNull();
        provider.GetService<IClaudeSession>().Should().NotBeNull();
    }
}
