using Avalonia.Controls;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
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

    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not part of the plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => BrowserScenes.All;

    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new BrowserEngineInfoControl(CefRuntimeCatalog.Load()));
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Browser", _ => _Home(host).CreateSlot(BrowserPlace.Tab))
        {
            IconKind = MaterialIconKind.Web,
            Description = "Browse the web with your own profile",
        });

        // KeepAlive: one slot for the plugin's lifetime, so collapsing the rail or floating it never rebuilds the page.
        host.AddDockPanel(new DockPanelRegistration(DockPanelId, "Browser", MaterialIconKind.Web, () => _Home(host).CreateSlot(BrowserPlace.Rail))
        {
            KeepAlive = true,
            CanFloat = true,
        });
    }

    public void Dispose() => BrowserEngine.Shared.Shutdown();

    private BrowserHome _Home(ICockpitUiHost host)
    {
        if (_home is not null)
        {
            return _home;
        }

        BrowserShell? shell = null;
        shell = new BrowserShell(new BrowserModel(BrowserEngine.Shared), url =>
        {
            if (TopLevel.GetTopLevel(shell) is { } top)
            {
                _ = top.Launcher.LaunchUriAsync(new Uri(url));
            }
        });
        _home = new BrowserHome(shell);
        _home.Moved += place => _ = place == BrowserPlace.Rail ? host.OpenDockPanelAsync(DockPanelId) : host.OpenWorkspaceAsync(WorkspaceTypeId);
        return _home;
    }
}
