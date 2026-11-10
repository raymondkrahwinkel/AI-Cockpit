using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser;

// The backend half of the browser plugin. It deliberately does nothing with CEF: the engine is fetched by the
// UI half (AC-1727), so a server, which loads only this assembly, never downloads one. What it does own is the
// Chromium agents browse with (AC-1712), which only runs where the host has a desktop UI.
public sealed class BrowserPlugin : ICockpitPlugin
{
    private AgentBrowserHost? _browsers;
    private AgentBrowserTabs? _tabs;
    private TabShareBridge? _share;
    private Action? _unsubscribe;

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
        var settings = new BrowserSettings(host.Storage);
        if (host.IsHeadless)
        {
            host.AddSessionResourceProvider(new AgentBrowserSessionResources(null, () => settings.AgentsUseCockpitBrowser));
            return;
        }

        // Under the host's state root when it was moved (COCKPIT_STATE_ROOT), so a second cockpit keeps its own runtimes.
        var stateRoot = Environment.GetEnvironmentVariable("COCKPIT_STATE_ROOT") is { Length: > 0 } root
            ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit");
        var runtime = new AgentBrowserRuntime(Path.Combine(stateRoot, "browser-runtimes"), new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var logger = host.Services?.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Browser");
        var browsers = new AgentBrowserHost(() => runtime.Resolve(), Path.Combine(stateRoot, "browser-profiles"), logger);
        browsers.SweepStale();
        _browsers = browsers;

        host.AddSessionResourceProvider(new AgentBrowserSessionResources(browsers, () => settings.AgentsUseCockpitBrowser));
        // AC-2151: a disabled instance must not keep its browsers' state reachable through the host.
        var sessions = host.Sessions;
        EventHandler<string> sessionClosed = (_, paneId) => browsers.Stop(paneId);
        sessions.SessionClosed += sessionClosed;
        _unsubscribe = () => sessions.SessionClosed -= sessionClosed;
        browsers.Changed += () => host.Channel.Publish("agents.changed", JsonSerializer.SerializeToElement(new { }));

        // AC-1713: the agent profile in the browser's Agents tab, over the plugin's own channel.
        string? OpenName(string paneId) => host.Sessions.OpenSessions.FirstOrDefault(session => session.PaneId == paneId)?.Name;
        var owned = new AgentOwnedTabs(browsers, new AddressPolicy(() => settings.AgentNetworkAllowlist),
            (paneId, target) => host.Channel.Publish("agents.show", JsonSerializer.SerializeToElement(new { paneId, targetId = target })), OpenName);
        var tabs = new AgentBrowserTabs(
            browsers,
            paneId => OpenName(paneId) ?? owned.OriginOfRetained(paneId),
            (name, payload) => host.Channel.Publish(name, JsonSerializer.SerializeToElement(payload)),
            logger,
            openedForOperator: owned.IsOperatorTab);
        _tabs = tabs;
        AgentChannel.Register(host.Channel, tabs);

        // AC-1714: reading a tab the operator shared, answered by the UI half that holds the tabs and their grants.
        _share = new TabShareBridge(host);
        _ = host.AddMcpEndpoint("cockpit-browser", new TabShareMcpTools(host, _share, owned));
    }

    public void Dispose()
    {
        _unsubscribe?.Invoke();
        _tabs?.Dispose();
        _share?.Dispose();
        _browsers?.Dispose();
    }
}
