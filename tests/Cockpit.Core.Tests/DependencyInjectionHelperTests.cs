using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Tests.TestDoubles;

namespace Cockpit.Core.Tests;

public class DependencyInjectionHelperTests
{
    [Fact]
    public void AddServices_ClassImplementingSingletonMarker_RegistersAsInterfaceAndSelf()
    {
        var services = new ServiceCollection();

        services.AddServices(typeof(DependencyInjectionHelperTests).Assembly);
        var provider = services.BuildServiceProvider();

        var byInterface = provider.GetService<IGreeter>();
        var bySelf = provider.GetService<SingletonGreeter>();

        Assert.NotNull(byInterface);
        Assert.IsType<SingletonGreeter>(byInterface);
        Assert.NotNull(bySelf);
    }

    [Fact]
    public void AddServices_ClassImplementingSingletonMarker_ResolvesSameInstanceAcrossScopes()
    {
        var services = new ServiceCollection();

        services.AddServices(typeof(DependencyInjectionHelperTests).Assembly);
        var provider = services.BuildServiceProvider();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var instanceA = scopeA.ServiceProvider.GetRequiredService<IGreeter>();
        var instanceB = scopeB.ServiceProvider.GetRequiredService<IGreeter>();

        Assert.Same(instanceA, instanceB);
    }
}
