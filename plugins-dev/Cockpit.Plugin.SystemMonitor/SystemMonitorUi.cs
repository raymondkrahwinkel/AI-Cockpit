using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.SystemMonitor;

// The system monitor (AC-1395, pure UI per F2.1). From the store, not bundled — the clock ships so a fresh
// Dashboard is not empty, this comes when wanted. `ICockpitUiHost.AddWidget` needs a window, so no backend part.
public sealed class SystemMonitorUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // "widgets." prefix kept on purpose: persisted with every placed instance, an API surface of its own.
        host.AddWidget(new WidgetRegistration("widgets.system-monitor", "System Monitor", context => new SystemMonitorWidget(context))
        {
            IconKind = MaterialIconKind.ChartLine,
            Description = "CPU, memory and disk usage.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 6,
            CreateConfigView = context => new SystemMonitorSettingsView(context),
        });
    }
}
