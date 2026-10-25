using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Browser.UI;

// The UI half of the browser plugin. For now only the engine's version in the plugin settings; the browser
// itself comes with AC-1711.
public sealed class BrowserUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host) =>
        host.AddSettings(() => new BrowserEngineInfoControl(CefRuntimeCatalog.Load()));
}
