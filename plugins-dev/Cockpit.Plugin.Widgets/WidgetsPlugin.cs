using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Widgets;

/// <summary>
/// The reference widgets. A plugin whose only contribution is <see cref="ICockpitHost.AddWidget"/> — which is
/// exactly what a "standalone" widget is: there is no separate widget package and no second installer, so a
/// clock ships, installs and is trusted the same way a provider or an issue tracker does. It publishes under
/// the store's "Widgets" category, which is the whole of what makes it show up there.
/// </summary>
public sealed class WidgetsPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "widgets",
        DisplayName: "Reference widgets",
        Version: "1.0.0",
        Author: "Cockpit",
        Description: "A clock and a system monitor for a Dashboard workspace. The clock shows the time and date; the system monitor shows CPU, memory and disk, and you pick which of the three in its settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddWidget(new WidgetRegistration("widgets.clock", "Clock", context => new ClockWidget(context))
        {
            Icon = "🕐",
            Description = "The time and date.",
        });

        // The one with settings, deliberately: it proves the config path end to end — CreateConfigView is what
        // puts the ⚙ on the pane, and the clock next to it having none proves the gear is really gated.
        host.AddWidget(new WidgetRegistration("widgets.system-monitor", "System Monitor", context => new SystemMonitorWidget(context))
        {
            Icon = "📈",
            Description = "CPU, memory and disk usage.",
            CreateConfigView = context => new SystemMonitorSettingsView(context),
        });
    }

    public void Dispose()
    {
    }
}
