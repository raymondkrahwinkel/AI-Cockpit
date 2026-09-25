using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Clock;

// The clock (AC-1395, pure UI per F2.1): its only contribution is `ICockpitUiHost.AddWidget`, which needs a
// window, so this project has no backend part at all — ships bundled so a fresh Dashboard is not empty.
public sealed class ClockUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // "widgets." prefix kept on purpose: persisted with every placed instance, an API surface of its own.
        host.AddWidget(new WidgetRegistration("widgets.clock", "Clock", context => new ClockWidget(context))
        {
            IconKind = MaterialIconKind.ClockOutline,
            Description = "The time and date.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 4,
        });
    }
}
