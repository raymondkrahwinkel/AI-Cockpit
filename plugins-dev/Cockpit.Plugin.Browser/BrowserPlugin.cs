using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser;

// The backend half of the browser plugin. It deliberately does nothing with CEF: the engine is fetched by the
// UI half (AC-1727), so a server, which loads only this assembly, never downloads one. What it does own is the
// Chromium agents browse with (AC-1712), which runs on the desktop and on the headless server alike.
public sealed class BrowserPlugin : ICockpitPlugin
{
    private AgentBrowserHost? _browsers;
    private AgentBrowserTabs? _tabs;
    private TabShareBridge? _share;

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
        var logger = host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Browser");
        var browsers = new AgentBrowserHost(() => runtime.Resolve(), Path.Combine(stateRoot, "browser-profiles"), logger);
        browsers.SweepStale();
        _browsers = browsers;

        var settings = new BrowserSettings(host.Storage);
        host.AddSessionResourceProvider(new AgentBrowserSessionResources(browsers, () => settings.AgentsUseCockpitBrowser));
        host.Sessions.SessionClosed += (_, paneId) => browsers.Stop(paneId);
        browsers.Changed += () => host.Channel.Publish("agents.changed", JsonSerializer.SerializeToElement(new { }));

        // AC-1713: the agent profile in the browser's Agents tab, over the plugin's own channel.
        var tabs = new AgentBrowserTabs(
            browsers,
            paneId => host.Sessions.OpenSessions.FirstOrDefault(session => session.PaneId == paneId)?.Name,
            (name, payload) => host.Channel.Publish(name, JsonSerializer.SerializeToElement(payload)));
        _tabs = tabs;
        AgentChannel.Register(host.Channel, tabs);

        // AC-1714: reading a tab the operator shared, answered by the UI half that holds the tabs and their grants.
        _share = new TabShareBridge(host);
        _ = host.AddMcpEndpoint("cockpit-browser", new TabShareMcpTools(host, _share));
    }

    public void Dispose()
    {
        _tabs?.Dispose();
        _share?.Dispose();
        _browsers?.Dispose();
    }
}
