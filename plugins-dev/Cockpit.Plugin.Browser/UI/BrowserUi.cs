using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.Plugin.Browser.UI;

// The UI half of the browser plugin: the engine's version in the settings (AC-1727) and Raymond's browser as a tab
// and in the rail, one browser in either place (AC-1711). Nothing touches CEF until the browser is first opened;
// disposing shuts the engine down on the UI thread.
public sealed class BrowserUi : ICockpitPluginUi, IDisposable
{
    public const string WorkspaceTypeId = "browser.tab";

    public const string DockPanelId = "browser.rail";

    private BrowserHome? _home;
    private AgentTabsModel? _agents;

    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not part of the plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => BrowserScenes.All;

    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> WorkspaceScenes => BrowserScenes.Workspaces;

    public void InitializeUi(ICockpitUiHost host)
    {
        _agents = new AgentTabsModel(host.Channel);
        host.AddSettings(() => new BrowserEngineInfoControl(CefRuntimeCatalog.Load(), host.Storage));
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Browser", _ => _Home(host).CreateSlot(BrowserPlace.Tab))
        {
            IconKind = MaterialIconKind.Web,
            Description = "Browse the web with your own profile",
            HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () =>
            {
                _Home(host).DockInRail();
                return Task.CompletedTask;
            })],
        });

        // KeepAlive: one slot for the plugin's lifetime, so collapsing the rail or floating it never rebuilds the page.
        host.AddDockPanel(new DockPanelRegistration(DockPanelId, "Browser", MaterialIconKind.Web, () => _Home(host).CreateSlot(BrowserPlace.Rail))
        {
            KeepAlive = true,
            Badge = _agents.Badge,
            CanFloat = true,
        });

        // First thing when Cockpit closes, ahead of the session teardown and its exit watchdog; Dispose stays the
        // fallback for the tray's Quit, which does not raise this event (review AC-1711, finding 3).
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) => BrowserEngine.Shared.Shutdown();
        }
    }

    public void Dispose()
    {
        _agents?.Dispose();
        BrowserEngine.Shared.Shutdown();
    }

    // Mockup screens 1 and 2: what the status bar says about the browser; nothing while it has no open tab.
    internal static string? StatusText(BrowserPlace place, BrowserEngineState state, int tabs, bool agentSide = false, int agentTabs = 0) =>
        agentSide ? $"Agent profile · {agentTabs} {(agentTabs == 1 ? "tab" : "tabs")} by agents"
        : state != BrowserEngineState.Ready || tabs == 0 ? null
        : place == BrowserPlace.Rail ? "Browser docked in the rail"
        : $"Browser · profile Raymond · {tabs} {(tabs == 1 ? "tab" : "tabs")}";

    private BrowserHome _Home(ICockpitUiHost host)
    {
        if (_home is not null)
        {
            return _home;
        }

        BrowserShell? shell = null;
        shell = new BrowserShell(new BrowserModel(BrowserEngine.Shared), _agents!, url =>
        {
            if (TopLevel.GetTopLevel(shell) is { } top)
            {
                _ = top.Launcher.LaunchUriAsync(new Uri(url));
            }
        });
        var home = new BrowserHome(shell);
        home.Moved += place => _ = place == BrowserPlace.Rail ? host.OpenDockPanelAsync(DockPanelId) : host.OpenWorkspaceAsync(WorkspaceTypeId);
        void ShowStatus() => host.SetStatusText(StatusText(home.Place, shell.Model.Engine.State, shell.Model.Tabs.Count, _agents!.Showing, _agents.Tabs.Count));
        home.Changed += ShowStatus;
        _agents!.Changed += ShowStatus;
        shell.Model.Changed += ShowStatus;
        shell.Model.Engine.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ShowStatus);
        _home = home;
        return home;
    }
}
