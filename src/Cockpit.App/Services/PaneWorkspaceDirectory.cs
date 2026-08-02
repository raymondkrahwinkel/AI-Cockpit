using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Services;

// Host-side `IPaneWorkspaceDirectory` (AC-439) over the running session panels — the same seam
// `WorkspaceAgentGateway` is for one caller's own desk, generalised to every live pane at once. Goes
// through the same `SessionWorkspacePlacement` as that gateway rather than restating the rule, so a
// session started before workspaces existed resolves to the same desk here as it does when that session itself
// calls `list_agents` — and the assistant, which sits on no desk at all, is absent from both.
//
// Called from `Cockpit.App.Views.CockpitView`'s own UI-thread timer alongside the idle sweep and the
// resource sampler, so — unlike `WorkspaceAgentGateway`, which is reached from an MCP request thread
// and has to marshal — this is never called off the UI thread and takes no dispatch of its own.
//
// Takes `IServiceProvider` rather than `CockpitViewModel` directly and resolves it lazily
// inside `WorkspaceIdsByPane`, not in the constructor: `CockpitViewModel` itself takes
// `IClaimCollisionMonitor`, whose own dependency chain runs back through here — a straight
// constructor dependency on `CockpitViewModel` would make the container recurse into building
// `CockpitViewModel` a second time while still building it the first time (unlike
// `WorkspaceAgentGateway`, which nothing on `CockpitViewModel`'s own construction path depends
// on). By the time this is actually called — the 5s timer, well after startup — `CockpitViewModel`'s
// singleton entry is already cached, so the lazy resolve is just a cache hit, not a second construction.
internal sealed class PaneWorkspaceDirectory(IServiceProvider services) : IPaneWorkspaceDirectory, ISingletonService
{
    public IReadOnlyDictionary<string, string> WorkspaceIdsByPane()
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
    }
}
