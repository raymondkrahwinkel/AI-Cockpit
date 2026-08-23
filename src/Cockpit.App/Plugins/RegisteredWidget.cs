using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

// A widget type plus its owning plugin's storage slice and session-observe surface, recorded at
// registration since the widget id is the only link back to the plugin once an instance is built.
// `DeclaredSecretKeys`: credential keys the plugin declared, carried so export can drop them.
internal sealed record RegisteredWidget(
    WidgetRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions,
    IReadOnlyList<string> DeclaredSecretKeys);
