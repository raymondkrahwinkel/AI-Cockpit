using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Services;

/// <summary>
/// Host-side <see cref="IPaneWorkspaceDirectory"/> (AC-439) over the running session panels — the same seam
/// <see cref="WorkspaceAgentGateway"/> is for one caller's own desk, generalised to every live pane at once. Mirrors
/// <see cref="WorkspaceAgentGateway"/>'s own "empty WorkspaceId falls back to the first Sessions workspace" rule
/// (see its <c>_ResolveWorkspaceId</c>), so a session started before workspaces existed resolves to the same desk
/// here as it does when that session itself calls <c>list_agents</c>.
/// <para>
/// Called from <see cref="Cockpit.App.Views.CockpitView"/>'s own UI-thread timer alongside the idle sweep and the
/// resource sampler, so — unlike <see cref="WorkspaceAgentGateway"/>, which is reached from an MCP request thread
/// and has to marshal — this is never called off the UI thread and takes no dispatch of its own.
/// </para>
/// <para>
/// Takes <see cref="IServiceProvider"/> rather than <see cref="CockpitViewModel"/> directly and resolves it lazily
/// inside <see cref="WorkspaceIdsByPane"/>, not in the constructor: <c>CockpitViewModel</c> itself takes
/// <see cref="IClaimCollisionMonitor"/>, whose own dependency chain runs back through here — a straight
/// constructor dependency on <c>CockpitViewModel</c> would make the container recurse into building
/// <c>CockpitViewModel</c> a second time while still building it the first time (unlike
/// <see cref="WorkspaceAgentGateway"/>, which nothing on <c>CockpitViewModel</c>'s own construction path depends
/// on). By the time this is actually called — the 5s timer, well after startup — <c>CockpitViewModel</c>'s
/// singleton entry is already cached, so the lazy resolve is just a cache hit, not a second construction.
/// </para>
/// </summary>
internal sealed class PaneWorkspaceDirectory(IServiceProvider services) : IPaneWorkspaceDirectory, ISingletonService
{
    public IReadOnlyDictionary<string, string> WorkspaceIdsByPane()
    {
        var cockpit = services.GetRequiredService<CockpitViewModel>();
        var firstSessionsWorkspaceId = cockpit.Workspaces.Settings.Workspaces
            .FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id;

        var byPane = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in cockpit.AllSessions().Where(session => session.ShowPluginHeaderItems))
        {
            var workspaceId = session.WorkspaceId.Length == 0 ? firstSessionsWorkspaceId ?? string.Empty : session.WorkspaceId;
            if (workspaceId.Length > 0)
            {
                byPane[session.PaneId] = workspaceId;
            }
        }

        return byPane;
    }
}
