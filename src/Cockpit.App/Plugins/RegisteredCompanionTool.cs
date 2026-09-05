using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// A companion tool plus its owning plugin's storage slice and session-observe surface, recorded at registration
// since the tool id is the only link back to the plugin once its context is built. Mirrors RegisteredWidget,
// minus DeclaredSecretKeys: a companion tool declares no credentials of its own to scrub from an export.
internal sealed record RegisteredCompanionTool(
    CompanionToolRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions);
