using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Composition;

// AC-1441: desktop services whose in-proc half is a backend call; it is handed in here, so the services never name it.
internal static class DesktopComposition
{
    public static IServiceCollection AddBackendSeams(this IServiceCollection services)
    {
        services.AddSingleton(provider => new ResourceMonitor(
            provider.GetRequiredService<IProcessTableReader>(),
            LinuxSessionCgroup.PressureAvg10));

        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DevPluginReloadWatcher>>();
            return new DevPluginReloadWatcher(
                DevPluginInstaller.FindPluginsDevRoot,
                cancellationToken => new DevPluginInstaller(logger).InstallAsync(PluginBootstrap.PluginsRoot, cancellationToken),
                provider.GetRequiredService<IToastService>(),
                provider.GetRequiredService<IAppRestartService>(),
                logger,
                debounce: null);
        });

        services.AddSingleton(provider =>
        {
            var bootstrap = provider.GetRequiredService<PluginBootstrap>();
            return new PluginUpdateChecker(
                cancellationToken => bootstrap.DiscoverAsync(AbstractionsContract.Version, cancellationToken),
                provider.GetRequiredService<IPluginStoreConfigStore>(),
                provider.GetRequiredService<IPluginStoreClient>(),
                provider.GetRequiredService<IToastService>(),
                provider.GetRequiredService<CockpitViewModel>(),
                provider.GetRequiredService<ILogger<PluginUpdateChecker>>());
        });
        services.AddSingleton<IPluginUpdateChecker>(provider => provider.GetRequiredService<PluginUpdateChecker>());

        return services;
    }
}
