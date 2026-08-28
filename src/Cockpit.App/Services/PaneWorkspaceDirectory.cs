using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Services;

// Host-side `IPaneWorkspaceDirectory` (AC-439): `WorkspaceAgentGateway`'s single-desk seam generalised to
// every live pane, marshalled through `UiThreadCall` like the gateway — AC-1201: `Task.Run` calls it off-thread too.
// AC-1013: resolves `CockpitViewModel` lazily via `IServiceProvider`, not constructor injection, since it depends on `IClaimCollisionMonitor` which chains back through here and would make the container recurse.
internal sealed class PaneWorkspaceDirectory(IServiceProvider services) : IPaneWorkspaceDirectory, ISingletonService
{
    public IReadOnlyDictionary<string, string> WorkspaceIdsByPane() => UiThreadCall.Run(() =>
    {
        var cockpit = services.GetRequiredService<CockpitViewModel>();
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);

        var byPane = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in cockpit.AllSessions().Where(session => session.ShowPluginHeaderItems))
        {
            // A pane placed nowhere is left out of the directory entirely, which is what collision detection
            // wants: the assistant shares no desk with anything, so it can collide with nothing.
            if (SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId) is { } workspaceId)
            {
                byPane[session.PaneId] = workspaceId;
            }
        }

        return byPane;
    });
}
