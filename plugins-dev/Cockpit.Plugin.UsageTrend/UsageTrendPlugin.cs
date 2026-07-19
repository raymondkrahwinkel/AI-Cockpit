using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.UsageTrend;

/// <summary>
/// The usage-trend widget's plugin (AC-54): its only contribution is one dashboard widget that charts the
/// context / 5h / weekly usage of your sessions over time, per profile. Bundled like the clock so the trend is
/// there out of the box, and it is what proves the AC-54 read surface end to end from outside the host — a widget
/// reads a session's live usage through <c>ICockpitSessionObserver.ActiveSessionUsage</c> and keeps its own
/// history, with no core knowledge of what it shows.
/// </summary>
public sealed class UsageTrendPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "usage-trend",
        DisplayName: "Usage Trend",
        // Kept in lockstep with plugin.json's "version": the manifest gates loading, this shows in the plugin list,
        // and a mismatch would have the two disagree about what is installed.
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Charts the context / 5h / weekly usage of your sessions over time, per profile, on a Dashboard workspace.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // The type id is persisted with every placed instance, so it is an API surface — changing it would orphan
        // trends on dashboards people have already arranged. No settings form: v1 always shows all three metrics,
        // so there is nothing to configure and the pane shows no gear (CreateConfigView left null).
        host.AddWidget(new WidgetRegistration("widgets.usage-trend", "Usage Trend", context => new UsageTrendWidget(context))
        {
            IconKind = MaterialIconKind.ChartTimelineVariant,
            Description = "Context, 5h and weekly usage over time, per profile.",
            DefaultColumnSpan = 8,
            DefaultRowSpan = 4,
        });
    }

    public void Dispose()
    {
    }
}
