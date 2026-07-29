using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <c>ISessionStateStore</c> is internal to Cockpit.Infrastructure, and <c>SessionStateRecorder</c> takes both it
/// and the conversation tracker as constructor arguments — a type that is not registered with Scrutor's
/// marker-interface scan is not an error anywhere, it is a silent <c>GetService</c> null, and every call site
/// that hands the recorder around by hand still compiles while the running cockpit persists nothing at all
/// (AC-409, same shape as AC-408's <c>SessionConversationSinkDependencyInjectionTests</c>). Building the real
/// container the way <c>Program.cs</c> does is the only place that catches it.
/// </summary>
public class SessionStateStoreDependencyInjectionTests
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
    public async Task Container_ResolvesTheSessionStateStore()
    {
        await using var provider = BuildProvider();

        Assert.IsType<SessionStateStore>(provider.GetService<ISessionStateStore>());
    }

    [Fact]
    public async Task Container_ResolvesTheSessionStateRecorder()
    {
        await using var provider = BuildProvider();

        Assert.NotNull(provider.GetService<SessionStateRecorder>());
    }
}
