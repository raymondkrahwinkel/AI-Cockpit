using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// A workspace type together with what its owning plugin brought along: that plugin's storage slice and the
/// session-observe surface it was handed. Recorded at registration because a workspace of this type builds its
/// <see cref="IWorkspaceContext"/> long after the plugin loaded — on a saved desk, a restart later — and by
/// then the only thing linking it back to its plugin is the type id; the registration alone carries no way home.
/// </summary>
/// <param name="Registration">What the plugin contributed.</param>
/// <param name="PluginStorage">The owning plugin's storage; a workspace gets a per-workspace slice of it.</param>
/// <param name="Sessions">The read/observe surface handed to that plugin's host.</param>
internal sealed record RegisteredWorkspaceType(
    WorkspaceTypeRegistration Registration,
    IPluginStorage PluginStorage,
    ICockpitSessionObserver Sessions);
