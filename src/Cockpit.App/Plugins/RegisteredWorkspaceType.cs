using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// A workspace type plus its owning plugin's storage slice and session-observe surface, recorded
// at registration since the type id is the only link back to the plugin once a saved desk rebuilds
// its `IWorkspaceContext` on a later restart.
internal sealed record RegisteredWorkspaceType(
    WorkspaceTypeRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions);
