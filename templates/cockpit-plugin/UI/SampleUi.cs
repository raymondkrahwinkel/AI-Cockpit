using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Sample.UI;

// The UI part (the manifest's uiEntryType): everything the plugin shows. Other places to show something are on
// the same host: AddSettings, AddSessionHeaderItem, AddShortcut, AddWidget, AddDockPanel (PLUGIN-SDK.md).
public sealed class SampleUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSideMenuButton("Sample", () => _ = host.ShowDialogAsync("Sample", () => new SamplePanelControl(host)));
    }
}
