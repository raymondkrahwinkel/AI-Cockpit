using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser;

// The backend half of the browser plugin. It deliberately does nothing with CEF: the engine is fetched by the
// UI half (AC-1727), so a server, which loads only this assembly, never downloads one. What it does own is the
// Chromium agents browse with (AC-1712), which runs on the desktop and on the headless server alike.
public sealed class BrowserPlugin : ICockpitPlugin
{
    private AgentBrowserHost? _browsers;

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
        // Under the host's state root when it was moved (COCKPIT_STATE_ROOT), so a second cockpit keeps its own runtimes.
        var stateRoot = Environment.GetEnvironmentVariable("COCKPIT_STATE_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit");
        var runtime = new AgentBrowserRuntime(Path.Combine(stateRoot, "browser-runtimes"), new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var browsers = new AgentBrowserHost(() => runtime.Resolve(), Path.Combine(stateRoot, "browser-profiles"));
        browsers.SweepStale();
        _browsers = browsers;

        var settings = new BrowserSettings(host.Storage);
        host.AddSessionResourceProvider(new AgentBrowserSessionResources(browsers, () => settings.AgentsUseCockpitBrowser));
        host.Sessions.SessionClosed += (_, paneId) => browsers.Stop(paneId);
    }

    public void Dispose() => _browsers?.Dispose();
}
