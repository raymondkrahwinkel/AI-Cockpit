using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.Infrastructure.Tests.Shell;

/// <summary>
/// AC-1066: <c>cockpit-shell</c>'s registration shape — the two switches criterion 7 hangs on. <c>AlwaysMounted</c>
/// is what the generic mechanism (<c>AlwaysMountedServerTests</c>, <c>CockpitToolSearchTests</c>) already proves
/// reaches a delegated task and rides preloaded above the 30-tool threshold; what is specific to this endpoint is
/// that it actually carries that flag, and that its own <c>IsEnabled</c> reflects the shell-access master switch —
/// off by default, flips live.
/// </summary>
public class ShellEndpointRegistrationTests
{
    [Fact]
    public void CockpitShell_IsAlwaysMounted_AndNotInternal()
    {
        var services = new ServiceCollection().AddLogging().AddCore().AddInfrastructure()
            .AddServices(typeof(Cockpit.Core.DependencyInjection).Assembly, typeof(DependencyInjection).Assembly);
        var provider = services.BuildServiceProvider();

        var endpoint = provider.GetServices<CockpitMcpEndpoint>().Single(candidate => candidate.ServerName == "cockpit-shell");

        Assert.True(endpoint.AlwaysMounted);
        Assert.False(endpoint.Internal);
    }

    [Fact]
    public void CockpitShell_IsEnabled_DefaultsOff_AndFollowsTheSwitchLive()
    {
        var services = new ServiceCollection().AddLogging().AddCore().AddInfrastructure()
            .AddServices(typeof(Cockpit.Core.DependencyInjection).Assembly, typeof(DependencyInjection).Assembly);
        var provider = services.BuildServiceProvider();
        var endpoint = provider.GetServices<CockpitMcpEndpoint>().Single(candidate => candidate.ServerName == "cockpit-shell");

        Assert.False(endpoint.IsEnabled!());

        provider.GetRequiredService<IShellAccessSwitch>().Enabled = true;

        Assert.True(endpoint.IsEnabled!());
    }
}
