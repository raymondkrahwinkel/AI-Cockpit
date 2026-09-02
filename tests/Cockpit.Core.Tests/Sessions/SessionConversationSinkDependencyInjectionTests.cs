using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Both routes take the conversation sink as an optional constructor argument and resolve it with
/// <c>GetService</c> (AC-408), so a sink that is never registered is not an error anywhere — it is a silent
/// null, and every test that hands a driver its own sink still passes while the running cockpit reports
/// nothing at all. Building the real container the way <c>Program.cs</c> does is the only place that catches it.
/// </summary>
public class SessionConversationSinkDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_ResolvesTheSinkAndTheTrackerAsOneSharedInstance()
    {
        // The tracker only suppresses a repeat report because it remembers what a pane last reported. A second
        // instance behind the interface would remember nothing the first was told, so every route would look
        // like a change to whoever holds the other one.
        await using var provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<SessionConversationTracker>(),
            provider.GetRequiredService<ISessionConversationSink>());
    }
}
