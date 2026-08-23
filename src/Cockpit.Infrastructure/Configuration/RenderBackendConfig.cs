using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Configuration;

// AC-67: reads just the persisted render backend for `Program.BuildAvaloniaApp()`'s pre-container pass,
// before the DI host exists. Public, like `PluginBootstrap`, so it's callable statically. Returns
// `Auto` on any absence or read error — a broken config must never stop the app from starting.
public static class RenderBackendConfig
{
    public static RenderBackendChoice Read() => Read(CockpitConfigPath.Default);

    internal static RenderBackendChoice Read(string configFilePath)
    {
        try
        {
            var configFile = new CockpitConfigFileAccess(configFilePath)
                .ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return configFile?.Rendering?.ToDomain().Backend ?? RenderBackendChoice.Auto;
        }
        catch
        {
            return RenderBackendChoice.Auto;
        }
    }
}
