using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.SystemMonitor;

/// <summary>
/// The system monitor. Its own plugin rather than half of a "reference widgets" pair, so it can be left out:
/// wanting a clock is not wanting a CPU meter (Raymond, 2026-07-15: "als ik wel de clock wil maar niet de
/// system monitor, wil ik dus alleen de clock downloaden en installeren"). One plugin per widget is what makes
/// that a choice instead of a package deal.
/// <para>
/// From the store, not bundled: the clock ships so a fresh Dashboard is not empty, and this comes when it is
/// wanted. It is also the half with settings, which is what proves the pane's ⚙ is really gated by
/// <see cref="WidgetRegistration.CreateConfigView"/> — the clock beside it has none and shows no gear.
/// </para>
/// </summary>
public sealed class SystemMonitorPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "system-monitor",
        DisplayName: "System Monitor",
        Version: "1.0.1",
        Author: "Cockpit",
        Description: "CPU, memory and disk usage for a Dashboard workspace. You pick which of the three in its settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
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

    public void Dispose()
    {
    }
}
