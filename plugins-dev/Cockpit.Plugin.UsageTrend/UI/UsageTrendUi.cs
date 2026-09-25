using Material.Icons;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.UsageTrend.UI;

// The usage-trend widget's UI part (AC-54; AC-1395 split): the backend part (UsageTrendPlugin) keeps the
// debounce/jump/retention rules and answers this widget's reads and writes over the plugin's own channel.
public sealed class UsageTrendUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // "widgets." prefix kept on purpose: persisted with every placed instance, an API surface of its own.
        // No settings form: v1 always shows all three metrics (CreateConfigView left null).
        host.AddWidget(new WidgetRegistration("widgets.usage-trend", "Usage Trend", context => new UsageTrendWidget(context, host.Channel))
        {
            IconKind = MaterialIconKind.ChartTimelineVariant,
            Description = "Context, 5h and weekly usage over time, per profile.",
            DefaultColumnSpan = 8,
            DefaultRowSpan = 4,
        });
    }
}
