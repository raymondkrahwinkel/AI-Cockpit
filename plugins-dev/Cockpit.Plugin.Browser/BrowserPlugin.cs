using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser;

// The backend half of the browser plugin. It deliberately does nothing with CEF: the engine is fetched by the
// UI half (AC-1727), so a server, which loads only this assembly, never downloads one.
public sealed class BrowserPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "browser",
        DisplayName: "Browser",
        Author: "Cockpit",
        Description: "A web browser inside Cockpit, on a Chromium engine fetched on first use.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
    }

    public void Dispose()
    {
    }
}
