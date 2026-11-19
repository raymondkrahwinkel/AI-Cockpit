using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Configuration;

// Read before Avalonia and DI start so the first window uses the saved graphics settings.
public static class RenderBackendConfig
{
    public static RenderingSettings Read()
    {
        try
        {
            var configFile = new CockpitConfigFileAccess(CockpitConfigPath.Default)
                .ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            return configFile?.Rendering?.ToDomain() ?? new RenderingSettings();
        }
        catch
        {
            return new RenderingSettings();
        }
    }
}
