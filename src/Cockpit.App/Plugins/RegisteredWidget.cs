using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

/// <summary>
/// A widget type together with the two things its owning plugin brought along: that plugin's storage slice
/// and the session-observe surface it was handed. Recorded at registration because the dashboard builds an
/// <see cref="IWidgetContext"/> per placed instance long after the plugin loaded, and by then the only thing
/// linking an instance back to its plugin is the widget id — the registration alone carries no way home.
/// </summary>
/// <param name="Registration">What the plugin contributed.</param>
/// <param name="PluginStorage">The owning plugin's storage; a widget instance gets a per-instance slice of it.</param>
/// <param name="Sessions">The read/observe surface handed to that plugin's host.</param>
internal sealed record RegisteredWidget(
    WidgetRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions);
