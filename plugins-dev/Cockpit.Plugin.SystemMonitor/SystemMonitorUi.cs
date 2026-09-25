using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.SystemMonitor;

// The system monitor (AC-1395, pure UI per F2.1). Its own plugin rather than half of a "reference widgets"
// pair, so it can be left out: wanting a clock is not wanting a CPU meter (wanting the clock but not the
// system monitor means being able to download and install just the clock). One plugin per widget is what makes
// that a choice instead of a package deal.
//
// From the store, not bundled: the clock ships so a fresh Dashboard is not empty, and this comes when it is
// wanted. It is also the half with settings, which is what proves the pane's settings icon is really gated by
// `WidgetRegistration.CreateConfigView` — the clock beside it has none and shows no gear.
public sealed class SystemMonitorUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The type id keeps its "widgets." prefix on purpose: it is persisted with every placed instance, so
        // changing it would orphan the monitors on dashboards people have already arranged. The id is an API
        // surface; which plugin delivers it was never part of that promise.
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
