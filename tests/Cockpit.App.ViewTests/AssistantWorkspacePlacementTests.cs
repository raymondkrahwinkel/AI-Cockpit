using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.Infrastructure.Agents;
using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The assistant sits on no workspace, and nothing quietly puts it on one (AC-543, criterion 10).
/// </summary>
/// <remarks>
/// This is the test for an inventory rather than for a feature, which is why it is written as one case per place
/// that used to decide the question for itself. Before AC-543 the rule — an unassigned session belongs to the
/// first Sessions workspace — was written out three separate times, and each copy would have had to be found and
/// taught about the assistant on its own. They now share
/// <see cref="SessionWorkspacePlacement"/>; these cases hold each of the three <em>consumers</em> to the answer,
/// so a future fourth copy of the rule fails here rather than in production.
/// <para>
/// Every case seeds a Sessions workspace on purpose. Without one there is nothing for the fallback to fall back
/// to, so the assistant would be excluded for the wrong reason and the test would pass against an implementation
/// that never learned about it at all.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantWorkspacePlacementTests
{
    [Fact]
    public void ListAgents_TheAssistant_IsNotReportedAsANeighbour()
    {
        var (gateway, desk, assistant) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _CockpitWithASessionsDesk(out var deskId);
            var session = new SessionViewModel { WorkspaceId = deskId };
            var assistantSession = new SessionViewModel { BelongsToNoWorkspace = true, WorkspaceId = deskId };
            cockpit.Sessions.Add(session);
            cockpit.Sessions.Add(assistantSession);

            return (new WorkspaceAgentGateway(cockpit, new WorkspaceAgentCoordinator(), NullLogger<WorkspaceAgentGateway>.Instance), session, assistantSession);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(desk.PaneId).GetAwaiter().GetResult());

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Panes, pane => pane.PaneId == desk.PaneId);

        // The assistant is stamped with the caller's own desk id here on purpose: the marker has to win over an
        // explicit WorkspaceId, not merely over the empty-means-first-desk fallback. Asserting absence rather than
        // a count, because this cockpit is the design-time graph and comes with demo sessions of its own — a count
        // would be a test about those.
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == assistant.PaneId);
    }

    [Fact]
    public void ListAgents_CalledByTheAssistantItself_IsRefusedRatherThanPlacedOnTheFirstDesk()
    {
        var (gateway, assistant) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _CockpitWithASessionsDesk(out _);
            var session = new SessionViewModel { BelongsToNoWorkspace = true };
            cockpit.Sessions.Add(session);

            return (new WorkspaceAgentGateway(cockpit, new WorkspaceAgentCoordinator(), NullLogger<WorkspaceAgentGateway>.Instance), session);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(assistant.PaneId).GetAwaiter().GetResult());

        // Not an empty snapshot: null is the refusal AgentsMcpTools turns into "this session is not one the cockpit
        // can place in a workspace". A snapshot naming a desk would be the silent fallback this rule forbids.
        Assert.Null(snapshot);
    }

    [Fact]
    public void ClaimCollisions_TheAssistant_IsAbsentFromThePaneDirectory()
    {
        var (directory, desk, assistant) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _CockpitWithASessionsDesk(out var deskId);
            var session = new SessionViewModel { WorkspaceId = deskId };
            var assistantSession = new SessionViewModel { BelongsToNoWorkspace = true };
            cockpit.Sessions.Add(session);
            cockpit.Sessions.Add(assistantSession);

            return (new PaneWorkspaceDirectory(new _SingleObjectProvider(cockpit)), session, assistantSession);
        });

        var byPane = Dispatcher.UIThread.Invoke(directory.WorkspaceIdsByPane);

        Assert.True(byPane.ContainsKey(desk.PaneId));
        Assert.False(byPane.ContainsKey(assistant.PaneId));
    }

    [Fact]
    public void Sidebar_TheAssistant_IsNeverListedOnTheActiveDesk()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _CockpitWithASessionsDesk(out var deskId);
            var onTheDesk = new SessionViewModel { WorkspaceId = deskId };
            var assistant = new SessionViewModel { BelongsToNoWorkspace = true };
            cockpit.Sessions.Add(onTheDesk);
            cockpit.Sessions.Add(assistant);

            // VisibleSessions and the grid's own pane visibility ask the same question (BelongsToActiveWorkspace),
            // so this covers both surfaces. An assistant that resolved to the first Sessions desk would take a row
            // in the sidebar and a tile in the grid — visibly wrong, but only once someone happened to look.
            var visible = cockpit.VisibleSessions.ToList();

            Assert.Contains(onTheDesk, visible);
            Assert.DoesNotContain(assistant, visible);
        });
    }

    /// <summary>A cockpit with one Sessions desk — the desk the old fallback would have handed the assistant.</summary>
    private static CockpitViewModel _CockpitWithASessionsDesk(out string deskId)
    {
        var cockpit = new CockpitViewModel();
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        deskId = desk.Id;
        cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id };
        return cockpit;
    }

    /// <summary>The one dependency <see cref="PaneWorkspaceDirectory"/> resolves lazily; a container is more machinery than this needs.</summary>
    private sealed class _SingleObjectProvider(object instance) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(instance) ? instance : null;
    }
}
