using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Clock;

// The clock (AC-1395, pure UI per F2.1): its only contribution is `ICockpitUiHost.AddWidget` — which is exactly
// what a "standalone" widget is: there is no separate widget package and no second installer, so a clock ships,
// installs and is trusted the same way a provider or an issue tracker does.
//
// It ships with the app because a Dashboard workspace with nothing to put on it is a worse first impression
// than one that already has a clock. Its former other half, the system monitor, is now its own plugin from the
// store (wanting the clock but not the system monitor means being able to download and install just the clock)
// — one plugin per widget is what makes that possible. Between them they still
// prove the settings icon is really gated: this one has no settings, that one has.
public sealed class ClockUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // The type id keeps its "widgets." prefix on purpose. It is persisted with every placed instance, so
        // changing it would orphan the clocks on dashboards people have already arranged — the id is an API
        // surface, and which plugin delivers it was never part of that promise.
        //
        // The spans are what a freshly placed instance takes on the dashboard's default 24x24 grid — that grid
        // is a canvas to arrange on, not a slot count, so a couple of cells would land a postage stamp the
        // operator has to resize before they can read it.
        host.AddWidget(new WidgetRegistration("widgets.clock", "Clock", context => new ClockWidget(context))
        {
            IconKind = MaterialIconKind.ClockOutline,
            Description = "The time and date.",
            DefaultColumnSpan = 6,
            DefaultRowSpan = 4,
        });
    }
}
