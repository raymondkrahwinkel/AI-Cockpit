using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="WorkspaceAgentGateway"/> is where workspace isolation for the agent-coordination line (AC-391) is
/// actually enforced: it decides which sessions share a workspace, and therefore which panes an agent's
/// <c>list_agents</c> call can ever see. The existing unit tests under
/// <c>Cockpit.Infrastructure.Tests/Agents</c> stub <c>IWorkspaceAgentGateway</c> with NSubstitute — they prove
/// <c>AgentsMcpTools</c> calls the gateway correctly, never that the gateway itself draws the boundary correctly.
/// That boundary can only be exercised against a real <see cref="CockpitViewModel"/> and its real
/// <see cref="SessionPanelViewModel"/> collection, which is why this lives here rather than in the unit tests —
/// the same reasoning as <see cref="PluginActionsSessionNameTests"/>, and for the same mechanical reason: building
/// a <see cref="SessionViewModel"/> and touching its observable properties has to happen on Avalonia's UI thread,
/// or the dispatcher-bound plumbing underneath it never settles.
/// </summary>
[Collection("avalonia")]
public class WorkspaceAgentGatewayTests
{
    [Fact]
    public void GetWorkspaceSnapshot_TwoWorkspaces_OnlyReturnsTheCallersOwnWorkspaceMates()
    {
        var (gateway, deskA, deskB) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var sessionA = new SessionViewModel { WorkspaceId = "desk-a" };
            var sessionB = new SessionViewModel { WorkspaceId = "desk-b" };
            cockpit.Sessions.Add(sessionA);
            cockpit.Sessions.Add(sessionB);

            return (new WorkspaceAgentGateway(cockpit), sessionA, sessionB);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(deskA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal("desk-a", snapshot!.WorkspaceId);
        Assert.Single(snapshot.Panes);
        Assert.Equal(deskA.PaneId, snapshot.Panes[0].PaneId);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == deskB.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_TwoSessionsOnTheSameDesk_BothAppearInTheSnapshot()
    {
        var (gateway, sessionA, sessionB) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var a = new SessionViewModel { WorkspaceId = "shared-desk" };
            var b = new SessionViewModel { WorkspaceId = "shared-desk" };
            cockpit.Sessions.Add(a);
            cockpit.Sessions.Add(b);

            return (new WorkspaceAgentGateway(cockpit), a, b);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(sessionA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Panes.Count);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionA.PaneId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionB.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_UnknownPaneId_ReturnsNull()
    {
        var gateway = Dispatcher.UIThread.Invoke(() => new WorkspaceAgentGateway(new CockpitViewModel()));

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot("no-such-pane"));

        Assert.Null(snapshot);
    }

    /// <summary>
    /// Mirrors the fallback <see cref="CockpitViewModel.BelongsToActiveWorkspace"/> uses: a session with no
    /// workspace stamp (created before workspaces existed, or in the design-time graph) belongs to the first
    /// Sessions workspace rather than to none. Two unstamped sessions must therefore land on each other, and not
    /// on a session explicitly stamped to a different, real workspace — that consistency is the whole point of
    /// the fallback, not just that it resolves to *something*.
    /// </summary>
    [Fact]
    public void GetWorkspaceSnapshot_SessionsWithNoWorkspaceStamp_FallBackToTheFirstSessionsWorkspaceTogether()
    {
        var (gateway, unstampedA, unstampedB, stampedElsewhere, firstSessionsWorkspaceId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var firstSessionsWorkspace = cockpit.Workspaces.Settings.Workspaces.First(workspace => workspace.Type == WorkspaceType.Sessions);
            var otherDesk = Workspace.Create("Other desk", WorkspaceType.Sessions);
            cockpit.Workspaces.Settings = cockpit.Workspaces.Settings.WithWorkspace(otherDesk);

            // No WorkspaceId set at all: both keep the type's default (empty string).
            var a = new SessionViewModel();
            var b = new SessionViewModel();
            var elsewhere = new SessionViewModel { WorkspaceId = otherDesk.Id };
            cockpit.Sessions.Add(a);
            cockpit.Sessions.Add(b);
            cockpit.Sessions.Add(elsewhere);

            return (new WorkspaceAgentGateway(cockpit), a, b, elsewhere, firstSessionsWorkspace.Id);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(unstampedA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal(firstSessionsWorkspaceId, snapshot!.WorkspaceId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == unstampedA.PaneId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == unstampedB.PaneId);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == stampedElsewhere.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_APaneThatIsNotARealAgentSession_NeverAppearsAsANeighbour()
    {
        var (gateway, agentSession, plainTerminal) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var agent = new SessionViewModel { WorkspaceId = "desk-a" };
            var terminal = new SessionViewModel { WorkspaceId = "desk-a", ShowPluginHeaderItems = false };
            cockpit.Sessions.Add(agent);
            cockpit.Sessions.Add(terminal);

            return (new WorkspaceAgentGateway(cockpit), agent, terminal);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(agentSession.PaneId));

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Panes);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == plainTerminal.PaneId);
    }

    /// <summary>
    /// S-4: TtyLauncher stamps COCKPIT_PANE_ID/COCKPIT_MCP_KEY into every TTY pane, including a plain shell the
    /// operator started directly — so that pane can call an MCP tool even though it is not itself an agent
    /// session. It must not be able to enroll itself on a workspace's roster by doing so, so the caller itself is
    /// checked, not only filtered out of the sibling list.
    /// </summary>
    [Fact]
    public void GetWorkspaceSnapshot_CallerIsAPlainTerminalPane_Refuses()
    {
        var (gateway, terminal) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var terminal = new SessionViewModel { WorkspaceId = "desk-a", ShowPluginHeaderItems = false };
            cockpit.Sessions.Add(terminal);

            return (new WorkspaceAgentGateway(cockpit), terminal);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(terminal.PaneId));

        Assert.Null(snapshot);
    }

    /// <summary>
    /// S-3: an unstamped session's fallback ("the first Sessions workspace") has nothing to resolve to when no
    /// Sessions workspace exists at all — every desk closed, or a graph that only ever built a Projects overview.
    /// Reporting workspaceId="" there would describe a desk that is not on screen anywhere; refusing is the fix.
    /// </summary>
    [Fact]
    public void GetWorkspaceSnapshot_NoSessionsWorkspaceExists_Refuses()
    {
        var (gateway, unstamped) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            // Replace the settings outright with one holding only the fixed Projects overview — no Sessions desk
            // for an unstamped session to fall back to.
            cockpit.Workspaces.Settings = new WorkspaceSettings
            {
                Workspaces = [Workspace.Create("Projects", WorkspaceType.Projects)],
            };
            var session = new SessionViewModel();
            cockpit.Sessions.Add(session);

            return (new WorkspaceAgentGateway(cockpit), session);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(unstamped.PaneId));

        Assert.Null(snapshot);
    }

    /// <summary>
    /// MF-1: an embedded session (an Autopilot step, a plugin run) is a full agent session with its own MCP token,
    /// but the grid deliberately never lists it in <see cref="CockpitViewModel.Sessions"/> — it lives in the
    /// host's separate embedded-sessions table instead. It must still show up as a workspace neighbour, or an
    /// embedded agent is invisible both as a sibling and as a gap.
    /// </summary>
    [Fact]
    public void GetWorkspaceSnapshot_AnEmbeddedSession_AppearsAsANeighbour()
    {
        var (gateway, gridSession, embeddedPaneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _NewEmbeddingCapableCockpit();
            var grid = new SessionViewModel { WorkspaceId = "plugin-desk" };
            cockpit.Sessions.Add(grid);

            var embedded = cockpit.Embed("plugin-desk", new EmbeddedSessionRequest());

            return (new WorkspaceAgentGateway(cockpit), grid, embedded.PaneId);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(gridSession.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Panes.Count);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == embeddedPaneId);
    }

    /// <summary>
    /// S-1: a closed session must stop being remembered on the agent-presence roster, or the roster only grows for
    /// the app's lifetime. <see cref="CockpitViewModel.CloseSessionCommand"/> is the grid's own close path — this
    /// proves it actually calls <see cref="IWorkspaceAgentCoordinator.Forget"/> for the pane that closed, not just
    /// that the API exists.
    /// </summary>
    [Fact]
    public void CloseSession_ForgetsThePaneFromTheAgentCoordinator()
    {
        var coordinator = Substitute.For<IWorkspaceAgentCoordinator>();
        var (cockpit, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(coordinator);
            var s = new SessionViewModel();
            c.Sessions.Add(s);
            return (c, s);
        });

        Dispatcher.UIThread.Invoke(() => cockpit.CloseSessionCommand.ExecuteAsync(session).GetAwaiter().GetResult());

        coordinator.Received(1).Forget(session.PaneId);
    }

    // A CockpitViewModel wired enough for Embed(...) to work (it refuses outright without a session factory and a
    // profile store): substitutes for everything else, since these tests only exercise session placement, not any
    // of these stores' own behaviour. Mirrors Cockpit.Core.Tests.Voice.TestCockpit, which cannot be referenced
    // from this test project (a different assembly).
    private static CockpitViewModel _NewEmbeddingCapableCockpit(IWorkspaceAgentCoordinator? agentCoordinator = null)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            sessionProfileStore: Substitute.For<ISessionProfileStore>(),
            agentCoordinator: agentCoordinator);
    }
}
