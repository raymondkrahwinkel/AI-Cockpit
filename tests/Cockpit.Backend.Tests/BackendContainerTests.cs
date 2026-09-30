using Cockpit.Core;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1373: the seams F0 found only App could fill (AC-1353, M1) resolve from Core and Infrastructure alone. AC-1425:
/// what journeys J1 and J6 resolve on startup went; the claim collision monitor is not one of them.
/// The container is built the way <c>Program.cs</c> builds it, minus the App assembly.
/// </summary>
public class BackendContainerTests
{
    [Theory]
    [InlineData(typeof(IClaimCollisionMonitor))]
    public async Task TheCoreAndInfrastructureContainer_ResolvesTheSeam_WithoutLoadingAvalonia(Type seam)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService(seam));
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }
}
