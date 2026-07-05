using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Zyra.Voice.Core.Tests.TestDoubles;

namespace Zyra.Voice.Core.Tests;

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

        byInterface.Should().NotBeNull();
        byInterface.Should().BeOfType<SingletonGreeter>();
        bySelf.Should().NotBeNull();
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

        instanceA.Should().BeSameAs(instanceB);
    }
}
