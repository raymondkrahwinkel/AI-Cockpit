using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// The CockpitUiHost a plugin's UI part receives, over a real service collection (the channel hub is always there) and
// substitutes for everything a test does not look at.
internal static class TestUiHost
{
    public static CockpitUiHost Create(
        Action<IServiceCollection>? configure = null,
        IPluginContributionSink? sink = null,
        IPluginDialogHost? dialogHost = null,
        ICockpitHost? host = null,
        PluginChannelHub? hub = null,
        IPluginActiveSession? activeSession = null,
        string pluginId = "test-plugin",
        string pluginName = "Test Plugin")
    {
        var services = new ServiceCollection();
        services.AddSingleton(hub ?? new PluginChannelHub(NullLogger<PluginChannelHub>.Instance));
        configure?.Invoke(services);

        return new CockpitUiHost(
            pluginId,
            pluginName,
            host ?? Substitute.For<ICockpitHost>(),
            services.BuildServiceProvider(),
            sink ?? Substitute.For<IPluginContributionSink>(),
            dialogHost ?? Substitute.For<IPluginDialogHost>(),
            Substitute.For<IPluginWindowActions>(),
            activeSession ?? Substitute.For<IPluginActiveSession>(),
            []);
    }
}
