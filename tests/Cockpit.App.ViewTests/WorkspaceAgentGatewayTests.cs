using System.Collections.Concurrent;
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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(deskA.PaneId).GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(sessionA.PaneId).GetAwaiter().GetResult());

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Panes.Count);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionA.PaneId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionB.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_UnknownPaneId_ReturnsNull()
    {
        var gateway = Dispatcher.UIThread.Invoke(() => new WorkspaceAgentGateway(new CockpitViewModel()));

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync("no-such-pane").GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(unstampedA.PaneId).GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(agentSession.PaneId).GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(terminal.PaneId).GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(unstamped.PaneId).GetAwaiter().GetResult());

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

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshotAsync(gridSession.PaneId).GetAwaiter().GetResult());

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

    /// <summary>
    /// S-2 (review round 2): <see cref="CloseSession_ForgetsThePaneFromTheAgentCoordinator"/> above proves the
    /// grid's own close path; the embedded half of that same wiring — <c>CockpitViewModel._TeardownEmbeddedSessionAsync</c>,
    /// which every embedded-session end path (a workspace closing, <see cref="Plugins.Abstractions.Workspaces.IEmbeddedSession.CloseAsync"/>,
    /// the session ending itself) funnels through — had no test covering it at all. Removing that call still left
    /// every other test in this suite green.
    /// </summary>
    [Fact]
    public void CloseEmbeddedSession_ForgetsThePaneFromTheAgentCoordinator()
    {
        var coordinator = Substitute.For<IWorkspaceAgentCoordinator>();
        var (cockpit, embedded) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(coordinator);
            var grid = new SessionViewModel { WorkspaceId = "plugin-desk" };
            c.Sessions.Add(grid);

            var e = c.Embed("plugin-desk", new EmbeddedSessionRequest());
            return (c, e);
        });

        Dispatcher.UIThread.Invoke(() => embedded.CloseAsync().GetAwaiter().GetResult());

        coordinator.Received(1).Forget(embedded.PaneId);
    }

    /// <summary>
    /// AC-393's entire expiry story: there is no heartbeat and no TTL, so a claim stops standing only because the
    /// closing pane's teardown drops it. This proves the grid's own close path actually calls
    /// <see cref="IAgentResourceClaims.Forget"/> — the sibling roster call one line above it has had that proof since
    /// AC-391, and without this one both new call sites could be deleted with every test still green.
    /// </summary>
    [Fact]
    public void CloseSession_ForgetsThePanesResourceClaims()
    {
        var claims = Substitute.For<IAgentResourceClaims>();
        var (cockpit, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(agentClaims: claims);
            var s = new SessionViewModel();
            c.Sessions.Add(s);
            return (c, s);
        });

        Dispatcher.UIThread.Invoke(() => cockpit.CloseSessionCommand.ExecuteAsync(session).GetAwaiter().GetResult());

        claims.Received(1).Forget(session.PaneId);
    }

    /// <summary>
    /// The embedded half of the same wiring, which for the roster was the half that had no test at all until it was
    /// caught in review. An embedded session ends through <c>_TeardownEmbeddedSessionAsync</c> — a workspace closing,
    /// <see cref="Plugins.Abstractions.Workspaces.IEmbeddedSession.CloseAsync"/>, or the session ending itself — and
    /// nothing else funnels through there.
    /// </summary>
    [Fact]
    public void CloseEmbeddedSession_ForgetsThePanesResourceClaims()
    {
        var claims = Substitute.For<IAgentResourceClaims>();
        var (cockpit, embedded) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(agentClaims: claims);
            var grid = new SessionViewModel { WorkspaceId = "plugin-desk" };
            c.Sessions.Add(grid);

            var e = c.Embed("plugin-desk", new EmbeddedSessionRequest());
            return (c, e);
        });

        Dispatcher.UIThread.Invoke(() => embedded.CloseAsync().GetAwaiter().GetResult());

        claims.Received(1).Forget(embedded.PaneId);
    }

    /// <summary>
    /// The teardown that drops a pane's claims runs after the session is disposed, and disposal is the step most
    /// likely to fail — it kills a CLI process or stops a tailer. Everything the host holds on a session's behalf (the
    /// terminal couplings, the roster entry, the unread inbox, the claims) lives outside the session object, so a
    /// dispose that throws must not be able to strand any of it for the life of the app.
    /// </summary>
    [Fact]
    public void CloseSession_WhenDisposingTheSessionThrows_StillForgetsWhatTheHostHeldForIt()
    {
        var claims = Substitute.For<IAgentResourceClaims>();
        var coordinator = Substitute.For<IWorkspaceAgentCoordinator>();
        var (cockpit, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(coordinator, claims);
            var s = new ThrowsOnDisposeSession();
            c.Sessions.Add(s);
            return (c, s);
        });

        Dispatcher.UIThread.Invoke(() => cockpit.CloseSessionCommand.ExecuteAsync(session).GetAwaiter().GetResult());

        Assert.True(session.DisposeAttempted);
        claims.Received(1).Forget(session.PaneId);
        coordinator.Received(1).Forget(session.PaneId);
    }

    /// <summary>
    /// The embedded half of the best-effort dispose above, where skipping the teardown costs more: this path runs
    /// fire-and-forget, so the exception lands in a task nobody observes and the claims would simply never be
    /// dropped, with nothing anywhere saying so.
    /// </summary>
    [Fact]
    public void CloseEmbeddedSession_WhenDisposingTheSessionThrows_StillForgetsWhatTheHostHeldForIt()
    {
        var claims = Substitute.For<IAgentResourceClaims>();
        var (cockpit, embedded) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewEmbeddingCapableCockpit(agentClaims: claims, sessionFactory: () => new ThrowsOnDisposeSession());
            var grid = new SessionViewModel { WorkspaceId = "plugin-desk" };
            c.Sessions.Add(grid);

            var e = c.Embed("plugin-desk", new EmbeddedSessionRequest());
            return (c, e);
        });

        Dispatcher.UIThread.Invoke(() => embedded.CloseAsync().GetAwaiter().GetResult());

        claims.Received(1).Forget(embedded.PaneId);
    }

    /// <summary>
    /// MF-1 (review round 2): <see cref="WorkspaceAgentGateway.GetWorkspaceSnapshotAsync"/> marshals onto the UI
    /// thread only when the caller is not already on it — but every other test above calls in from inside
    /// <see cref="Dispatcher.UIThread.Invoke(System.Action)"/>, so <c>CheckAccess()</c> is always true there and the
    /// marshal branch never actually runs. An MCP tool call lands on its own request thread instead, racing the UI
    /// thread's own mutation of <see cref="CockpitViewModel.Sessions"/> — an
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>, which is not thread-safe. This
    /// reproduces exactly that: real background threads (never through <c>Dispatcher.UIThread.Invoke</c>) read the
    /// snapshot while the UI thread adds and removes sibling sessions. Without the marshal it fails fast with
    /// <see cref="InvalidOperationException"/> ("Collection was modified").
    /// <para>
    /// Read this as a guard whose whole signal is in the <em>red</em> direction. A green run here does not show that
    /// concurrent reads were survived, because with the marshal in place there are none to survive: the churn below
    /// occupies the dispatcher for its entire run, so each reader's <c>InvokeAsync</c> queues behind it and only
    /// completes once the churn is over. That is the fix working — serialising the reads is exactly what it is for —
    /// but it means green is satisfied trivially. The test earns its place by going red the moment the marshal is
    /// removed, because an unmarshalled reader touches the collection on its own thread mid-mutation. Anyone changing
    /// the marshalling should re-run this with the change reverted and confirm it still fails; green alone proves
    /// nothing about this seam.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetWorkspaceSnapshotAsync_CalledFromBackgroundThreadsWhileTheUiThreadChurnsSessions_NeverThrows()
    {
        var (gateway, cockpit, callerPaneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var vm = new CockpitViewModel();
            var caller = new SessionViewModel { WorkspaceId = "desk-a" };
            vm.Sessions.Add(caller);
            return (new WorkspaceAgentGateway(vm), vm, caller.PaneId);
        });

        var stop = 0;
        var readerExceptions = new ConcurrentQueue<Exception>();

        // Real background threads — never through Dispatcher.UIThread.Invoke — the same calling convention an MCP
        // tool's own request thread uses. With the marshal in place these queue behind the churn rather than running
        // alongside it; with it removed they read the collection directly, which is what makes the mutation fail.
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    gateway.GetWorkspaceSnapshotAsync(callerPaneId).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    readerExceptions.Enqueue(exception);
                    return;
                }
            }
        })).ToArray();

        // The UI thread itself churns: adding and removing a sibling session on a tight loop. Bounded by an
        // iteration count rather than a sleep, so the test is not "hoping" a timing window lines up — 20,000
        // iterations of Add+Remove keep the collection in motion long enough that an unmarshalled reader is
        // overwhelmingly likely to land mid-mutation on any machine this runs on.
        try
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                for (var i = 0; i < 20_000; i++)
                {
                    var sibling = new SessionViewModel { WorkspaceId = "desk-a" };
                    cockpit.Sessions.Add(sibling);
                    cockpit.Sessions.Remove(sibling);
                }
            });
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Empty(readerExceptions);
    }

    // A CockpitViewModel wired enough for Embed(...) to work (it refuses outright without a session factory and a
    // profile store): substitutes for everything else, since these tests only exercise session placement, not any
    // of these stores' own behaviour. Mirrors Cockpit.Core.Tests.Voice.TestCockpit, which cannot be referenced
    // from this test project (a different assembly).
    private static CockpitViewModel _NewEmbeddingCapableCockpit(
        IWorkspaceAgentCoordinator? agentCoordinator = null,
        IAgentResourceClaims? agentClaims = null,
        Func<SessionViewModel>? sessionFactory = null)
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
            sessionFactory ?? (() => new SessionViewModel()),
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
            agentCoordinator: agentCoordinator,
            agentClaims: agentClaims);
    }

    /// <summary>
    /// A session whose kind-specific teardown fails. The failure comes from <c>DisposeCoreAsync</c> because that is
    /// where the real ones live — killing a CLI process, stopping a transcript tailer — and it is the only part of
    /// disposal a panel defines for itself. It derives from <see cref="SessionViewModel"/> rather than the panel base
    /// so the same failure can be driven through the embedded path, which builds its session from the factory.
    /// </summary>
    private sealed class ThrowsOnDisposeSession : SessionViewModel
    {
        public bool DisposeAttempted { get; private set; }

        protected override ValueTask DisposeCoreAsync()
        {
            DisposeAttempted = true;
            throw new InvalidOperationException("the CLI process would not die");
        }
    }

}
