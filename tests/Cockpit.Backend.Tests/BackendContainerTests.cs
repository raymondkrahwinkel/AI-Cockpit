using Cockpit.Core;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1373: three of the five seams F0 found only App could fill (AC-1353, M1) now resolve from Core and
/// Infrastructure alone. The container is built the way <c>Program.cs</c> builds it, minus the App assembly.
/// </summary>
public class BackendContainerTests
{
    [Theory]
    [InlineData(typeof(IDelegationService))]
    [InlineData(typeof(IClaimCollisionMonitor))]
    [InlineData(typeof(IMcpToolProvider))]
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
