using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
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

    [Fact]
    public async Task AddServices_CycleBetweenScannedServices_FailsWithTheChainInsteadOfHanging()
    {
        var services = new ServiceCollection();

        services.AddServices(typeof(DependencyInjectionHelperTests).Assembly);
        var provider = services.BuildServiceProvider();

        // The test bounds its own wait: without the guard this deadlocks, and a bare Assert.Throws would
        // stall the whole run for the Blame timeout — the exact failure shape AC-1110 spent an evening on.
        var resolve = Task.Run(() => Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICycleStart>()));
        var finished = await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(resolve, finished);
        Assert.Equal(
            "A circular dependency was detected: ICycleStart -> ICycleEnd -> ICycleStart",
            (await resolve).Message);
    }

    // A deliberate cycle for the test above. Non-public on purpose: the scan takes non-public types too,
    // and nothing outside this file resolves them, so the other containers in the suite stay untouched.
    private interface ICycleStart;

    private interface ICycleEnd;

    private sealed class CycleStart(ICycleEnd end) : ICycleStart, ISingletonService
    {
        public ICycleEnd End { get; } = end;
    }

    private sealed class CycleEnd(ICycleStart start) : ICycleEnd, ISingletonService
    {
        public ICycleStart Start { get; } = start;
    }
}
