using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

// A widget type together with the two things its owning plugin brought along: that plugin's storage slice
// and the session-observe surface it was handed. Recorded at registration because the dashboard builds an
// `IWidgetContext` per placed instance long after the plugin loaded, and by then the only thing
// linking an instance back to its plugin is the widget id — the registration alone carries no way home.
//
// `Registration`: What the plugin contributed.
// `PluginStorage`: The owning plugin's storage; a widget instance gets a per-instance slice of it.
// `Sessions`: The read/observe surface handed to that plugin's host.
// `DeclaredSecretKeys`:
// The storage keys the owning plugin declared as credentials in its manifest. Carried because an export has
// to drop them, and the name rule cannot guess a key called "pat" — without this the declaration would protect
// the backup and the at-rest encryption but not the file you hand to someone.
internal sealed record RegisteredWidget(
    WidgetRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions,
    IReadOnlyList<string> DeclaredSecretKeys);
